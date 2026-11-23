using Korda.Interop.Async;

namespace Korda.Interop.Execution;

/// <summary>Observed native worker join and outstanding-owner counters for one engine instance.</summary>
/// <remarks>The concrete engine's typed close take supplies these values only after its
/// runtime owner has actually joined workers. The A07 retirement acknowledgement also
/// covers any close observer. A timeout, dropped handle or stopped admission is insufficient.</remarks>
internal readonly record struct ExecutorJoinResult(bool Joined, ulong WorkersStarted, ulong WorkersStopped,
    ulong OutstandingOperations, ulong ReservedBytes);

/// <summary>Engine-owned close observation whose canceled waits preserve actual cleanup.</summary>
/// <remarks>Construction starts no native runtime or close worker. The owning engine supplies
/// one preallocated A07 close operation with an uncancelable cleanup lifetime and one trusted
/// callback-scope query. RequestClose starts it once. Successful completion requires both
/// typed native join evidence and A07 native/managed retirement. The engine must mark every
/// callback/mapper that contributes to its own drain; such a scope may request close but cannot
/// await it. No admission mutex is held across the scope query, native start or await.</remarks>
internal sealed class ExecutorLifetime : IDisposable, IAsyncDisposable
{
    private readonly AsyncOperation<ExecutorJoinResult> _close;
    private readonly Func<bool> _isInOwnedCallback;
    private readonly Task<ExecutorJoinResult> _completion;
    private int _requested;

    /// <summary>Adopts the engine's reserved close-control operation and trusted scope query.</summary>
    /// <remarks>The supplied operation must be unstarted and have no cancellation token.
    /// Business admission must reserve this separate close slot before admitting work.</remarks>
    internal ExecutorLifetime(AsyncOperation<ExecutorJoinResult> close, Func<bool> isInOwnedCallback)
    {
        ArgumentNullException.ThrowIfNull(close);
        ArgumentNullException.ThrowIfNull(isInOwnedCallback);
        if (close.Snapshot.Start != AsyncStartOutcome.NotStarted || close.Snapshot.SetupSettled)
            throw new ArgumentException("The close operation must be unstarted.", nameof(close));
        _close = close;
        _isInOwnedCallback = isInOwnedCallback;
        _completion = CompleteAfterRetirement();
    }

    /// <summary>Whether one caller has requested close; this does not prove resource release.</summary>
    internal bool CloseRequested => Volatile.Read(ref _requested) != 0;

    /// <summary>Fixed A07 diagnostics, including retained root and quarantine, for this close.</summary>
    internal AsyncSnapshot Snapshot => _close.Snapshot;

    /// <summary>Idempotently closes admission and requests native stop without blocking a callback.</summary>
    internal void RequestClose()
    {
        if (Interlocked.CompareExchange(ref _requested, 1, 0) == 0) _close.Start();
    }

    /// <summary>Waits for actual close; canceling this wait leaves the same cleanup operation rooted.</summary>
    /// <param name="waitCancellation">Cancels only this caller's observation, never native cleanup.</param>
    /// <returns>The actual joined-worker counters after independent A07 retirement.</returns>
    /// <exception cref="InvalidOperationException">An owned callback attempts to await its own drain.</exception>
    internal Task<ExecutorJoinResult> CloseAsync(CancellationToken waitCancellation = default)
    {
        RequestClose();
        if (_isInOwnedCallback())
            throw new InvalidOperationException("An engine callback cannot wait for its own cleanup; request close instead.");
        return waitCancellation.CanBeCanceled ? _completion.WaitAsync(waitCancellation) : _completion;
    }

    private async Task<ExecutorJoinResult> CompleteAfterRetirement()
    {
        ExecutorJoinResult result;
        try { result = await _close.Task.ConfigureAwait(false); }
        finally { await _close.Retirement.ConfigureAwait(false); }
        if (!result.Joined || result.WorkersStarted == 0 || result.WorkersStarted != result.WorkersStopped ||
            result.OutstandingOperations != 0 || result.ReservedBytes != 0)
            throw new InvalidOperationException("The native close did not prove worker join and zero outstanding owners.");
        return result;
    }

    /// <summary>Requests close and returns immediately; existing cleanup retains its owner.</summary>
    public void Dispose() => RequestClose();

    /// <summary>Completes only after actual native worker join and native/managed drain.</summary>
    public ValueTask DisposeAsync() => new(CloseAsync());
}
