using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Korda.Interop;
using Korda.Interop.Abi;
using Korda.Interop.Callbacks;
using Korda.Query.Internal;

namespace Korda.Database;

internal enum HostCommandBoundary
{
    BeforeRoot, RootAllocated, BeforeReserve, Reserved, BeforeRegister, Registered,
    BeforeRequest, RequestCalled, RequestReturned, Ready, BeforeTake, ResultTaken,
    CancelEntered, CancelReturned, Published
}

internal readonly record struct HostCommandSnapshot(uint RequestKind, uint ExpectedKind, bool SetupReturned,
    bool RequestEntered, bool RequestReturned, bool Accepted, bool Ready, bool Rooted, bool SlotReserved,
    int Controls, int CallbackEntries, bool RegistrationDrained, bool ResultTaken, bool Published,
    bool FallbackStarted, bool FallbackSettled, bool Indeterminate, bool NativeSafe);

// A command has one result and one detached notification. Ready releases its
// execution accessors; only an indeterminate bridge uses the generic quiet join.
// The work item never runs business continuations on the unmanaged callback.
internal abstract class HostCommandCallback(ICallbackScheduler? scheduler) : CoordinatedCallback(scheduler)
{
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static void OnReady(nint context, ulong generation)
    {
        try { (GCHandle.FromIntPtr(context).Target as HostCommandCallback)?.NotifyReady(generation); }
        catch { /* A foreign cookie cannot be repaired; never unwind across the ABI. */ }
    }
    protected abstract void NotifyReady(ulong generation);
}

internal sealed class HostCommandController<T> : HostCommandCallback
{
    private readonly object _gate = new();
    private readonly DataSource _source;
    private readonly NativeCommandKind _kind;
    private readonly uint _expectedKind;
    private readonly CommandResultReader<T> _read;
    private readonly CancellationToken _token;
    private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private GCHandle _root;
    private CancellationTokenRegistration _registration;
    private nint _command;
    private NativeCommandResult _result;
    private DatabaseOwner? _proofOwner;
    private CloseController? _proof;
    private int _slot = -1, _controls, _callbackEntries;
    private bool _started, _setupReturned, _requestEntered, _requestReturned, _accepted, _ready, _rooted;
    private bool _registrationAttempted, _registrationReady, _hasRegistration, _drainStarted, _registrationDrained;
    private bool _cancelRequested, _cancelSent, _takeSettled, _resultTaken, _published, _publishing;
    private bool _indeterminate, _nativeSafe, _fallbackStarted, _fallbackSettled, _nativeRejected;
    private bool _processing, _rescan;
    private long _fallbackRetryAfter;
    private Exception? _failure;

    internal HostCommandController(DataSource source, NativeCommandKind kind, uint expectedKind,
        CommandResultReader<T> read, CancellationToken token) : base(source.TestCommandScheduler)
    { _source = source; _kind = kind; _expectedKind = expectedKind; _read = read; _token = token; }

    internal HostCommandSnapshot Snapshot
    {
        get
        {
            lock (_gate) return new((uint)_kind, _expectedKind, _setupReturned, _requestEntered,
                _requestReturned, _accepted, _ready, _rooted, _slot >= 0, _controls, _callbackEntries,
                _registrationDrained, _resultTaken, _published, _fallbackStarted, _fallbackSettled,
                _indeterminate, _nativeSafe);
        }
    }

    internal bool DefinitelyRejected { get { lock (_gate) return _nativeRejected && !_indeterminate; } }

