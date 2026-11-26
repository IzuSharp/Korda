using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks.Sources;
using Korda.Data;
using Korda.Database.Internal;
using Korda.Interop;
using Korda.Interop.Abi;
using Korda.Interop.Callbacks;
using Korda.Query.Internal;

namespace Korda.Database;

internal enum HostPullBoundary
{
    BeforeRoot, RootAllocated, BeforeRegister, Registered, BeforeRequest,
    RequestReturned, Ready, BeforeTake, ResultTaken, StopEntered, StopReturned, Published
}

internal readonly record struct HostPullSnapshot(ulong Generation, bool Active, bool SetupReturned,
    bool Accepted, bool Ready, bool Rooted, bool SlotReserved, int CallbackEntries, int Controls,
    bool RegistrationDrained, bool ResultTaken, bool Published, bool Consumed, bool StopObserved);

// One work item and one ValueTask source per channel. Only an active request
// owns a callback root/reservation; an idle, abandoned stream is not self-rooted.
internal sealed class HostPullController : CoordinatedCallback, IValueTaskSource<ScopedStreamBatch>
{
    private readonly object _gate = new();
    private readonly DataSource _source;
    private readonly DatabaseOwner _channel;
    private readonly ScopedStreamBatch _batch;
    private readonly CloseController _close;
    private readonly CancellationToken _token;
    private readonly int _rows;
    private ManualResetValueTaskSourceCore<ScopedStreamBatch> _completion = new() { RunContinuationsAsynchronously = true };
    private GCHandle _root;
    private CancellationTokenRegistration _registration;
    private int _slot = -1, _controls, _callbackEntries;
    private ulong _generation;
    private bool _active, _setupReturned, _accepted, _requestEntered, _requestReturned;
    private bool _ready, _rooted, _hasRegistration, _registrationAttempted, _registrationReady;
    private bool _drainStarted, _registrationDrained, _takeSettled, _resultTaken, _published, _consumed = true;
    private bool _stopping, _stopObserved, _closed, _forcedCloseStarted, _indeterminate;
    private Exception? _failure, _stopFailure;

    internal HostPullController(DataSource source, DatabaseOwner channel, ScopedStreamBatch batch,
        CloseController close, int rows, CancellationToken token) : base(null)
    {
        _source = source; _channel = channel; _batch = batch; _close = close;
        _rows = rows; _token = token;
    }

    internal HostPullSnapshot Snapshot
    {
        get
        {
            lock (_gate) return new(_generation, _active, _setupReturned, _accepted, _ready, _rooted,
                _slot >= 0, _callbackEntries, _controls, _registrationDrained, _resultTaken,
                _published, _consumed, _stopObserved);
        }
    }
    internal Exception? StopFailure { get { lock (_gate) return _stopFailure; } }

