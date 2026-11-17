using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NativeWeave.Interop.Abi;
using NativeWeave.Interop.Callbacks;
using NativeWeave.Interop.Lifetime;

namespace NativeWeave.Interop.Async;

/// <summary>Statically bound operation controls; domain adapters define their exact input/result ABI.</summary>
internal abstract unsafe class AsyncNativeCalls
{
    internal abstract uint Start(nint operation, in NwOperationCallbacksV1 callbacks);
    internal abstract uint RequestCancel(nint operation);
    internal abstract uint TryRetire(nint operation);
    /// <summary>Maps a terminal domain failure while a cleanup control lease still protects the operation.</summary>
    internal virtual Exception MapFailure(nint operation, uint status, in NwErrorInfoV1 error, CancellationToken token) =>
        NativeError.Create("async", "terminal", status, in error, token);
    /// <summary>Nonthrowingly closes the owning engine's ordinary admission, preserving trusted cleanup.</summary>
    internal abstract void Poison();
}

/// <summary>Exact known setup outcome; Indeterminate retains potential native obligations.</summary>
internal enum AsyncStartOutcome { NotStarted, Skipped, Rejected, AcceptedReturned, Indeterminate }
/// <summary>Optional conformance fault boundaries; production adapters leave the test hook null.</summary>
internal enum AsyncBoundary { BeforeRoot, RootAllocated, BeforeRegister, Registered, BeforeStart, StartReturned, CancelEntered, TerminalFieldsCopied, BeforeTake }

