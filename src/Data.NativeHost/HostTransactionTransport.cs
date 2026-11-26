using Korda.Data;
using Korda.Query.Internal;

namespace Korda.Database;

internal sealed class HostTransactionTransport : ITransactionTransport
{
    private readonly DataSource _source;
    private readonly DatabaseOwner _owner;
    private readonly CloseController _close;
    private readonly string? _recoveryKey;
    private readonly object _gate = new();
    private readonly Dictionary<SavepointId, DatabaseOwner> _savepoints = [];
    private TransactionSnapshot _last;
    private ulong _sequence;
    private bool _closed;
    internal HostTransactionTransport(DataSource source, DatabaseOwner owner, string? recoveryKey)
    {
        _source = source; _owner = owner; _recoveryKey = recoveryKey; _last = NativeHost.Snapshot(owner);
        _close = new(source, owner, () => { lock (_gate) { _last = NativeHost.Snapshot(_owner); foreach (var savepoint in _savepoints.Values) savepoint.Dispose(); _savepoints.Clear(); _owner.Dispose(); } });
    }
    public TransactionSnapshot Snapshot
    { get { lock (_gate) { if (!_owner.IsClosed) _last = NativeHost.Snapshot(_owner); return _last; } } }
    private void Admit(CancellationToken token)
    {
        lock (_gate) ObjectDisposedException.ThrowIf(_closed, this);
        _source.CheckOpen(); token.ThrowIfCancellationRequested();
    }
    public Task CommitAsync(CancellationToken admissionToken)
    {
        try { Admit(admissionToken); } catch (OperationCanceledException) { return Task.FromCanceled(admissionToken); }
        return Finish(0, admissionToken);
    }
    public Task RollbackAsync(CancellationToken admissionToken)
    {
        try { Admit(admissionToken); } catch (OperationCanceledException) { return Task.FromCanceled(admissionToken); }
        return Finish(1, admissionToken);
    }
    private async Task Finish(uint action, CancellationToken token)
    {
        try { await _source.SubmitCommand(NativeCommandKind.FinishTransaction, _owner, null, default, action, 101, NativeCommand.Snapshot, token).ConfigureAwait(false); }
        catch (CommitOutcomeUnknownException failure) { throw new CommitOutcomeUnknownException(failure.Message, _recoveryKey, failure.ProviderCode, failure.CorrelationId); }
        catch (DatabaseException failure) when (action == 1) { throw new DatabaseCleanupException("The native rollback was not confirmed.", Snapshot.Cleanup) { Data = { ["Korda.Database.Primary.v1"] = failure } }; }
        finally { lock (_gate) if (!_owner.IsClosed) _last = NativeHost.Snapshot(_owner); }
    }
    public async Task<SavepointId> CreateSavepointAsync(CancellationToken admissionToken)
    {
        Admit(admissionToken);
        var owner = await _source.SubmitCommand(NativeCommandKind.BeginSavepoint, _owner, null, default, 0, 4, NativeCommand.Owner, admissionToken).ConfigureAwait(false);
        lock (_gate)
        {
            try { var id = new SavepointId(1, checked(++_sequence)); _savepoints.Add(id, owner); return id; }
            catch { owner.Dispose(); throw; }
        }
    }
    private DatabaseOwner Get(SavepointId id)
    { lock (_gate) return _savepoints.TryGetValue(id, out var owner) ? owner : throw new DatabaseException("The savepoint is stale or already released.", DatabaseErrorKind.InvalidState); }
    public Task RollbackSavepointAsync(SavepointId id, CancellationToken admissionToken) => Action(id, 0, admissionToken);
    public Task ReleaseSavepointAsync(SavepointId id, CancellationToken admissionToken) => Action(id, 1, admissionToken);
    private async Task Action(SavepointId id, uint action, CancellationToken token)
    {
        Admit(token); var owner = Get(id);
        var snapshot = await _source.SubmitCommand(NativeCommandKind.SavepointAction, owner, null, default, action, 101, NativeCommand.Snapshot, token).ConfigureAwait(false);
        lock (_gate) _last = snapshot;
        if (action == 1) { lock (_gate) _savepoints.Remove(id); owner.Dispose(); }
    }
    public ValueTask DisposeSavepointAsync(SavepointId id)
    {
        DatabaseOwner? owner; lock (_gate) { if (!_savepoints.TryGetValue(id, out owner)) return ValueTask.CompletedTask; }
        return new(DisposeSavepoint(id, owner));
    }
    private async Task DisposeSavepoint(SavepointId id, DatabaseOwner owner)
    {
        try { await _source.CloseOwner(owner).ConfigureAwait(false); }
        finally { lock (_gate) _savepoints.Remove(id); owner.Dispose(); }
    }
    public void RequestClose() { lock (_gate) _closed = true; _close.Request(); }
    public Task<DatabaseCloseResult> WaitCloseAsync(TimeSpan waitBudget, CancellationToken cancellationToken)
    { HostEncoding.WaitBudget(waitBudget); RequestClose(); return _close.WaitAsync(waitBudget, cancellationToken); }
    public ValueTask JoinAsync() { RequestClose(); return new(_close.JoinAsync()); }
}
