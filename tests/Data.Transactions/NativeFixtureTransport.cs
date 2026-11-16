using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using NativeWeave.Data;
using NativeWeave.Database;

// This is a component test bridge, never a product provider or the B04 host.
// Blocking fixture entrypoints run on test workers while the product native
// coordinator owns its Tokio tasks. Actual provider composition is tested separately.
internal sealed class NativeFixtureTransport : ITransactionTransport
{
    private readonly FixtureHandle _handle;
    private readonly string? _recovery;
    private TransactionSnapshot _final;
    private readonly object _gate = new();
    private Task? _join;
    private int _calls;
    public int ActiveManagedCalls => Volatile.Read(ref _calls);
    public NativeFixtureTransport(int mode, TimeSpan? commitTimeout = null, TimeSpan? cleanupTimeout = null, string? recovery = null)
    {
        _handle = Native.Create((uint)mode, (ulong)(commitTimeout ?? TimeSpan.FromSeconds(30)).TotalMilliseconds, (ulong)(cleanupTimeout ?? TimeSpan.FromSeconds(5)).TotalMilliseconds);
        if (_handle.IsInvalid) throw new InvalidOperationException("Native fixture creation failed.");
        _recovery = recovery;
    }
    public DataTransaction Wrap() => new(this, _recovery);
    public TransactionSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                if (_handle.IsClosed) return _final;
                Native.Read(_handle, out var wire);
                return _final = wire.Snapshot;
            }
        }
    }
    public ulong[] NativeResources()
    {
        lock (_gate)
        {
            Native.Read(_handle, out var wire);
            return [wire.Lease, wire.Operations, wire.CleanupOwners, wire.Lifetime];
        }
    }
    public void Proceed() => Native.Proceed(_handle);
    public void FailCommand() => Throw(Native.FailCommand(_handle), default);
    public Task CommitAsync(CancellationToken admissionToken) => Run(() => Throw(Native.Finish(_handle, 1, admissionToken.IsCancellationRequested ? 1u : 0u), admissionToken));
    public Task RollbackAsync(CancellationToken admissionToken) => Run(() => Throw(Native.Finish(_handle, 0, admissionToken.IsCancellationRequested ? 1u : 0u), admissionToken));
    public async Task<SavepointId> CreateSavepointAsync(CancellationToken admissionToken)
    {
        ulong sequence = 0;
        await Run(() => Throw(Native.CreateSavepoint(_handle, admissionToken.IsCancellationRequested ? 1u : 0u, out sequence), admissionToken)).ConfigureAwait(false);
        Native.Read(_handle, out var wire);
        return new(wire.Generation, sequence);
    }
    public Task RollbackSavepointAsync(SavepointId id, CancellationToken admissionToken) => SavepointAction(id, 0, admissionToken);
    public Task ReleaseSavepointAsync(SavepointId id, CancellationToken admissionToken) => SavepointAction(id, 1, admissionToken);
    public async ValueTask DisposeSavepointAsync(SavepointId id)
    {
        // A failed automatic cleanup is retained in the native snapshot; it does
        // not replace an earlier business error through await using.
        uint status = 0;
        await Run(() => { status = Native.SavepointAction(_handle, id.Sequence, 2, 0); }).ConfigureAwait(false);
        if (status != 0) await JoinAsync().ConfigureAwait(false);
    }
    private Task SavepointAction(SavepointId id, uint action, CancellationToken token)
        => Run(() => Throw(Native.SavepointAction(_handle, id.Sequence, action, token.IsCancellationRequested ? 1u : 0u), token));
    public void RequestClose()
    {
        lock (_gate) { if (!_handle.IsClosed) Native.RequestClose(_handle); }
    }
    public async Task<DatabaseCloseResult> WaitCloseAsync(TimeSpan waitBudget, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Run(() => Native.WaitClose(_handle, checked((ulong)waitBudget.Ticks))).WaitAsync(cancellationToken).ConfigureAwait(false);
        return Snapshot.Cleanup;
    }
    public ValueTask JoinAsync()
    {
        lock (_gate) return new ValueTask(_join ??= JoinCoreAsync());
    }
    private async Task JoinCoreAsync()
    {
        await Run(() => Native.Join(_handle)).ConfigureAwait(false);
        while (Volatile.Read(ref _calls) != 0) await Task.Yield();
        lock (_gate)
        {
            _ = Snapshot;
            _handle.Dispose();
        }
    }
    private Task Run(Action action)
    {
        Interlocked.Increment(ref _calls);
        return Task.Run(() => { try { action(); } finally { Interlocked.Decrement(ref _calls); } });
    }
    private void Throw(uint code, CancellationToken token)
    {
        if (code == 0) return;
        var cleanup = Snapshot.Cleanup;
        throw code switch
        {
            2 => new OperationCanceledException(token),
            3 => new CommitOutcomeUnknownException("Commit confirmation is unavailable.", _recovery, cleanup.ProviderCode, cleanup.CorrelationId),
            4 => new DatabaseException("The provider confirmed transaction failure.", DatabaseErrorKind.Provider, cleanup.ProviderCode, cleanup.CorrelationId),
            5 => new DatabaseCleanupException("Rollback was not confirmed.", cleanup),
            _ => new DatabaseException("Transaction operation is invalid for this scope.", DatabaseErrorKind.InvalidState)
        };
    }
}
internal sealed class FixtureHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public FixtureHandle() : base(true) { }
    protected override bool ReleaseHandle() { Native.Free(handle); return true; }
}
[StructLayout(LayoutKind.Sequential)]
internal struct SnapshotWire
{
    public ulong State, Outcome, Attempted, Cleanup, Disposition, Released, Deadline, Correlation, ProviderCode, Generation, SessionId, Lease, Operations, CleanupOwners, Lifetime, Reserved;
    public readonly TransactionSnapshot Snapshot => new((TransactionState)State, (TransactionOutcome)Outcome, Attempted != 0,
        new((CleanupStatus)Cleanup, Correlation, ProviderCode == 0 ? null : "08006", Released != 0, (ConnectionDisposition)Disposition, Deadline != 0));
}
internal static partial class Native
{
    private const string Library = "nativeweave_transactions_fixture";
    [LibraryImport(Library, EntryPoint = "nw_tx_fixture_create")]
    internal static partial FixtureHandle Create(uint mode, ulong commitMs, ulong cleanupMs);
    [LibraryImport(Library, EntryPoint = "nw_tx_fixture_snapshot")]
    internal static partial void Read(FixtureHandle handle, out SnapshotWire output);
    [LibraryImport(Library, EntryPoint = "nw_tx_fixture_finish")]
    internal static partial uint Finish(FixtureHandle handle, uint commit, uint canceled);
    [LibraryImport(Library, EntryPoint = "nw_tx_fixture_request_close")]
    internal static partial void RequestClose(FixtureHandle handle);
    [LibraryImport(Library, EntryPoint = "nw_tx_fixture_join")]
    internal static partial void Join(FixtureHandle handle);
    [LibraryImport(Library, EntryPoint = "nw_tx_fixture_wait_close")]
    internal static partial void WaitClose(FixtureHandle handle, ulong ticks);
    [LibraryImport(Library, EntryPoint = "nw_tx_fixture_proceed")]
    internal static partial void Proceed(FixtureHandle handle);
    [LibraryImport(Library, EntryPoint = "nw_tx_fixture_fail_command")]
    internal static partial uint FailCommand(FixtureHandle handle);
    [LibraryImport(Library, EntryPoint = "nw_tx_fixture_savepoint_create")]
    internal static partial uint CreateSavepoint(FixtureHandle handle, uint canceled, out ulong sequence);
    [LibraryImport(Library, EntryPoint = "nw_tx_fixture_savepoint_action")]
    internal static partial uint SavepointAction(FixtureHandle handle, ulong sequence, uint action, uint canceled);
    [LibraryImport(Library, EntryPoint = "nw_tx_fixture_release")]
    internal static partial void Free(nint handle);
}