    internal unsafe Task<T> Request(DatabaseOwner target, DatabaseOwner? input, ReadOnlyMemory<byte> payload, uint action)
    {
        lock (_gate)
        {
            if (_started) throw new InvalidOperationException("A command completion is submitted once.");
            _started = true;
        }
        bool control = false;
        nint command = 0;
        try
        {
            Boundary(HostCommandBoundary.BeforeRoot);
            _root = GCHandle.Alloc(this, GCHandleType.Normal);
            lock (_gate) _rooted = true;
            Boundary(HostCommandBoundary.RootAllocated);
            Boundary(HostCommandBoundary.BeforeReserve);
            var slot = _source.CallbackCoordinator.Reserve(this);
            lock (_gate) _slot = slot;
            Boundary(HostCommandBoundary.Reserved);
            Boundary(HostCommandBoundary.BeforeRegister);
            if (_token.CanBeCanceled)
            {
                lock (_gate) _registrationAttempted = true;
                var registration = _token.UnsafeRegister(static state => ((HostCommandController<T>)state!).Cancel(), this);
                lock (_gate) { _registration = registration; _hasRegistration = true; _registrationReady = true; }
            }
            else lock (_gate) { _registrationReady = true; _registrationDrained = true; }
            Boundary(HostCommandBoundary.Registered);
            Boundary(HostCommandBoundary.BeforeRequest);
            lock (_gate)
            {
                if (_cancelRequested || _token.IsCancellationRequested) _failure ??= new OperationCanceledException(_token);
                else { _controls++; control = true; }
            }
            if (control)
            {
                NativePullCallbacks callbacks = new()
                {
                    StructSize = (uint)sizeof(NativePullCallbacks), Context = GCHandle.ToIntPtr(_root), Ready = &OnReady
                };
                var error = BindingChecks.Error();
                lock (_gate) _requestEntered = true;
                var status = NativeCommand.Request(_kind, target, input, payload, action, callbacks, ref command, ref error);
                Boundary(HostCommandBoundary.RequestCalled);
                lock (_gate)
                {
                    _requestReturned = true;
                    _accepted = status == NativeStatus.Ok;
                    _nativeRejected = !_accepted && command == 0 && !_ready;
                    if (_accepted && command == 0 || !_accepted && (command != 0 || _ready))
                    {
                        _indeterminate = true;
                        _failure ??= new NativeEngineException("Native command acceptance has an inconsistent owner or notification.");
                    }
                }
                if (status != NativeStatus.Ok)
                    lock (_gate) _failure ??= HostFailure.Create(_source.Binding, status, error, token: _token);
                Boundary(HostCommandBoundary.RequestReturned);
            }
        }
        catch (Exception error)
        {
            lock (_gate)
            {
                _failure ??= error;
                _indeterminate |= _requestEntered && !_requestReturned
                    || _registrationAttempted && !_registrationReady;
            }
        }
        finally
        {
            bool resume, cancel;
            lock (_gate)
            {
                _command = command;
                if (control) _controls--;
                if (!_registrationAttempted && !_registrationReady)
                { _registrationReady = true; _registrationDrained = true; }
                _setupReturned = true;
                cancel = _cancelRequested && _accepted && !_ready;
                resume = !_accepted || _ready || _failure is not null || _indeterminate;
            }
            if (cancel) Cancel();
            if (resume) Resume();
        }
        return CompleteAsync();
    }

    private async Task<T> CompleteAsync()
    {
        try { return await _completion.Task.ConfigureAwait(false); }
        catch (OperationCanceledException) when (_failure is OperationCanceledException { InnerException: not null })
        {
            // Preserve the copied cleanup detail without changing Task cancellation.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(_failure).Throw();
            throw;
        }
    }

    private unsafe void Cancel()
    {
        nint command;
        lock (_gate)
        {
            _cancelRequested = true;
            if (!_setupReturned || !_accepted || _ready || _cancelSent || _published) return;
            _cancelSent = true; _controls++; command = _command;
        }
        try
        {
            Boundary(HostCommandBoundary.CancelEntered);
            var status = NativeCommand.Calls(_source.Binding).Cancel(command);
            if (status != NativeStatus.Ok)
                throw HostFailure.Create(_source.Binding, status, BindingChecks.Error(), token: _token);
            Boundary(HostCommandBoundary.CancelReturned);
        }
        catch (Exception error)
        {
            lock (_gate) { _failure ??= error; _indeterminate = true; }
            _source.Poison();
        }
        finally
        {
            lock (_gate) _controls--;
            Resume();
        }
    }

    protected override void NotifyReady(ulong generation)
    {
        bool entered = false;
        try
        {
            lock (_gate)
            {
                _callbackEntries++; entered = true;
                if (!_requestEntered || _ready || _published || generation != 0)
                {
                    _failure ??= new NativeEngineException("The command ready notification is invalid or duplicated.");
                    _indeterminate = true;
                }
                else _ready = true;
            }
            Boundary(HostCommandBoundary.Ready);
        }
        catch (Exception error)
        {
            lock (_gate) _failure ??= error;
        }
        finally
        {
            if (entered)
            {
                // This is the last access through the callback cookie. The native
                // notification tail owns only its independent completion fences.
                lock (_gate) { _callbackEntries--; Signal(); }
            }
        }
    }

