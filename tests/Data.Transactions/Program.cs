using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using NativeWeave.Data;
using NativeWeave.Database;
using NativeWeave.Samples.Transactions;

if (args.Contains("--aot") && RuntimeFeature.IsDynamicCodeSupported) throw new Exception("Actual AOT required.");
var families = new List<string>();
int assertions = 0;
void Check(bool condition, string message) { assertions++; if (!condition) throw new Exception(message); }
async Task<T> Throws<T>(Func<Task> operation) where T : Exception
{
    try { await operation(); } catch (T error) { assertions++; return error; }
    throw new Exception("Expected " + typeof(T).Name);
}
async Task WaitFor(Func<bool> predicate)
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    while (!predicate()) await Task.Delay(1, deadline.Token);
}
var options = new TransactionOptions();
Check(options.Isolation is null && options.RecoveryKey is null && !options.ReadOnly && options.CommitTimeout == TimeSpan.FromSeconds(30) && options.CleanupTimeout == TimeSpan.FromSeconds(5), "exact defaults");
var unknownValue = new CommitOutcomeUnknownException("safe", "recovery", "08006", 71);
Check(unknownValue.Outcome == TransactionOutcome.Unknown && !unknownValue.IsTransient && unknownValue.RecoveryKey == "recovery", "immutable unknown identity");
families.Add("canonical-values");
{
    var transport = new NativeFixtureTransport(0); var tx = transport.Wrap(); using var canceled = new CancellationTokenSource(); canceled.Cancel();
    await Throws<OperationCanceledException>(() => tx.CommitAsync(canceled.Token));
    Check(tx.State == TransactionState.Active && !tx.CommitAttempted && tx.Outcome == TransactionOutcome.InProgress, "before dispatch");
    await tx.DisposeAsync(); Check(tx.Cleanup.LocalResourcesReleased && tx.Outcome == TransactionOutcome.RolledBack, "automatic rollback join");
    tx.Dispose(); await tx.DisposeAsync();
    families.Add("predispatch-cancellation");
}
{
    var transport = new NativeFixtureTransport(3); var tx = transport.Wrap(); using var request = new CancellationTokenSource();
    var commit = tx.CommitAsync(request.Token); await WaitFor(() => tx.State == TransactionState.CommitInFlight); request.Cancel();
    await Throws<DatabaseException>(() => tx.CommitAsync()); transport.Proceed(); await commit;
    await tx.DisposeAsync(); Check(tx.Outcome == TransactionOutcome.Committed && tx.CommitAttempted, "postdispatch token ignored");
    families.Add("postdispatch-admission");
}
{
    var transport = new NativeFixtureTransport(1, recovery: "tenant-a/request-71"); var tx = transport.Wrap();
    var error = await Throws<CommitOutcomeUnknownException>(() => tx.CommitAsync());
    Check(error.RecoveryKey == tx.RecoveryKey && error.Outcome == TransactionOutcome.Unknown, "recovery identity");
    await tx.DisposeAsync(); Check(tx.Outcome == TransactionOutcome.Unknown && tx.Cleanup.LocalResourcesReleased, "cleanup cannot overwrite unknown");
    families.Add("unknown-cleanup");
}
{
    var transport = new NativeFixtureTransport(3, TimeSpan.FromMilliseconds(5)); var tx = transport.Wrap();
    await Throws<CommitOutcomeUnknownException>(() => tx.CommitAsync());
    var pending = await tx.CloseAsync(TimeSpan.Zero);
    Check(pending.Status == CleanupStatus.Pending && !pending.LocalResourcesReleased && pending.ConnectionDisposition == ConnectionDisposition.Quarantined, "bounded close retains owner");
    var resources = transport.NativeResources(); Check(resources[0] == 1 && resources[1] == 1 && resources[2] == 1 && resources[3] == 1, "native obligations retained");
    var join = tx.DisposeAsync().AsTask(); await Task.Delay(10); Check(!join.IsCompleted && transport.ActiveManagedCalls > 0, "managed join root retained");
    GC.Collect(); GC.WaitForPendingFinalizers(); transport.Proceed(); await join;
    Check(tx.Outcome == TransactionOutcome.Unknown && tx.Cleanup.LocalResourcesReleased, "late confirmation remains unknown");
    families.Add("deadline-and-eventual-retirement");
}
{
    var transport = new NativeFixtureTransport(0); var tx = transport.Wrap(); transport.FailCommand();
    await Throws<DatabaseException>(() => tx.CommitAsync()); Check(tx.State == TransactionState.Failed && !tx.CommitAttempted, "failed tx cannot commit");
    await tx.RollbackAsync(); await tx.DisposeAsync(); Check(tx.Outcome == TransactionOutcome.RolledBack, "explicit rollback");
    families.Add("failed-transaction");
}
{
    var transport = new NativeFixtureTransport(4); var tx = transport.Wrap();
    await Throws<DatabaseCleanupException>(() => tx.RollbackAsync()); await tx.DisposeAsync();
    Check(tx.State == TransactionState.Closed && tx.Outcome == TransactionOutcome.InProgress && !tx.CommitAttempted, "unconfirmed no commit");
    Check(tx.Cleanup.Status == CleanupStatus.RollbackUnconfirmed && tx.Cleanup.LocalResourcesReleased && tx.Cleanup.ConnectionDisposition == ConnectionDisposition.Discarded, "unconfirmed cleanup independent safety");
    families.Add("rollback-unconfirmed");
}
{
    var transport = new NativeFixtureTransport(0); var tx = transport.Wrap();
    var outer = await tx.CreateSavepointAsync(); var inner = await tx.CreateSavepointAsync();
    await Throws<DatabaseException>(() => outer.ReleaseAsync()); transport.FailCommand();
    await inner.RollbackAsync(); Check(tx.State == TransactionState.Active, "savepoint recovery");
    await inner.ReleaseAsync(); await inner.DisposeAsync(); await outer.DisposeAsync();
    Check(tx.State == TransactionState.Active && !tx.CommitAttempted, "caller-owned outer preserved");
    await TransactionExamples.RollBackSavepointAsync(tx); await tx.DisposeAsync();
    families.Add("savepoint-lifo-and-recovery");
}
{
    var transport = new NativeFixtureTransport(0); var tx = transport.Wrap();
    var outer = await tx.CreateSavepointAsync(); _ = await tx.CreateSavepointAsync();
    await outer.DisposeAsync(); Check(tx.State == TransactionState.Closed && tx.Cleanup.Status == CleanupStatus.Failed, "nontop closes admission");
    families.Add("nontop-disposal");
}
{
    var transport = new NativeFixtureTransport(5, cleanupTimeout: TimeSpan.FromMilliseconds(5)); var tx = transport.Wrap();
    await tx.CommitAsync(); var pending = await tx.CloseAsync(TimeSpan.FromMilliseconds(20));
    Check(pending.Status == CleanupStatus.Pending && !pending.LocalResourcesReleased && pending.DeadlineExceeded, "queued close not release");
    using var canceled = new CancellationTokenSource(); canceled.Cancel();
    await Throws<OperationCanceledException>(() => tx.CloseAsync(TimeSpan.Zero, canceled.Token));
    transport.Proceed(); await tx.DisposeAsync(); Check(tx.Cleanup.LocalResourcesReleased, "close canceled waiter still cleans");
    families.Add("close-wait-cancellation");
}
{
    var transport = new NativeFixtureTransport(0); await TransactionExamples.CommitAsync(transport.Wrap(), default);
    var loss = new NativeFixtureTransport(1, recovery: "example-recovery"); var tx = loss.Wrap();
    Check(await TransactionExamples.CommitWithBoundedCloseAsync(tx, default) == "example-recovery", "advanced recovery consumer"); await tx.DisposeAsync();
    families.Add("minimal-and-advanced-consumers");
}
Console.WriteLine(JsonSerializer.Serialize(new Report("passed", families, assertions, true, RuntimeFeature.IsDynamicCodeSupported ? "jit" : "native-aot", "controlled-component-only", false), ReportContext.Default.Report));

internal sealed record Report(string Status, List<string> Families, int Assertions, bool NativeCoordinator, string Mode, string ProviderFixture, bool AssembledHostAcceptance);
[JsonSerializable(typeof(Report))]
internal partial class ReportContext : JsonSerializerContext { }
