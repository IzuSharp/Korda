using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Korda.Interop.Abi;
using Korda.Interop.Lifetime;

namespace Korda.Interop.Callbacks;

/// <summary>Statically compiled engine operations. Each concrete adapter keeps its owning module alive.</summary>
internal abstract unsafe class CallbackNativeCalls
{
    internal abstract uint Start(nint subscription, in NwCallbacksV1 callbacks);
    internal abstract uint RequestStop(nint subscription);
    internal abstract uint TryRetire(nint subscription);
}

/// <summary>An explicitly synchronous decision profile; exceptions and false are fail-closed.</summary>
internal delegate bool SynchronousDecision(uint kind, ReadOnlySpan<byte> payload);

/// <summary>Internal D05 subscription owner, strong cookie root and separately drained dispatcher.</summary>
/// <remarks>Construction transfers the supplied owner only after its cleanup slot and root exist.
/// Start failure remains owned by this context. Notification bytes are copied once; both accepted
/// item count and bytes include an active asynchronous handler. Dispose discards queued work and
/// requests stop, while DisposeAsync joins native quiet, setup/control/callback and handler drain.</remarks>
internal sealed class CallbackRegistration : CoordinatedCallback, IDisposable, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly NativeResourceOwner _owner;
    private readonly CallbackNativeCalls _native;
    private readonly CallbackCoordinator _coordinator;
    private readonly int _slot;
    private readonly Queue<Notification> _pending;
    private readonly int _maxItems;
    private readonly int _maxBytes;
    private readonly int _maxPayload;
    private readonly Func<uint, ReadOnlyMemory<byte>, ValueTask>? _handler;
    private readonly SynchronousDecision? _decision;
    private readonly TaskCompletionSource _retirement = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private GCHandle _root;
    private bool _rootLive;
    private bool _startCalled;
    private bool _setupSettled;
    private bool _accepted;
    private bool _indeterminate;
    private bool _closing;
    private bool _stopSent;
    private bool _nativeQuiet;
    private bool _releasing;
    private bool _released;
    private bool _quarantined;
    private int _controls;
    private int _nativeEntries;
    private int _activeHandlers;
    private int _retainedItems;
    private int _retainedBytes;
    private uint _failureStatus;
    private Exception? _failure;
    private readonly long _created = Environment.TickCount64;
    private static readonly AsyncLocal<CallbackRegistration?> HandlerScope = new();
    [ThreadStatic] private static CallbackRegistration? s_decisionScope;

    internal CallbackRegistration(NativeResourceOwner owner, CallbackNativeCalls native,
        CallbackCoordinator coordinator, int maxItems, int maxBytes, int maxPayload,
        Func<uint, ReadOnlyMemory<byte>, ValueTask>? handler = null,
        SynchronousDecision? decision = null, ICallbackScheduler? scheduler = null) : base(scheduler)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(native);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxItems);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(maxPayload);
        if (maxPayload > maxBytes) throw new ArgumentOutOfRangeException(nameof(maxPayload));
        if ((handler is null) == (decision is null))
            throw new ArgumentException("Exactly one notification or synchronous decision profile is required.");
        _owner = owner;
        _native = native;
        _coordinator = coordinator;
        _maxItems = maxItems;
        _maxBytes = maxBytes;
        _maxPayload = maxPayload;
        _handler = handler;
        _decision = decision;
        _pending = new Queue<Notification>(maxItems);
        _root = GCHandle.Alloc(this, GCHandleType.Normal);
        _rootLive = true;
        try { _slot = coordinator.Reserve(this); }
        catch { _root.Free(); _rootLive = false; throw; }
    }

    /// <summary>Observable handler/bridge failure after actual cleanup; not a native outcome substitute.</summary>
    internal Task Completion => _completion.Task;
    internal Task Retirement => _retirement.Task;
    internal CallbackSnapshot Snapshot
    {
        get
        {
            lock (_gate) return new(_setupSettled, _closing, _nativeQuiet, _released, _rootLive,
                _quarantined, _controls, _nativeEntries, _pending.Count, _activeHandlers,
                _retainedItems, _retainedBytes, Environment.TickCount64 - _created, _failureStatus);
        }
    }

    /// <summary>Publishes the callback table once; a callback may occur before this call returns.</summary>
    internal unsafe void Start()
    {
        lock (_gate)
        {
            if (_startCalled || _closing) throw new InvalidOperationException("Subscription start is no longer available.");
            _startCalled = true;
        }
        bool acquired = false;
        uint status = NativeStatus.ContractViolation;
        bool returned = false;
        try
        {
            nint pointer = AcquireControl(ref acquired, cleanup: true);
            NwCallbacksV1 table = new()
            {
                StructSize = (uint)sizeof(NwCallbacksV1),
                Cookie = (nuint)GCHandle.ToIntPtr(_root),
                OnEvent = &OnEvent
            };
            status = _native.Start(pointer, in table);
            returned = true;
            lock (_gate) _accepted = status == NativeStatus.Ok;
            if (status != NativeStatus.Ok)
                throw StatusException("start", status);
        }
        catch (Exception error)
        {
            lock (_gate) _indeterminate = !returned;
            RecordFailure(error, status);
            throw;
        }
        finally
        {
            ReleaseControl(ref acquired);
            lock (_gate) _setupSettled = true;
            Signal();
        }
    }

    /// <summary>Admits an ordinary generated control call; closure rejects it before native entry.</summary>
    internal nint AcquireControl(ref bool acquired) => AcquireControl(ref acquired, cleanup: false);

    private nint AcquireControl(ref bool acquired, bool cleanup)
    {
        if (acquired) throw new InvalidOperationException("A control lease cannot acquire twice.");
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_released || _releasing || (!cleanup && _closing), this);
            checked { _controls++; }
        }
        try { return _owner.Acquire(ref acquired); }
        catch
        {
            lock (_gate) _controls--;
            Signal();
            throw;
        }
    }

    internal void ReleaseControl(ref bool acquired)
        => ReleaseControl(ref acquired, notify: true);

    private void ReleaseControl(ref bool acquired, bool notify)
    {
        if (!acquired) return;
        try { _owner.Release(ref acquired); }
        finally
        {
            lock (_gate) _controls--;
            if (notify) Signal();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe uint OnEvent(nuint cookie, uint kind, void* payload, ulong bytes)
    {
        CallbackRegistration? context = null;
        bool entered = false;
        try
        {
            // Native admission/retirement guarantees this exact strong handle is
            // live. Neither nonzero nor catch makes an arbitrary handle valid.
            context = GCHandle.FromIntPtr((nint)cookie).Target as CallbackRegistration;
            if (context is null) return NativeStatus.ContractViolation;
            lock (context._gate) { checked { context._nativeEntries++; } }
            entered = true;
            return context.Receive(kind, payload, bytes);
        }
        catch (Exception error)
        {
            context?.RecordFailure(error, NativeStatus.CallbackFailed);
            return NativeStatus.CallbackFailed;
        }
        finally
        {
            if (entered)
            {
                lock (context!._gate) context._nativeEntries--;
                context.Signal();
            }
        }
    }

    private unsafe uint Receive(uint kind, void* payload, ulong bytes)
    {
        if (bytes > (ulong)_maxPayload || (bytes != 0 && payload is null) ||
            (bytes != 0 && (nuint)payload > nuint.MaxValue - (nuint)bytes))
        {
            RecordFailure(null, NativeStatus.InvalidArgument);
            return NativeStatus.InvalidArgument;
        }
        lock (_gate)
        {
            // Discarded ordinary notifications may be acknowledged. An already
            // admitted synchronous decision reaching a closed managed gate must
            // refuse; returning OK would incorrectly approve without a handler.
            if (_closing) return _decision is null ? NativeStatus.Ok : NativeStatus.CallbackFailed;
        }
        int length = (int)bytes;
        if (_decision is not null)
        {
            CallbackRegistration? previous = s_decisionScope;
            s_decisionScope = this;
            try
            {
                if (_decision(kind, new ReadOnlySpan<byte>(payload, length))) return NativeStatus.Ok;
                RecordFailure(null, NativeStatus.CallbackFailed);
                return NativeStatus.CallbackFailed;
            }
            finally { s_decisionScope = previous; }
        }
        lock (_gate)
        {
            if (_closing) return NativeStatus.Ok;
            if (_retainedItems == _maxItems || length > _maxBytes - _retainedBytes)
            {
                RecordFailureLocked(null, NativeStatus.CallbackFailed);
                return NativeStatus.CallbackFailed;
            }
            _retainedItems++;
            _retainedBytes += length;
        }
        byte[] copy;
        try { copy = new ReadOnlySpan<byte>(payload, length).ToArray(); }
        catch
        {
            lock (_gate) { _retainedItems--; _retainedBytes -= length; }
            throw;
        }
        lock (_gate)
        {
            if (_closing) { _retainedItems--; _retainedBytes -= length; }
            else _pending.Enqueue(new(kind, copy));
        }
        Signal();
        return NativeStatus.Ok;
    }

    /// <summary>Closes handler admission and discards accepted but not running notifications.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_released) return;
            _closing = true;
            // A never-started subscription has no setup or callback obligations.
            if (!_startCalled) _setupSettled = true;
            DiscardPendingLocked();
        }
        Signal();
    }

    /// <summary>Joins the same cleanup, rejecting waits from this subscription's own handler.</summary>
    public ValueTask DisposeAsync()
    {
        Dispose();
        if (ReferenceEquals(s_decisionScope, this) || ReferenceEquals(HandlerScope.Value, this))
            throw new InvalidOperationException("A callback cannot wait for its own subscription to retire.");
        return new ValueTask(_retirement.Task);
    }

    private void DiscardPendingLocked()
    {
        while (_pending.TryDequeue(out Notification item))
        {
            _retainedItems--;
            _retainedBytes -= item.Bytes.Length;
        }
    }

    private void RecordFailureLocked(Exception? error, uint status)
    {
        if (_failureStatus == NativeStatus.Ok)
        {
            _failureStatus = status == NativeStatus.Ok ? NativeStatus.ContractViolation : status;
            _failure = error;
        }
        _closing = true;
        DiscardPendingLocked();
    }

    private void RecordFailure(Exception? error, uint status)
    {
        lock (_gate) RecordFailureLocked(error, status);
        Signal();
    }

    protected override void SchedulingFault(Exception error)
    {
        // A trusted cleanup operation that throws is indeterminate. Retain its
        // owner/root rather than claiming safe retirement or spinning retries.
        lock (_gate) { RecordFailureLocked(error, NativeStatus.ContractViolation); _quarantined = true; }
    }

    protected override void Process()
    {
        Notification notification = default;
        bool dispatch = false;
        bool cleanup;
        lock (_gate)
        {
            if (_released || _quarantined || !_setupSettled) return;
            cleanup = _closing;
            if (!cleanup && _activeHandlers == 0 && _pending.TryDequeue(out notification))
            {
                _activeHandlers = 1;
                dispatch = true;
            }
        }
        if (dispatch) _ = RunHandlerAsync(notification);
        if (!cleanup) return;

        bool accepted;
        bool indeterminate;
        bool stopSent;
        bool quiet;
        lock (_gate) { accepted = _accepted; indeterminate = _indeterminate; stopSent = _stopSent; quiet = _nativeQuiet; }
        if ((accepted || indeterminate) && !quiet)
        {
            bool acquired = false;
            try
            {
                nint pointer = AcquireControl(ref acquired, cleanup: true);
                if (!stopSent)
                {
                    uint stop = _native.RequestStop(pointer);
                    if (stop != NativeStatus.Ok) throw StatusException("request_stop", stop);
                    lock (_gate) _stopSent = true;
                }
                uint retire = _native.TryRetire(pointer);
                if (retire == NativeStatus.Ok) { lock (_gate) _nativeQuiet = true; }
                else if (retire == NativeStatus.Pending) RequestRetry();
                else throw StatusException("try_retire", retire);
            }
            finally { ReleaseControl(ref acquired, notify: false); }
        }
        else if (!accepted && !indeterminate) { lock (_gate) _nativeQuiet = true; }

        Exception? failure;
        uint failureStatus;
        lock (_gate)
        {
            if (!_nativeQuiet || _controls != 0 || _nativeEntries != 0 || _activeHandlers != 0 ||
                _retainedItems != 0 || _pending.Count != 0 || _releasing) return;
            _releasing = true;
            failure = _failure;
            failureStatus = _failureStatus;
        }
        _owner.Dispose();
        _root.Free();
        lock (_gate) { _rootLive = false; _released = true; }
        CompleteScheduling();
        _coordinator.Release(_slot, this);
        _retirement.TrySetResult();
        if (failureStatus == NativeStatus.Ok) _completion.TrySetResult();
        else _completion.TrySetException(failure ?? StatusException("callback", failureStatus));
    }

    private async Task RunHandlerAsync(Notification notification)
    {
        CallbackRegistration? previous = HandlerScope.Value;
        HandlerScope.Value = this;
        try { await _handler!(notification.Kind, notification.Bytes).ConfigureAwait(false); }
        catch (Exception error) { RecordFailure(error, NativeStatus.CallbackFailed); }
        finally
        {
            HandlerScope.Value = previous;
            lock (_gate)
            {
                _activeHandlers--;
                _retainedItems--;
                _retainedBytes -= notification.Bytes.Length;
            }
            Signal();
        }
    }

    private static Exception StatusException(string operation, uint status)
    {
        NwErrorInfoV1 record = new() { StructSize = 24 };
        return NativeError.Create("callback", operation, status, in record);
    }

    private readonly record struct Notification(uint Kind, byte[] Bytes);
}

internal readonly record struct CallbackSnapshot(bool SetupSettled, bool Closing, bool NativeQuiet,
    bool Released, bool RootLive, bool Quarantined, int Controls, int NativeEntries, int Pending,
    int ActiveHandlers, int RetainedItems, int RetainedBytes, long AgeMilliseconds, uint FailureStatus);
