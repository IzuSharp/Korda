namespace NativeWeave.Interop.Callbacks;

/// <summary>Engine-owned bounded retry slots; constructing Core alone creates no scheduler.</summary>
/// <remarks>One timer serves a fixed number of admitted contexts. Each context owns at most
/// one ThreadPool work item. Rejected scheduling leaves its dirty state intact for retry.
/// Closing stops new reservations but keeps existing contexts rooted until their actual drain.</remarks>
internal sealed class CallbackCoordinator : IDisposable
{
    private readonly object _gate = new();
    private readonly CoordinatedCallback?[] _slots;
    private readonly Timer _timer;
    private bool _closing;
    private int _count;

    internal CallbackCoordinator(int capacity, TimeSpan? retryInterval = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        TimeSpan interval = retryInterval ?? TimeSpan.FromMilliseconds(10);
        if (interval <= TimeSpan.Zero || interval > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(retryInterval));
        _slots = new CoordinatedCallback[capacity];
        _timer = new Timer(static state => ((CallbackCoordinator)state!).Retry(), this, interval, interval);
    }

    internal int Count { get { lock (_gate) return _count; } }

    internal int Reserve(CoordinatedCallback context)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            for (int i = 0; i < _slots.Length; i++)
            {
                if (_slots[i] is not null) continue;
                _slots[i] = context;
                _count++;
                return i;
            }
        }
        throw new InvalidOperationException("The engine's callback cleanup capacity is exhausted.");
    }

    internal void Release(int slot, CoordinatedCallback context)
    {
        bool stopTimer;
        lock (_gate)
        {
            if (!ReferenceEquals(_slots[slot], context))
                throw new InvalidOperationException("A callback cleanup slot has a different owner.");
            _slots[slot] = null;
            _count--;
            stopTimer = _closing && _count == 0;
        }
        if (stopTimer) _timer.Dispose();
    }

    private void Retry()
    {
        // Read one slot at a time: no per-tick array/list allocation and no calls
        // into the context while holding the coordinator's reservation gate.
        for (int i = 0; i < _slots.Length; i++)
        {
            CoordinatedCallback? context;
            lock (_gate) context = _slots[i];
            context?.Retry();
        }
    }

    /// <summary>Closes reservation admission; existing cleanup remains rooted and runnable.</summary>
    public void Dispose()
    {
        bool stopTimer;
        lock (_gate) { _closing = true; stopTimer = _count == 0; }
        if (stopTimer) _timer.Dispose();
    }
}

/// <summary>A trusted engine scheduler accepts an existing work item or explicitly rejects it.</summary>
/// <remarks>Implementations never run it inline, throw after accepting it, or duplicate acceptance.</remarks>
internal interface ICallbackScheduler
{
    bool TryQueue(IThreadPoolWorkItem item);
}

internal sealed class ThreadPoolCallbackScheduler : ICallbackScheduler
{
    internal static readonly ThreadPoolCallbackScheduler Instance = new();
    public bool TryQueue(IThreadPoolWorkItem item) => ThreadPool.UnsafeQueueUserWorkItem(item, preferLocal: false);
}

/// <summary>A preallocated, coalescing scheduling slot shared by callbacks and task retirement.</summary>
internal abstract class CoordinatedCallback : IThreadPoolWorkItem
{
    private readonly object _scheduleGate = new();
    private readonly ICallbackScheduler _scheduler;
    private bool _dirty;
    private bool _scheduled;
    private bool _completed;
    private bool _retry;
    private long _rejections;

    protected CoordinatedCallback(ICallbackScheduler? scheduler) =>
        _scheduler = scheduler ?? ThreadPoolCallbackScheduler.Instance;

    internal long SchedulingRejections => Interlocked.Read(ref _rejections);

    internal void Retry()
    {
        lock (_scheduleGate)
        {
            if (_completed || _scheduled || (!_dirty && !_retry)) return;
        }
        Signal();
    }

    protected void RequestRetry()
    {
        lock (_scheduleGate) _retry = true;
    }

    internal void Signal()
    {
        lock (_scheduleGate)
        {
            if (_completed) return;
            _dirty = true;
            if (_scheduled) return;
            _scheduled = true;
        }
        bool accepted;
        try { accepted = _scheduler.TryQueue(this); }
        catch (Exception) { accepted = false; }
        if (!accepted)
        {
            Interlocked.Increment(ref _rejections);
            lock (_scheduleGate) _scheduled = false;
        }
    }

    void IThreadPoolWorkItem.Execute()
    {
        lock (_scheduleGate)
        {
            if (_completed) { _scheduled = false; return; }
            _dirty = false;
            _retry = false;
        }
        try { Process(); }
        catch (Exception error) { SchedulingFault(error); }
        bool again;
        lock (_scheduleGate)
        {
            // Dirty and scheduled change under the same gate. A notification
            // between Process and this point cannot be overwritten by a stale read.
            _scheduled = false;
            again = _dirty && !_completed;
        }
        if (again) Signal();
    }

    protected void CompleteScheduling()
    {
        lock (_scheduleGate) { _completed = true; _dirty = false; _retry = false; }
    }

    protected abstract void Process();
    protected abstract void SchedulingFault(Exception error);
}