    internal unsafe ValueTask<ScopedStreamBatch> Request()
    {
        _source.CheckOpen();
        _token.ThrowIfCancellationRequested();
        short version;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed || _stopping, this);
            if (_active || !_consumed || !_batch.CanAttach)
                throw new DatabaseException("The preceding cursor pull has not been consumed and released.", DatabaseErrorKind.ConcurrentUse);
            _generation = checked(_generation + 1);
            _completion.Reset(); version = _completion.Version;
            _active = true; _consumed = false;
            _setupReturned = _accepted = _requestEntered = _requestReturned = _ready = false;
            _hasRegistration = _registrationAttempted = _registrationReady = false;
            _drainStarted = _registrationDrained = _takeSettled = _resultTaken = _published = _indeterminate = false;
            _failure = null;
        }
        bool control = false;
        try
        {
            Boundary(HostPullBoundary.BeforeRoot);
            _root = GCHandle.Alloc(this, GCHandleType.Normal);
            lock (_gate) _rooted = true;
            Boundary(HostPullBoundary.RootAllocated);
            _slot = _source.CallbackCoordinator.Reserve(this);
            Boundary(HostPullBoundary.BeforeRegister);
            if (_token.CanBeCanceled)
            {
                lock (_gate) _registrationAttempted = true;
                var registration = _token.UnsafeRegister(static state => ((HostPullController)state!).Cancel(), this);
                lock (_gate)
                {
                    _registration = registration; _hasRegistration = true; _registrationReady = true;
                }
            }
            else
            {
                lock (_gate) { _registrationReady = true; _registrationDrained = true; }
            }
            Boundary(HostPullBoundary.Registered);
            Boundary(HostPullBoundary.BeforeRequest);
            lock (_gate)
            {
                if (_token.IsCancellationRequested || _stopping)
                {
                    _failure ??= _token.IsCancellationRequested ? new OperationCanceledException(_token)
                        : new ObjectDisposedException(nameof(HostPullController));
                }
                else { _controls++; control = true; }
            }
            if (control)
            {
                using var lease = _channel.Borrow(DatabaseOwnerKind.Cursor);
                NativePullCallbacks callbacks = new()
                {
                    StructSize = (uint)sizeof(NativePullCallbacks),
                    Context = GCHandle.ToIntPtr(_root),
                    Ready = &OnReady
                };
                var error = BindingChecks.Error();
                lock (_gate) _requestEntered = true;
                var status = NativeHost.Pulls(_source.Binding).Request(lease.Pointer, _generation,
                    checked((uint)_rows), checked((ulong)_source.Limits.MaxBatchBytes), &callbacks, &error);
                lock (_gate)
                {
                    _requestReturned = true;
                    _accepted = status == NativeStatus.Ok;
                    if (!_accepted)
                    {
                        _failure ??= HostFailure.Create(_source.Binding, status, error, token: _token);
                        _indeterminate = _ready; // Rejection must have no callback.
                    }
                }
                Boundary(HostPullBoundary.RequestReturned);
            }
        }
        catch (Exception error)
        {
            lock (_gate)
            {
                _failure ??= error;
                // A throwing registration did not return a drainable handle;
                // retain that unknown managed obligation just like a native
                // request which threw before reporting acceptance.
                _indeterminate |= (_registrationAttempted && !_registrationReady)
                    || (_requestEntered && !_requestReturned);
            }
        }
        finally
        {
            lock (_gate)
            {
                if (control) _controls--;
                if (!_registrationAttempted && !_registrationReady)
                { _registrationReady = true; _registrationDrained = true; }
                _setupReturned = true;
            }
            // A pre-reservation failure has no native/cancellation obligation
            // and needs no scheduler capacity in order to release its root.
            if (_slot < 0) Process(); else Signal();
        }
        return new(this, version);
    }

    private void Cancel() => RequestStop();

    internal unsafe void RequestStop()
    {
        lock (_gate)
        {
            if (_stopping || _closed) return;
            _stopping = true; _controls++;
        }
        try
        {
            Boundary(HostPullBoundary.StopEntered);
            using var lease = _channel.Borrow(DatabaseOwnerKind.Cursor);
            var status = NativeHost.Pulls(_source.Binding).Stop(lease.Pointer);
            if (status != NativeStatus.Ok)
                throw HostFailure.Create(_source.Binding, status, BindingChecks.Error(), token: _token);
            lock (_gate) _stopObserved = true;
            Boundary(HostPullBoundary.StopReturned);
            _source.TestPullSnapshot?.Invoke(Snapshot);
        }
        catch (Exception error)
        {
            lock (_gate) { _failure ??= error; _stopFailure ??= error; _indeterminate = true; }
            _source.Poison();
        }
        finally
        {
            lock (_gate) { _controls--; Signal(); }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnReady(nint context, ulong generation)
    {
        HostPullController? controller = null;
        bool entered = false;
        try
        {
            controller = (HostPullController?)GCHandle.FromIntPtr(context).Target;
            if (controller is null) return;
            lock (controller._gate)
            {
                controller._callbackEntries++; entered = true;
                if (!controller._active || controller._published || generation != controller._generation || controller._ready)
                {
                    controller._failure ??= new NativeEngineException("The cursor pull callback generation is invalid or duplicated.");
                    controller._indeterminate = true;
                }
                else controller._ready = true;
            }
            controller.Boundary(HostPullBoundary.Ready);
        }
        catch (Exception error)
        {
            if (controller is not null)
                lock (controller._gate) controller._failure ??= error;
        }
        finally
        {
            if (controller is not null && entered)
            {
                // The drain cannot observe zero accesses until this callback
                // has made its last state access under the same gate.
                lock (controller._gate) { controller._callbackEntries--; controller.Signal(); }
            }
        }
    }

    protected override void Process()
    {
        bool forceClose, waitReady, stop;
        lock (_gate)
        {
            if (!_active || _published || !_setupReturned) return;
            forceClose = _indeterminate && !_closed;
            waitReady = _accepted && !_ready;
            stop = waitReady && _failure is not null && !_stopping;
        }
        if (forceClose)
        {
            _source.Poison();
            bool start;
            lock (_gate) { start = !_forcedCloseStarted; _forcedCloseStarted = true; }
            if (start) _ = ForceCloseAsync();
            return;
        }
        if (stop) RequestStop();
        if (waitReady) return;

        bool drain;
        lock (_gate)
        {
            if (!_registrationReady) return;
            drain = _hasRegistration && !_drainStarted;
            if (drain) _drainStarted = true;
        }
        if (drain)
        {
            var pending = _registration.DisposeAsync();
            if (pending.IsCompletedSuccessfully)
            {
                pending.GetAwaiter().GetResult();
                lock (_gate) { _registration = default; _registrationDrained = true; }
            }
            else _ = DrainRegistrationAsync(pending);
        }
        bool take;
        lock (_gate)
        {
            if (!_registrationDrained || _controls != 0 || _callbackEntries != 0) return;
            take = _accepted && !_closed && !_takeSettled;
            if (take) _controls++;
        }
        if (take)
        {
            try
            {
                using var lease = _channel.Borrow(DatabaseOwnerKind.Cursor);
                Boundary(HostPullBoundary.BeforeTake);
                NativeHost.TakePullBatch(_source.Binding, lease.Pointer, _generation, _batch,
                    checked((int)_source.Limits.MaxBatchBytes), _token, _source.TestAfterErrorTake);
                lock (_gate) _resultTaken = true;
                Boundary(HostPullBoundary.ResultTaken);
            }
            catch (Exception error) { lock (_gate) _failure ??= error; }
            finally { lock (_gate) { _controls--; _takeSettled = true; } }
        }
        Exception? failure;
        lock (_gate)
        {
            // A pre-reservation setup failure can drain synchronously while a
            // concurrent stop has already queued this same work item.
            if (_published) return;
            if (_controls != 0 || _callbackEntries != 0) { Signal(); return; }
            failure = _failure;
            if (failure is null && !_resultTaken)
                failure = _failure = new ObjectDisposedException(nameof(HostPullController));
            if (failure is not null) _stopping = true;
            if (_rooted) { _root.Free(); _rooted = false; }
            if (_slot >= 0) { _source.CallbackCoordinator.Release(_slot, this); _slot = -1; }
            _published = true;
        }
        if (failure is not null) _batch.Dispose();
        try
        {
            Boundary(HostPullBoundary.Published);
            _source.TestPullSnapshot?.Invoke(Snapshot);
        }
        catch (Exception error)
        {
            failure ??= error;
            lock (_gate) { _failure ??= error; _stopping = true; }
            _batch.Dispose();
        }
        // No native owner, callback cookie, registration or request control is
        // touched after publication. A continuation may consume and reset now.
        if (failure is null) _completion.SetResult(_batch);
        else _completion.SetException(failure);
    }

    private async Task DrainRegistrationAsync(ValueTask pending)
    {
        try
        {
            await pending.ConfigureAwait(false);
            lock (_gate) { _registration = default; _registrationDrained = true; }
        }
        catch (Exception error) { SchedulingFault(error); }
        finally { Signal(); }
    }

    private async Task ForceCloseAsync()
    {
        try
        {
            RequestStop();
            await _close.JoinAsync().ConfigureAwait(false);
            lock (_gate) { _closed = true; _accepted = false; _indeterminate = false; }
        }
        catch (Exception error)
        {
            // Unknown native safety retains the callback root/reservation.
            lock (_gate) _failure ??= error;
            _source.Poison();
        }
        finally { Signal(); }
    }

    internal void Closed()
    {
        bool complete;
        lock (_gate)
        {
            _closed = true;
            complete = !_active || _published;
            if (!complete)
            {
                _accepted = false; _indeterminate = false;
                _failure ??= new ObjectDisposedException(nameof(HostPullController));
            }
        }
        if (complete) CompleteScheduling(); else Signal();
    }

    protected override void SchedulingFault(Exception error)
    {
        lock (_gate) { _failure ??= error; _indeterminate = true; }
        _source.Poison();
        Signal();
    }

    private void Boundary(HostPullBoundary boundary) => _source.TestPullBoundary?.Invoke(boundary);

    ScopedStreamBatch IValueTaskSource<ScopedStreamBatch>.GetResult(short token)
    {
        lock (_gate)
        {
            if (token != _completion.Version || !_published || _consumed)
                throw new InvalidOperationException("A cursor pull result is consumed once after completion.");
            _consumed = true;
        }
        try { return _completion.GetResult(token); }
        finally
        {
            lock (_gate) _active = false;
        }
    }
    ValueTaskSourceStatus IValueTaskSource<ScopedStreamBatch>.GetStatus(short token) => _completion.GetStatus(token);
    void IValueTaskSource<ScopedStreamBatch>.OnCompleted(Action<object?> continuation, object? state, short token,
        ValueTaskSourceOnCompletedFlags flags) => _completion.OnCompleted(continuation, state, token, flags);
}
