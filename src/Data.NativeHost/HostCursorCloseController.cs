using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Korda.Interop;
using Korda.Interop.Abi;
using Korda.Interop.Callbacks;
using Korda.Query.Internal;

namespace Korda.Database;

internal enum HostCloseBoundary
{
    BeforeRoot, RootAllocated, BeforeReserve, Reserved, BeforeRequest, RequestCalled,
    RequestReturned, Ready, BeforeTake, ResultTaken, Published
}

internal readonly record struct HostCloseSnapshot(ulong Generation, bool SetupReturned,
    bool RequestEntered, bool RequestReturned, bool Accepted, bool Ready, bool Rooted,
    bool SlotReserved, int Controls, int CallbackEntries, bool ResultTaken, bool Published,
    bool FallbackStarted, bool FallbackSettled, bool Indeterminate, bool NativeSafe);

// A cursor has one final close request. Its callback root and completion are
// independent from a possibly active pull, including an indeterminate pull.
// Readiness detaches this notification; the copied cleanup receipt establishes
// cursor safety. Neither condition changes the generic operation quiet contract.
internal sealed class HostCursorCloseController : CoordinatedCallback
{
    private readonly object _gate = new();
    private readonly DataSource _source;
    private readonly DatabaseOwner _channel;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private GCHandle _root;
    private int _slot = -1, _controls, _callbackEntries;
    private bool _started, _setupReturned, _requestEntered, _requestReturned, _accepted, _ready;
    private bool _rooted, _takeStarted, _takeSettled, _resultTaken, _published, _publishing;
    private bool _indeterminate, _nativeSafe, _fallbackRequired, _fallbackStarted, _fallbackSettled;
    private bool _processing, _rescan;
    private CloseController? _fallback;
    private Exception? _failure, _rejection;
    private DatabaseCloseResult _last = new(CleanupStatus.NotStarted, 0, null, false, ConnectionDisposition.Usable, false);

    internal HostCursorCloseController(DataSource source, DatabaseOwner channel) : base(source.TestCloseScheduler)
    { _source = source; _channel = channel; }

    internal HostCloseSnapshot Snapshot
    {
        get
        {
            lock (_gate) return new(0, _setupReturned, _requestEntered, _requestReturned, _accepted,
                _ready, _rooted, _slot >= 0, _controls, _callbackEntries, _resultTaken, _published,
                _fallbackStarted, _fallbackSettled, _indeterminate, _nativeSafe);
        }
    }
    internal DatabaseCloseResult Cleanup { get { lock (_gate) return _last; } }

    internal Task JoinAsync()
    {
        bool start;
        lock (_gate)
        {
            start = !_started;
            if (start) { _started = true; _last = _last with { Status = CleanupStatus.Pending }; }
        }
        if (start) Request();
        return _completion.Task;
    }

    private unsafe void Request()
    {
        bool control = false;
        try
        {
            Boundary(HostCloseBoundary.BeforeRoot);
            _root = GCHandle.Alloc(this, GCHandleType.Normal);
            lock (_gate) _rooted = true;
            Boundary(HostCloseBoundary.RootAllocated);
            Boundary(HostCloseBoundary.BeforeReserve);
            try
            {
                var slot = _source.CallbackCoordinator.Reserve(this);
                lock (_gate) _slot = slot;
            }
            catch (InvalidOperationException rejection)
            {
                // Parent closure or exhausted coordinator capacity may reject
                // this new reservation. No direct native call has been made.
                lock (_gate) { _rejection = rejection; _fallbackRequired = true; }
                return;
            }
            Boundary(HostCloseBoundary.Reserved);
            Boundary(HostCloseBoundary.BeforeRequest);
            lock (_gate) { _controls++; control = true; }
            using var lease = _channel.Borrow(DatabaseOwnerKind.Cursor);
            NativePullCallbacks callbacks = new()
            {
                StructSize = (uint)sizeof(NativePullCallbacks),
                Context = GCHandle.ToIntPtr(_root),
                Ready = &OnReady
            };
            var error = BindingChecks.Error();
            lock (_gate) _requestEntered = true;
            var status = NativeHost.Pulls(_source.Binding).CloseRequest(lease.Pointer, &callbacks, &error);
            Boundary(HostCloseBoundary.RequestCalled);
            lock (_gate)
            {
                _requestReturned = true;
                _accepted = status == NativeStatus.Ok;
                if (!_accepted)
                {
                    _fallbackRequired = true;
                    _indeterminate |= _ready; // A definite rejection must have no callback.
                    if (_ready) _failure ??= new NativeEngineException("Cursor close rejected after publishing readiness.");
                }
            }
            if (status != NativeStatus.Ok)
            {
                var rejection = HostFailure.Create(_source.Binding, status, error);
                lock (_gate) _rejection = rejection;
            }
            Boundary(HostCloseBoundary.RequestReturned);
        }
        catch (Exception error)
        {
            lock (_gate)
            {
                _failure ??= error;
                _indeterminate |= _requestEntered && !_requestReturned;
                if (!_accepted) _fallbackRequired = true;
            }
        }
        finally
        {
            lock (_gate)
            {
                if (control) _controls--;
                _setupReturned = true;
            }
            Resume();
        }
    }