/// <summary>Nongeneric static reverse-P/Invoke, setup/cancellation drain and native retirement owner.</summary>
/// <remarks>Native terminal outcome, managed Task publication and resource retirement are
/// separate. A Normal GCHandle and reserved coordinator slot survive every admitted native
/// call and cancellation callback. No application continuation runs inside the UCO entry.
/// Indeterminate setup retains its owner and budget instead of fabricating never-started cleanup.</remarks>
internal abstract class AsyncOperation : CoordinatedCallback, IDisposable, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly NativeResourceOwner _owner;
    private readonly AsyncNativeCalls _native;
    private readonly CallbackCoordinator _coordinator;
    private readonly CancellationToken _token;
    private readonly IDisposable? _inputLifetime;
    private readonly long _inputBytes;
    private readonly int _slot;
    private readonly Action<AsyncBoundary>? _testBoundary;
    private readonly TaskCompletionSource _retirement = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private GCHandle _root;
    private CancellationTokenRegistration _registration;
    private bool _rootLive;
    private bool _setupStarted;
    private bool _setupSettled;
    private bool _registrationAttempted;
    private bool _registrationReady;
    private bool _hasRegistration;
    private bool _registrationDrainStarted;
    private bool _registrationDrained;
    private bool _startEntered;
    private bool _closing;
    private bool _cancelRequested;
    private bool _cancelSent;
    private bool _nativeQuiet;
    private bool _resultProcessed;
    private bool _releasing;
    private bool _released;
    private bool _quarantined;
    private bool _needsPoison;
    private bool _poisonReported;
    private int _controls;
    private int _callbackEntries;
    private int _noticeClaimed;
    private int _noticeReceived;
    private int _duplicateNotices;
    private int _bindingFailure;
    private uint _nativeStatus;
    private NwErrorInfoV1 _error = new() { StructSize = 24 };
    private Exception? _localFailure;
    private bool _localCanceled;
    private uint _cancelFailureStatus;
    private AsyncStartOutcome _startOutcome;
    private readonly long _created = Environment.TickCount64;
    [ThreadStatic] private static AsyncOperation? s_callbackScope;

    protected AsyncOperation(NativeResourceOwner owner, AsyncNativeCalls native, CallbackCoordinator coordinator,
        CancellationToken cancellationToken, IDisposable? inputLifetime, long inputBytes,
        ICallbackScheduler? scheduler, Action<AsyncBoundary>? testBoundary) : base(scheduler)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(native);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentOutOfRangeException.ThrowIfNegative(inputBytes);
        _owner = owner;
        _native = native;
        _coordinator = coordinator;
        _token = cancellationToken;
        _inputLifetime = inputLifetime;
        _inputBytes = inputBytes;
        _testBoundary = testBoundary;
        testBoundary?.Invoke(AsyncBoundary.BeforeRoot);
        _root = GCHandle.Alloc(this, GCHandleType.Normal);
        _rootLive = true;
        try
        {
            testBoundary?.Invoke(AsyncBoundary.RootAllocated);
            _slot = coordinator.Reserve(this);
        }
        catch { _root.Free(); _rootLive = false; throw; }
    }

    internal Task Retirement => _retirement.Task;
    internal AsyncSnapshot Snapshot
    {
        get
        {
            lock (_gate) return new(_setupSettled, _startOutcome, _registrationReady, _hasRegistration,
                _registrationDrained, _closing, _cancelRequested, _nativeQuiet, _released, _rootLive,
                _quarantined, _controls, _callbackEntries, Volatile.Read(ref _noticeReceived) != 0,
                _nativeStatus, Volatile.Read(ref _duplicateNotices), Volatile.Read(ref _bindingFailure) != 0,
                _resultProcessed, _released ? 0 : _inputBytes, Environment.TickCount64 - _created);
        }
    }

    protected unsafe void StartCore()
    {
        lock (_gate)
        {
            if (_setupStarted || _setupSettled || _closing)
                throw new InvalidOperationException("Operation setup may start exactly once before closure.");
            _setupStarted = true;
        }
        bool startLease = false;
        try
        {
            _testBoundary?.Invoke(AsyncBoundary.BeforeRegister);
            if (_token.CanBeCanceled)
            {
                lock (_gate) _registrationAttempted = true;
                // This may invoke RequestCancellation synchronously before the
                // returned registration is assigned. Context/root/control state
                // already exists, but setup is not yet settled.
                CancellationTokenRegistration registration = _token.UnsafeRegister(
                    static state => ((AsyncOperation)state!).RequestCancellation(), this);
                lock (_gate)
                {
                    _registration = registration;
                    _hasRegistration = true;
                    _registrationReady = true;
                }
            }
            else
            {
                lock (_gate) { _registrationReady = true; _registrationDrained = true; }
            }
            _testBoundary?.Invoke(AsyncBoundary.Registered);
            _testBoundary?.Invoke(AsyncBoundary.BeforeStart);
            lock (_gate)
            {
                if (_closing || _cancelRequested || _token.IsCancellationRequested)
                {
                    _startOutcome = AsyncStartOutcome.Skipped;
                    _localCanceled = _cancelRequested || _token.IsCancellationRequested;
                    if (!_localCanceled) _localFailure = new ObjectDisposedException("AsyncOperation");
                    _closing = true;
                    return;
                }
                _startEntered = true;
            }
            nint pointer = AcquireControl(ref startLease, cleanup: true);
            NwOperationCallbacksV1 callbacks = new()
            {
                StructSize = (uint)sizeof(NwOperationCallbacksV1),
                Cookie = (nuint)GCHandle.ToIntPtr(_root),
                OnTerminal = &OnTerminal
            };
            uint status = _native.Start(pointer, in callbacks);
            lock (_gate)
            {
                if (status == NativeStatus.Ok) _startOutcome = AsyncStartOutcome.AcceptedReturned;
                else if (Volatile.Read(ref _noticeClaimed) != 0)
                {
                    _startOutcome = AsyncStartOutcome.Indeterminate;
                    _quarantined = true;
                    _needsPoison = true;
                    _localFailure = BridgeException("Start rejected after publishing a terminal callback.");
                    _closing = true;
                }
                else
                {
                    _startOutcome = AsyncStartOutcome.Rejected;
                    _localFailure = StatusException("start", status);
                    _closing = true;
                }
            }
            _testBoundary?.Invoke(AsyncBoundary.StartReturned);
        }
        catch (Exception error)
        {
            lock (_gate)
            {
                _localFailure ??= error;
                _closing = true;
                if ((_registrationAttempted && !_registrationReady) ||
                    (_startEntered && _startOutcome == AsyncStartOutcome.NotStarted))
                {
                    _startOutcome = AsyncStartOutcome.Indeterminate;
                    _quarantined = true;
                    _needsPoison = true;
                }
                else if (_startOutcome == AsyncStartOutcome.NotStarted) _startOutcome = AsyncStartOutcome.Skipped;
            }
        }
        finally
        {
            ReleaseControl(ref startLease, notify: false);
            lock (_gate)
            {
                if (!_registrationAttempted && !_registrationReady)
                {
                    _registrationReady = true; // explicit NoRegistration sentinel
                    _registrationDrained = true;
                }
                _setupSettled = true; // no registration/start can occur after this publication
            }
            Signal();
        }
    }

    internal nint AcquireControl(ref bool acquired) => AcquireControl(ref acquired, cleanup: false);

    private nint AcquireControl(ref bool acquired, bool cleanup)
    {
        if (!TryAcquireControl(ref acquired, cleanup, out nint pointer))
            throw new ObjectDisposedException("AsyncOperation");
        return pointer;
    }

    private bool TryAcquireControl(ref bool acquired, bool cleanup, out nint pointer)
    {
        pointer = 0;
        if (acquired) throw new InvalidOperationException("A control lease cannot acquire twice.");
        lock (_gate)
        {
            if (_released || _releasing || (!cleanup && _closing)) return false;
            checked { _controls++; }
        }
        try { pointer = _owner.Acquire(ref acquired); return true; }
        catch { lock (_gate) _controls--; Signal(); throw; }
    }

    internal void ReleaseControl(ref bool acquired) => ReleaseControl(ref acquired, notify: true);
    private void ReleaseControl(ref bool acquired, bool notify)
    {
        if (!acquired) return;
        try { _owner.Release(ref acquired); }
        finally { lock (_gate) _controls--; if (notify) Signal(); }
    }

    private void RequestCancellation()
    {
        AsyncOperation? previous = s_callbackScope;
        s_callbackScope = this;
        bool acquired = false;
        try
        {
            lock (_gate)
            {
                _cancelRequested = true;
                if (_closing || _released || _releasing) return;
            }
            _testBoundary?.Invoke(AsyncBoundary.CancelEntered);
            if (!TryAcquireControl(ref acquired, cleanup: false, out nint pointer)) return;
            uint status = _native.RequestCancel(pointer);
            if (status != NativeStatus.Ok)
            {
                lock (_gate) { _cancelFailureStatus = status; _closing = true; _needsPoison = true; }
                return;
            }
            lock (_gate) _cancelSent = true;
        }
        catch (Exception error)
        {
            // Controlled internal delegate: never throw through UnsafeRegister
            // or CancellationTokenSource.Cancel, and never publish cancellation.
            lock (_gate) { _localFailure ??= error; _closing = true; _needsPoison = true; }
        }
        finally
        {
            ReleaseControl(ref acquired, notify: false);
            s_callbackScope = previous;
            Signal();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe uint OnTerminal(nuint cookie, uint status, NwErrorInfoV1* error)
    {
        AsyncOperation? context = null;
        AsyncOperation? previous = s_callbackScope;
        bool entered = false;
        bool claimed = false;
        uint result = NativeStatus.Ok;
        try
        {
            // Validity follows native admission/retirement, never a nonzero probe.
            context = GCHandle.FromIntPtr((nint)cookie).Target as AsyncOperation;
            if (context is null) return NativeStatus.ContractViolation;
            s_callbackScope = context;
            lock (context._gate) { checked { context._callbackEntries++; } }
            entered = true;
            if (Interlocked.CompareExchange(ref context._noticeClaimed, 1, 0) != 0)
            {
                Interlocked.Increment(ref context._duplicateNotices);
                lock (context._gate) context._needsPoison = true;
                return NativeStatus.CallbackFailed;
            }
            claimed = true;
            context._nativeStatus = status;
            if (status == NativeStatus.Pending || (error is not null && (error->StructSize != 24 || error->Flags != 0)))
            {
                Interlocked.Exchange(ref context._bindingFailure, 1);
                lock (context._gate) context._needsPoison = true;
                result = NativeStatus.CallbackFailed;
            }
            else if (error is not null) context._error = *error;
            context._testBoundary?.Invoke(AsyncBoundary.TerminalFieldsCopied);
        }
        catch (Exception)
        {
            if (context is not null)
            {
                Interlocked.Exchange(ref context._bindingFailure, 1);
                lock (context._gate) context._needsPoison = true;
            }
            result = NativeStatus.CallbackFailed;
        }
        finally
        {
            if (claimed) Volatile.Write(ref context!._noticeReceived, 1);
            if (entered)
            {
                lock (context!._gate) { context._callbackEntries--; context._closing = true; }
                context.Signal();
            }
            s_callbackScope = previous;
        }
        return result;
    }

    /// <summary>Closes ordinary control admission and requests native stop without completing the Task early.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_released) return;
            _closing = true;
            if (!_setupStarted)
            {
                _setupSettled = true;
                _registrationReady = true;
                _registrationDrained = true;
                _startOutcome = AsyncStartOutcome.Skipped;
                _localFailure = new ObjectDisposedException("AsyncOperation");
            }
        }
        Signal();
    }

    /// <summary>Joins actual cleanup, separately from the application Task and caller's canceled wait.</summary>
    public ValueTask DisposeAsync()
    {
        Dispose();
        if (ReferenceEquals(s_callbackScope, this))
            throw new InvalidOperationException("An operation callback/result mapper cannot wait for its own retirement.");
        return new ValueTask(_retirement.Task);
    }

    protected override void SchedulingFault(Exception error)
    {
        bool poison;
        lock (_gate)
        {
            _localFailure ??= error;
            _closing = true;
            _quarantined = true;
            _needsPoison = true;
            poison = !_poisonReported;
            _poisonReported = true;
        }
        // This path runs on the external coordinator/drain executor. Poison is
        // a trusted nonthrowing admission control, independent from retry success.
        if (poison) _native.Poison();
        // Retained quarantine is observable, never reported as safe closure.
        PublishFailure(error);
    }

    protected override void Process()
    {
        bool poison;
        bool quarantine;
        bool closing;
        Exception? localFailure;
        lock (_gate)
        {
            if (_released || !_setupSettled) return;
            poison = _needsPoison && !_poisonReported;
            if (poison) _poisonReported = true;
            quarantine = _quarantined;
            closing = _closing;
            localFailure = _localFailure;
        }
        if (poison) _native.Poison();
        if (quarantine)
        {
            PublishFailure(localFailure ?? BridgeException("Indeterminate operation setup retains native ownership."));
            return;
        }
        if (!closing) return;

        AsyncStartOutcome start;
        bool cancelSent;
        lock (_gate) { start = _startOutcome; cancelSent = _cancelSent; }
        if (start == AsyncStartOutcome.AcceptedReturned && !cancelSent)
        {
            bool lease = false;
            try
            {
                nint pointer = AcquireControl(ref lease, cleanup: true);
                uint status = _native.RequestCancel(pointer);
                if (status != NativeStatus.Ok) throw StatusException("cleanup_cancel", status);
                lock (_gate) _cancelSent = true;
            }
            finally { ReleaseControl(ref lease, notify: false); }
        }

        bool beginDrain;
        lock (_gate)
        {
            beginDrain = _hasRegistration && !_registrationDrainStarted;
            if (beginDrain) _registrationDrainStarted = true;
        }
        if (beginDrain)
        {
            ValueTask drain = _registration.DisposeAsync();
            if (drain.IsCompletedSuccessfully)
            {
                drain.GetAwaiter().GetResult();
                lock (_gate) _registrationDrained = true;
            }
            else _ = FinishRegistrationDrainAsync(drain);
        }
        lock (_gate) { if (!_registrationReady || !_registrationDrained || _controls != 0) return; }

        bool processResult;
        lock (_gate) processResult = !_resultProcessed;
        if (processResult)
        {
            bool notice = Volatile.Read(ref _noticeReceived) != 0;
            bool localCanceled;
            lock (_gate) { localFailure = _localFailure; localCanceled = _localCanceled; }
            if (localFailure is not null) { PublishFailure(localFailure); MarkResultProcessed(); }
            else if (localCanceled) { PublishCanceled(_token); MarkResultProcessed(); }
            else if (notice)
            {
                if (_cancelFailureStatus != NativeStatus.Ok)
                {
                    PublishFailure(StatusException("request_cancel", _cancelFailureStatus));
                    MarkResultProcessed();
                }
                else if (Volatile.Read(ref _bindingFailure) != 0)
                {
                    // The bridge failed, but the safely copied native diagnostic
                    // identity remains available, including on an Unknown effect.
                    Exception failure = new NativeEngineException("async", "bridge", NativeStatus.ContractViolation,
                        _error.Domain, _error.Code, _error.CorrelationId,
                        "The terminal binding failed; the original native outcome is retained.");
                    failure.Data["NativeTerminalStatus"] = _nativeStatus;
                    PublishFailure(failure);
                    MarkResultProcessed();
                }
                else if (_nativeStatus == NativeStatus.Canceled) { PublishCanceled(_token); MarkResultProcessed(); }
                else if (_nativeStatus != NativeStatus.Ok)
                {
                    bool lease = false;
                    AsyncOperation? previous = s_callbackScope;
                    s_callbackScope = this;
                    try
                    {
                        nint pointer = AcquireControl(ref lease, cleanup: true);
                        PublishFailure(_native.MapFailure(pointer, _nativeStatus, in _error, _token));
                    }
                    catch (Exception error) { PublishFailure(error); }
                    finally { ReleaseControl(ref lease, notify: false); s_callbackScope = previous; MarkResultProcessed(); }
                }
                else
                {
                    bool lease = false;
                    AsyncOperation? previous = s_callbackScope;
                    s_callbackScope = this;
                    try
                    {
                        nint pointer = AcquireControl(ref lease, cleanup: true);
                        _testBoundary?.Invoke(AsyncBoundary.BeforeTake);
                        PublishSuccess(pointer);
                    }
                    catch (Exception error) { PublishFailure(error); }
                    finally { ReleaseControl(ref lease, notify: false); s_callbackScope = previous; MarkResultProcessed(); }
                }
            }
        }

        bool quiet;
        lock (_gate) quiet = _nativeQuiet;
        if (!quiet)
        {
            if (start is AsyncStartOutcome.Skipped or AsyncStartOutcome.Rejected)
            {
                lock (_gate) _nativeQuiet = true;
            }
            else
            {
                bool lease = false;
                try
                {
                    nint pointer = AcquireControl(ref lease, cleanup: true);
                    uint status = _native.TryRetire(pointer);
                    if (status == NativeStatus.Ok) { lock (_gate) _nativeQuiet = true; }
                    else if (status == NativeStatus.Pending) RequestRetry();
                    else throw StatusException("try_retire", status);
                }
                finally { ReleaseControl(ref lease, notify: false); }
            }
        }
        lock (_gate)
        {
            if (!_nativeQuiet || !_resultProcessed || !_registrationDrained || _controls != 0 ||
                _callbackEntries != 0 || _releasing) return;
            _releasing = true;
        }
        _owner.Dispose();
        _inputLifetime?.Dispose();
        _root.Free();
        lock (_gate) { _rootLive = false; _released = true; }
        CompleteScheduling();
        _coordinator.Release(_slot, this);
        _retirement.TrySetResult();
    }

    private async Task FinishRegistrationDrainAsync(ValueTask drain)
    {
        try
        {
            await drain.ConfigureAwait(false);
            lock (_gate) _registrationDrained = true;
        }
        catch (Exception error) { SchedulingFault(error); }
        finally { Signal(); }
    }

    private void MarkResultProcessed() { lock (_gate) _resultProcessed = true; }
    private static Exception StatusException(string operation, uint status)
    {
        NwErrorInfoV1 record = new() { StructSize = 24 };
        return NativeError.Create("async", operation, status, in record);
    }
    private static NativeEngineException BridgeException(string message) =>
        new("async", "bridge", NativeStatus.ContractViolation, 0, 0, 0, message);
    protected abstract void PublishSuccess(nint operation);
    protected abstract void PublishFailure(Exception error);
    protected abstract void PublishCanceled(CancellationToken token);
}

