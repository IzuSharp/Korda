using NativeWeave.Interop.Abi;

namespace NativeWeave.Interop;

/// <summary>Internal fixed-record mapping; domain adapters own their richer error and outcome types.</summary>
internal static class NativeError
{
    internal static Exception Create(string engine, string operation, uint status, in NwErrorInfoV1 record,
        CancellationToken cancellationToken = default)
    {
        if (status == NativeStatus.Ok)
            throw new ArgumentException("Success has no error.", nameof(status));
        if (status == NativeStatus.Pending)
            return new NativeEngineException(engine, operation, NativeStatus.ContractViolation, 0, 0, 0,
                "Unexpected pending status on a terminal operation.");
        if (record.StructSize != 24 || record.Flags != 0)
            return new NativeEngineException("A native failure returned an incomplete or unsupported diagnostic record; original status: " + status + ".");
        return status switch
        {
            NativeStatus.Canceled => new OperationCanceledException("The native operation reported terminal cancellation.", cancellationToken),
            NativeStatus.InvalidArgument => new ArgumentException("The native adapter rejected the supplied arguments."),
            NativeStatus.InvalidState => new InvalidOperationException("The native operation is not legal in the current state."),
            NativeStatus.NotSupported => new NotSupportedException("The selected native capability is unavailable."),
            NativeStatus.Timeout => new TimeoutException("The native operation reported a timeout; remote effects require domain inspection."),
            _ => new NativeEngineException(engine, operation, status, record.Domain, record.Code, record.CorrelationId,
                "The native operation failed. Inspect its bounded status and domain diagnostics; do not retry implicitly."),
        };
    }
}

/// <summary>Preallocated bridge-failure state. It never rewrites an already-published native outcome.</summary>
internal sealed class CallbackFailureLatch(uint originalTerminalStatus)
{
    private int _failed;
    private int _cleanupSignals;
    internal uint OriginalTerminalStatus { get; } = originalTerminalStatus;
    internal bool HasFailure => Volatile.Read(ref _failed) != 0;
    internal int CleanupSignals => Volatile.Read(ref _cleanupSignals);

    internal uint Notify(Action bridge)
    {
        try
        {
            bridge();
            return NativeStatus.Ok;
        }
        catch (Exception)
        {
            // No allocation, formatting, logging or exception transfer across the ABI.
            Interlocked.Exchange(ref _failed, 1);
            return NativeStatus.CallbackFailed;
        }
        finally
        {
            // The real async owner supplies its pre-reserved cleanup slot in A06/A07.
            Interlocked.Increment(ref _cleanupSignals);
        }
    }
}