    private void Resume()
    {
        bool unreserved;
        lock (_gate) unreserved = _slot < 0 && _setupReturned && !_published;
        if (!unreserved) { Signal(); return; }
        try { Process(); }
        catch (Exception error) { SchedulingFault(error); }
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
                    _processing = false; return;
                }
            }
        }
        catch { lock (_gate) _processing = false; throw; }
    }

    private void ProcessCore()
    {
        bool fallback;
        lock (_gate)
        {
            if (!_setupReturned || _published || _publishing || _controls != 0 || _callbackEntries != 0) return;
            fallback = _indeterminate || _accepted && _failure is not null && !_takeSettled;
            if (!fallback && _accepted && !_ready) return;
            if (!fallback) _nativeSafe = true;
        }
        if (fallback)
        {
            StartFallback();
            lock (_gate) if (!_nativeSafe) return;
        }
        if (!DrainRegistration()) return;
        bool take;
        lock (_gate)
        {
            if (_controls != 0 || _callbackEntries != 0 || !_registrationDrained) return;
            take = _accepted && !fallback && !_takeSettled;
            if (take) _controls++;
        }
        if (take)
        {
            bool returned = false, detailsTaken = false;
            uint status = NativeStatus.Pending;
            try
            {
                Boundary(HostCommandBoundary.BeforeTake);
                var error = BindingChecks.Error();
                status = NativeCommand.Take(_source.Binding, _command, _expectedKind, ref _result, ref error);
                returned = true;
                if (status == NativeStatus.Pending)
                    throw new NativeEngineException("Command readiness preceded a transferable result.");
                lock (_gate) _takeSettled = true;
                if (status != NativeStatus.Ok) throw NativeCommand.Failure(_source, _command, status, error, _token, out detailsTaken);
                NativeCommand.Validate(_result, _expectedKind);
                lock (_gate) _resultTaken = true;
                Boundary(HostCommandBoundary.ResultTaken);
            }
            catch (Exception error)
            {
                lock (_gate)
                {
                    _failure ??= error;
                    if (!returned || !_takeSettled || (status == NativeStatus.Ok ? !_resultTaken : !detailsTaken))
                    { _indeterminate = true; _nativeSafe = false; }
                }
            }
            finally { lock (_gate) _controls--; }
        }
        lock (_gate) fallback = !_nativeSafe;
        if (fallback) { StartFallback(); return; }
        Publish();
    }

    private bool DrainRegistration()
    {
        bool drain;
        lock (_gate)
        {
            if (!_registrationReady) return false;
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
        lock (_gate) return _registrationDrained;
    }

    private async Task DrainRegistrationAsync(ValueTask pending)
    {
        try
        {
            await pending.ConfigureAwait(false);
            lock (_gate) { _registration = default; _registrationDrained = true; }
        }
        catch (Exception error) { SchedulingFault(error); }
        finally { Resume(); }
    }

    private void StartFallback()
    {
        bool start;
        lock (_gate)
        {
            start = !_fallbackStarted;
            if (start) _fallbackStarted = true;
        }
        _source.Poison();
        if (start) _ = FinishFallbackAsync();
        else
        {
            // Only cold failures poll an independent proof. A failed generic
            // observer is not permission to abandon an accepted callback.
            try
            {
                bool settled;
                lock (_gate) settled = _fallbackSettled;
                if (settled && _proof is not null && _proof.Snapshot.LocalResourcesReleased)
                    lock (_gate) _nativeSafe = true;
            }
            catch { /* Keep the root until a real close proof is available. */ }
            bool retry = false;
            lock (_gate)
            {
                if (_fallbackSettled && !_nativeSafe)
                {
                    if (Environment.TickCount64 >= _fallbackRetryAfter)
                    { _fallbackSettled = false; retry = true; }
                    else RequestRetry();
                }
            }
            if (retry) _ = FinishFallbackAsync();
        }
    }

    private async Task FinishFallbackAsync()
    {
        try
        {
            DatabaseOwner owner;
            if (_command != 0)
            {
                lock (_gate)
                    owner = _proofOwner ??= new DatabaseOwner(_source.Binding, DatabaseOwnerKind.CommandCompletion, _command);
            }
            else owner = _source.Owner;
            // A new cold join can retry a failed cleanup launch. Neither a
            // failed Task nor a copied diagnostic is a retirement certificate.
            var proof = new CloseController(_source, owner, static () => { });
            lock (_gate) _proof = proof;
            await proof.JoinAsync().ConfigureAwait(false);
            lock (_gate) _nativeSafe = proof.Snapshot.LocalResourcesReleased;
        }
        catch (Exception error)
        {
            lock (_gate)
            {
                if (_failure is null) _failure = error;
                else try { _failure.Data["Korda.Database.CommandCleanup.v1"] = error; } catch { }
            }
        }
        finally
        {
            lock (_gate) { _fallbackSettled = true; _fallbackRetryAfter = Environment.TickCount64 + 10; }
            Resume();
        }
    }

    private unsafe void Publish()
    {
        Exception? failure;
        nint command;
        int slot;
        lock (_gate)
        {
            if (_published || _publishing || !_nativeSafe || !_registrationDrained || _controls != 0 || _callbackEntries != 0) return;
            _publishing = true;
            failure = _failure;
            if (failure is null && !_resultTaken)
                failure = _failure = new NativeEngineException("Command completion has no result.");
            if (_rooted) { _root.Free(); _rooted = false; }
            slot = _slot; _slot = -1;
            command = _command; _command = 0;
        }
        if (_proofOwner is not null) _proofOwner.Dispose();
        else if (command != 0) _source.Binding.Calls.nw_db_owner_release_v1(command);
        if (slot >= 0) _source.CallbackCoordinator.Release(slot, this);
        CompleteScheduling();
        lock (_gate) _published = true;
        T value = default!;
        try
        {
            Boundary(HostCommandBoundary.Published);
            _source.TestCommandSnapshot?.Invoke(Snapshot);
            if (failure is null) value = _read(_source.Binding, ref _result);
        }
        catch (Exception error) { failure ??= error; }
        if (_result.Buffer != 0) { _source.Binding.Calls.nw_db_buffer_release_v1(_result.Buffer); _result.Buffer = 0; }
        if (failure is not null && _result.Owner != 0)
        {
            // A transferred transaction/savepoint is real ownership even if a
            // managed result observer failed. Join it before publishing failure.
            _ = AbandonResultAsync(failure);
            return;
        }
        Complete(value, failure);
    }

    private async Task AbandonResultAsync(Exception failure)
    {
        DatabaseOwner? owner = null;
        while (true)
        {
            CloseController? close = null;
            try
            {
                if (owner is null)
                {
                    owner = new(_source.Binding, _expectedKind == 3 ? DatabaseOwnerKind.Transaction : DatabaseOwnerKind.Savepoint, _result.Owner);
                    _result.Owner = 0;
                }
                close = new(_source, owner, static () => { });
                await close.JoinAsync().ConfigureAwait(false);
                if (close.Snapshot.LocalResourcesReleased) break;
            }
            catch (Exception cleanup)
            {
                _source.Poison();
                try { failure.Data["Korda.Database.CommandResultCleanup.v1"] = cleanup; } catch { }
                try { if (close is not null && close.Snapshot.LocalResourcesReleased) break; }
                catch { /* The next cold join still owns the exact child. */ }
            }
            // Only a failed result-adoption cleanup retries. Its state machine
            // retains the child until actual safety, including a failed launch.
            await Task.Delay(10).ConfigureAwait(false);
        }
        owner!.Dispose();
        Complete(default!, failure);
    }

    private void Complete(T value, Exception? failure)
    {
        lock (_gate) _failure = failure;
        if (failure is null) _completion.TrySetResult(value);
        else if (failure is OperationCanceledException cancellation) _completion.TrySetCanceled(cancellation.CancellationToken);
        else _completion.TrySetException(failure);
    }

    protected override void SchedulingFault(Exception error)
    {
        lock (_gate) { _failure ??= error; _indeterminate = true; _nativeSafe = false; _publishing = false; }
        _source.Poison();
        Signal();
    }

    private void Boundary(HostCommandBoundary boundary) => _source.TestCommandBoundary?.Invoke(boundary);
}
