using System.Runtime.CompilerServices;

if (RuntimeFeature.IsDynamicCodeSupported) throw new InvalidOperationException("Scheduler verification requires Native AOT.");
static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
static void Reconcile(OfferedReceipt value)
{
    Require(value.Planned == value.Attempted && value.Attempted == value.Sent + value.Rejected,
        "Planned or rejected operations disappeared.");
    Require(value.Sent == value.Started && value.Completed == value.Succeeded + value.Failed && value.Started == value.Completed,
        "The queue did not drain or failed operations disappeared.");
    Require(value.SuccessServiceHistogram.Sum() == value.Succeeded && value.SuccessScheduledHistogram.Sum() == value.Succeeded
        && value.AllCompletedScheduledHistogram.Sum() == value.Completed && value.StartDelayHistogram.Sum() == value.Started,
        "Latency histograms omit observed operations.");
    Require(value.Seconds.Sum(s => s.Planned) == value.Planned && value.Seconds.Sum(s => s.Sent) == value.Sent
        && value.Seconds.Sum(s => s.Succeeded) == value.Succeeded && value.Seconds.Sum(s => s.Failed) == value.Failed
        && value.Seconds.Sum(s => s.Rejected) == value.Rejected && value.Seconds.Sum(s => s.Rows) == value.Rows,
        "Time series does not reconcile with cumulative counts.");
    Require(!value.TimeSeriesTruncated && value.OfferedEndTicks >= value.DeadlineTicks && value.Retries == 0,
        "Effective offered interval was shortened or retries were hidden.");
}
static async Task<OfferedReceipt> Run(double seconds, double rate, int capacity,
    params Func<long, ValueTask<OperationResult>>[] workers)
{
    using var schedule = new OfferedLoad(seconds, rate, capacity, workers);
    schedule.Start();
    await schedule.Completion;
    var receipt = schedule.Snapshot(); Reconcile(receipt);
    var rejectedRestart = false;
    try { schedule.Start(); } catch (InvalidOperationException) { rejectedRestart = true; }
    Require(rejectedRestart, "A used schedule admitted a second producer.");
    return receipt;
}

var immediate = await Run(.055, 100, 16, i => ValueTask.FromResult(new OperationResult(3, (ulong)i, 0)));
Require(immediate.Planned == 6 && immediate.Succeeded == 6 && immediate.Failed == 0 && immediate.Rejected == 0 && immediate.Rows == 18,
    "Fractional offered interval or immediate completion accounting is wrong.");
Console.WriteLine("PASS immediate completion and fractional arrival count");

static async ValueTask<OperationResult> SometimesFails(long index)
{
    await Task.Delay(2);
    if (index % 4 == 0) throw new OperationCanceledException("Known scheduler test failure.");
    return new(2, (ulong)index, 1);
}
var failures = await Run(.2, 100, 64, SometimesFails, SometimesFails);
Require(failures.Planned == 20 && failures.Succeeded == 15 && failures.Failed == 5 && failures.Rejected == 0
    && failures.ErrorClasses.Values.Sum() == 5 && failures.Rows == 30 && failures.Suspensions == 15
    && failures.OracleFailures == 0 && failures.FirstOracleFailure is null,
    "Asynchronous failures were omitted, retried or counted as successes.");
Console.WriteLine("PASS true suspension, failure categories and failed latency accounting");

static async ValueTask<OperationResult> Slow(long index) { await Task.Delay(5); return new(1, (ulong)index, 1); }
var overloaded = await Run(.03, 10000, 1, Slow);
Require(overloaded.Planned == 300 && overloaded.Rejected > 0 && overloaded.Succeeded > 0 && overloaded.Failed == 0,
    "Bounded overload did not expose rejected arrivals.");
Console.WriteLine("PASS bounded queue overload and complete drain");

foreach (var bad in new[] { double.NaN, double.PositiveInfinity, 0d, -1d })
{
    var rejected = false;
    try { using var unused = new OfferedLoad(.01, bad, 1, [i => ValueTask.FromResult(new OperationResult(0, 0, 0))]); }
    catch (ArgumentOutOfRangeException) { rejected = true; }
    Require(rejected, "Non-finite or nonpositive arrival rate was accepted.");
}
Console.WriteLine("PASS invalid arrival settings rejected before worker creation");

CorrectnessOracle.SetPhase("oracle-negative-control");
var masked = await Run(.03, 100, 8, index =>
{
    if (index == 0)
    {
        try { throw CorrectnessOracle.Failure("Injected result mismatch before retirement.", "injected"); }
        finally { throw new IOException("Injected later retirement failure."); }
    }
    return ValueTask.FromResult(new OperationResult(1, (ulong)index, 0));
});
Require(masked.Failed == 1 && masked.Succeeded == 2 && masked.OracleFailuresBefore == 0
    && masked.OracleFailuresAfter == 1 && masked.OracleFailures == 1
    && masked.FirstOracleFailure?.Phase == "oracle-negative-control"
    && masked.FirstOracleFailure.Message == "Injected result mismatch before retirement.",
    "A later cleanup error hid the earlier result mismatch or changed the sticky oracle boundary.");
CorrectnessOracle.SetPhase("subsequent-recovery-control");
var afterOracle = await Run(.02, 100, 8, index => ValueTask.FromResult(new OperationResult(1, (ulong)index, 0)));
Require(afterOracle.Failed == 0 && afterOracle.OracleFailuresBefore == 1 && afterOracle.OracleFailuresAfter == 1
    && afterOracle.OracleFailures == 1 && ReferenceEquals(afterOracle.FirstOracleFailure, masked.FirstOracleFailure),
    "An independently successful recovery phase reset the process-lifetime failure evidence.");
var refused = false;
try { CorrectnessOracle.RequireClean(); } catch (InvalidOperationException) { refused = true; }
Require(refused, "A prior oracle violation was silently admitted after recovery.");
Console.WriteLine("PASS sticky result oracle survives masked cleanup and later successful phases; ordinary cancellation remains separate");

ReaderIntegrityTests.Run();