    private void Resume()
    {
        bool unreserved;
        lock (_gate) unreserved = _slot < 0 && _started && _setupReturned && !_published;
        if (!unreserved) { Signal(); return; }
        // A rejected reservation has no retry slot. Its setup and fallback
        // return paths must drain without depending on scheduler acceptance.
        try { Process(); }
        catch (Exception error) { SchedulingFault(error); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnReady(nint context, ulong generation)
    {
        HostCursorCloseController? controller = null;
        bool entered = false;
        try
        {
            controller = (HostCursorCloseController?)GCHandle.FromIntPtr(context).Target;
            if (controller is null) return;
            lock (controller._gate)
            {
                controller._callbackEntries++; entered = true;
                if (!controller._requestEntered || controller._published || controller._ready || generation != 0)
                {
                    controller._failure ??= new NativeEngineException("The cursor close callback is invalid or duplicated.");
                    controller._indeterminate = true;
                }
                else controller._ready = true;
            }
            controller.Boundary(HostCloseBoundary.Ready);
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
                // No cookie or controller access follows this gate release.
                lock (controller._gate) { controller._callbackEntries--; controller.Signal(); }
            }
        }
    }

    protected override void Process()
    {
        lock (_gate)
        {
            if (_processing) { _rescan = true; return; }
            _processing = true;
        }
        try
        {
            while (true)
            {
                lock (_gate) _rescan = false;
                ProcessCore();
                lock (_gate)
                {
                    if (_rescan && !_published) continue;
                    _processing = false;
                    return;
                }
            }
        }
        catch { lock (_gate) _processing = false; throw; }
    }

    private void ProcessCore()
    {
        bool fallback, poison;
        lock (_gate)
        {
            if (!_setupReturned || _published || _publishing || _controls != 0 || _callbackEntries != 0) return;
            poison = _indeterminate;
            fallback = _indeterminate || _fallbackRequired;
            if (!fallback && (!_accepted || !_ready)) return;
        }
        if (poison) _source.Poison();
        if (!fallback)
        {
            bool take;
            lock (_gate)
            {
                take = !_takeStarted;
                if (take) { _takeStarted = true; _controls++; }
            }
            if (take)
            {
                try
                {
                    using var lease = _channel.Borrow(DatabaseOwnerKind.Cursor);
                    Boundary(HostCloseBoundary.BeforeTake);
                    var result = NativeHost.TakePullClose(_source.Binding, lease.Pointer, _source.TestAfterErrorTake);
                    lock (_gate)
                    {
                        _last = result; _resultTaken = true; _nativeSafe = result.LocalResourcesReleased;
                        if (!_nativeSafe)
                            _failure ??= new DatabaseCleanupException("Native cursor close did not establish local safety.", result);
                    }
                    Boundary(HostCloseBoundary.ResultTaken);
                }
                catch (Exception error) { lock (_gate) _failure ??= error; }
                finally { lock (_gate) { _controls--; _takeSettled = true; } }
            }
            lock (_gate)
            {
                if (!_takeSettled) return;
                fallback = !_nativeSafe;
                if (fallback) _fallbackRequired = true;
            }
        }
        if (fallback)
        {
            StartFallback();
            lock (_gate)
            {
                if (!_fallbackSettled) return;
                // A failed fallback cannot discharge a request whose acceptance
                // or callback set is unknown. Keep its root and capacity charged.
                if (_indeterminate && !_nativeSafe) return;
            }
        }
        Publish();
    }

    private void StartFallback()
    {
        bool start, unaccepted;
        lock (_gate)
        {
            start = !_fallbackStarted;
            if (!start) return;
            _fallbackStarted = true;
            unaccepted = !_indeterminate && !_accepted;
        }
        // Release a definitely unused reservation before the generic bridge
        // requests its own slot. An unknown accepted callback keeps this root.
        if (unaccepted) ReleaseBridge();
        _ = FinishFallbackAsync();
    }

    private async Task FinishFallbackAsync()
    {
        try
        {
            // The generic public join and snapshot include the independent
            // close-notification fence. The direct close job joins only cursor
            // resources, so it never waits for its own notification to return.
            var fallback = new CloseController(_source, _channel, static () => { });
            lock (_gate) _fallback = fallback;
            await fallback.JoinAsync().ConfigureAwait(false);
            var result = fallback.Snapshot;
            lock (_gate) { _last = result; _nativeSafe = result.LocalResourcesReleased; }
        }
        catch (Exception error)
        {
            lock (_gate)
            {
                if (_failure is null) _failure = error;
                else
                {
                    try { _failure.Data["Korda.Database.CursorClose.v1"] = error; }
                    catch { /* Keep the original failure even if diagnostic attachment fails. */ }
                }
                _last = _last with { Status = CleanupStatus.Failed };
            }
            // A retirement observer can fail after real native safety. Only
            // the public snapshot, which also joins notification tails, may
            // recover that proof; completion or a ready bit alone cannot.
            try
            {
                CloseController? fallback;
                lock (_gate) fallback = _fallback;
                if (fallback is not null)
                {
                    var result = fallback.Snapshot;
                    if (result.LocalResourcesReleased)
                        lock (_gate) { _last = result; _nativeSafe = true; }
                }
            }
            catch { /* Unknown safety retains an indeterminate callback root. */ }
        }
        finally
        {
            lock (_gate) _fallbackSettled = true;
            Resume();
        }
    }

    private void ReleaseBridge()
    {
        int slot;
        lock (_gate)
        {
            if (_controls != 0 || _callbackEntries != 0)
                throw new InvalidOperationException("A cursor close bridge still has active accesses.");
            if (_rooted) { _root.Free(); _rooted = false; }
            slot = _slot; _slot = -1;
        }
        if (slot >= 0) _source.CallbackCoordinator.Release(slot, this);
    }

    private void Publish()
    {
        bool release;
        Exception? failure;
        lock (_gate)
        {
            if (_published || _publishing || !_setupReturned || _controls != 0 || _callbackEntries != 0) return;
            if (_indeterminate && !_nativeSafe) return;
            _publishing = true;
            release = _nativeSafe;
            failure = _failure;
            if (failure is null && !release)
                failure = _failure = _rejection ?? new DatabaseCleanupException("Native cursor cleanup remains unconfirmed.", _last);
        }
        if (release) _channel.Dispose();
        ReleaseBridge();
        CompleteScheduling();
        lock (_gate) _published = true;
        try
        {
            Boundary(HostCloseBoundary.Published);
            _source.TestCloseSnapshot?.Invoke(Snapshot);
        }
        catch (Exception error)
        {
            failure ??= error;
            lock (_gate) _failure ??= error;
        }
        if (failure is null) _completion.TrySetResult();
        else _completion.TrySetException(failure);
    }

    protected override void SchedulingFault(Exception error)
    {
        lock (_gate)
        {
            _failure ??= error;
            _indeterminate = true;
            _publishing = false;
        }
        _source.Poison();
        // Existing reservations retry through the coordinator. Without a slot,
        // only the still-running fallback return can provide new safety.
        Signal();
    }

    private void Boundary(HostCloseBoundary boundary) => _source.TestCloseBoundary?.Invoke(boundary);
}
