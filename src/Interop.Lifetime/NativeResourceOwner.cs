using System.Runtime.InteropServices;

namespace NativeWeave.Interop.Lifetime;

/// <summary>Internal D04 logical admission and physical SafeHandle leases.</summary>
/// <remarks>
/// Construction transfers the sole SafeHandle ownership only on success. Native
/// destructors must be nonthrowing, nonblocking and valid on any release thread.
/// A lease uses a caller-local Boolean in generated try/finally; it allocates no
/// lease object. Domain callbacks and native work run outside the admission lock.
/// No finalizer is added: the supplied SafeHandle owns the bounded fallback.
/// </remarks>
internal sealed class NativeResourceOwner : IDisposable, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly SafeHandle _handle;
    private readonly object? _lifetimeRoot;
    private int _active;
    private bool _closing;
    private bool _ownerDropped;
    private bool _retired;
    private TaskCompletionSource? _retirement;

    internal NativeResourceOwner(SafeHandle handle, object? lifetimeRoot = null)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (handle.IsClosed || handle.IsInvalid)
            throw new ArgumentException("A valid, uniquely owned, open SafeHandle is required.", nameof(handle));
        _handle = handle;
        _lifetimeRoot = lifetimeRoot;
    }

    /// <summary>True after logical closure, including after final physical release.</summary>
    internal bool IsClosing { get { lock (_gate) return _closing; } }

    /// <summary>Completes only after owner disposal and every physical lease release return.</summary>
    internal Task Retirement
    {
        get
        {
            lock (_gate)
            {
                if (_retired) return Task.CompletedTask;
                return (_retirement ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }
        }
    }

    /// <summary>Acquires logical admission and a physical reference under one gate.</summary>
    /// <param name="acquired">A caller-local false flag, set only after successful acquisition.</param>
    /// <returns>The borrowed pointer; valid only until the matching Release.</returns>
    internal nint Acquire(ref bool acquired)
    {
        if (acquired) throw new InvalidOperationException("A lease flag cannot acquire twice.");
        bool logical = false;
        bool physical = false;
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_closing, this);
                TestBoundary?.Invoke(LeaseBoundary.OpenChecked);
                checked { _active++; }
                logical = true;
                TestBoundary?.Invoke(LeaseBoundary.LogicalAcquired);
                _handle.DangerousAddRef(ref physical);
                TestBoundary?.Invoke(LeaseBoundary.PhysicalAcquired);
                nint pointer = _handle.DangerousGetHandle();
                TestBoundary?.Invoke(LeaseBoundary.PointerRead);
                acquired = true;
                return pointer;
            }
        }
        catch
        {
            // This rollback is outside the gate, even when acquisition failed
            // after AddRef. An earlier logical increment is always balanced.
            try { if (physical) _handle.DangerousRelease(); }
            finally
            {
                if (logical) EndLogicalLease();
                GC.KeepAlive(_lifetimeRoot);
            }
            throw;
        }
    }

    /// <summary>Releases one acquired flag; repeated release of that same flag is harmless.</summary>
    internal void Release(ref bool acquired)
    {
        if (!acquired) return;
        acquired = false;
        // DangerousRelease may run the creating module's destructor. Never do
        // that under the admission lock, and observe retirement only afterwards.
        try { _handle.DangerousRelease(); }
        finally
        {
            EndLogicalLease();
            GC.KeepAlive(_lifetimeRoot);
        }
    }

    private void EndLogicalLease()
    {
        TaskCompletionSource? complete;
        lock (_gate)
        {
            if (_active <= 0) throw new InvalidOperationException("Unbalanced internal resource lease.");
            _active--;
            complete = ObserveRetirement();
        }
        complete?.TrySetResult();
    }

    // Must be called under _gate. The completion is published outside the gate.
    private TaskCompletionSource? ObserveRetirement()
    {
        if (!_retired && _closing && _ownerDropped && _active == 0)
        {
            _retired = true;
            return _retirement;
        }
        return null;
    }

    /// <summary>Closes new admission and drops ownership without waiting for admitted calls.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_closing) return;
            _closing = true;
        }
        _handle.Dispose();
        GC.KeepAlive(_lifetimeRoot);
        TaskCompletionSource? complete;
        lock (_gate)
        {
            _ownerDropped = true;
            complete = ObserveRetirement();
        }
        complete?.TrySetResult();
    }

    /// <summary>Requests closure and observes actual physical resource retirement.</summary>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return new ValueTask(Retirement);
    }

    // The per-instance conformance hook is null on production adapters. Tests
    // alone install deterministic barriers/failure injection at the actual gate;
    // this is neither a user callback nor a domain extension point.
    internal Action<LeaseBoundary>? TestBoundary { get; set; }
    internal int ActiveLeases { get { lock (_gate) return _active; } }
}

internal enum LeaseBoundary
{
    OpenChecked,
    LogicalAcquired,
    PhysicalAcquired,
    PointerRead
}