/// <summary>Typed managed Task bridge; TResult and its mapper never cross the unmanaged ABI.</summary>
/// <remarks>The statically compiled domain mapper preallocates its wrapper, performs exactly
/// one typed take and disposes an acquired native result on conversion failure. A successful
/// result owns its independent lifetime after operation retirement. Inputs/budget tokens
/// transfer only after construction succeeds and remain owned until actual reclamation.</remarks>
internal sealed class AsyncOperation<TResult> : AsyncOperation
{
    private readonly Func<nint, TResult> _takeAndMaterialize;
    private readonly TaskCompletionSource<TResult> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal AsyncOperation(NativeResourceOwner owner, AsyncNativeCalls native, CallbackCoordinator coordinator,
        Func<nint, TResult> takeAndMaterialize, CancellationToken cancellationToken = default,
        IDisposable? inputLifetime = null, long inputBytes = 0, ICallbackScheduler? scheduler = null,
        Action<AsyncBoundary>? testBoundary = null)
        : base(owner, Validate(native, takeAndMaterialize), coordinator, cancellationToken, inputLifetime,
            inputBytes, scheduler, testBoundary) => _takeAndMaterialize = takeAndMaterialize;

    private static AsyncNativeCalls Validate(AsyncNativeCalls native, Func<nint, TResult> mapper)
    {
        ArgumentNullException.ThrowIfNull(mapper);
        return native;
    }
    internal Task<TResult> Task => _result.Task;
    internal Task<TResult> Start() { StartCore(); return _result.Task; }
    protected override void PublishSuccess(nint operation) => _result.TrySetResult(_takeAndMaterialize(operation));
    protected override void PublishFailure(Exception error) => _result.TrySetException(error);
    protected override void PublishCanceled(CancellationToken token) => _result.TrySetCanceled(token);
}

/// <summary>Fixed lifecycle/retained-budget diagnostics without native pointer or cookie disclosure.</summary>
internal readonly record struct AsyncSnapshot(bool SetupSettled, AsyncStartOutcome Start, bool RegistrationReady,
    bool HasRegistration, bool RegistrationDrained, bool Closing, bool CancelRequested, bool NativeQuiet,
    bool Released, bool RootLive, bool Quarantined, int Controls, int CallbackEntries, bool NoticeReceived,
    uint NativeStatus, int DuplicateNotices, bool BindingFailure, bool ResultProcessed, long RetainedInputBytes,
    long AgeMilliseconds);
