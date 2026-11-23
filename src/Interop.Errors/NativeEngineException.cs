namespace Korda.Interop;

/// <summary>A native panic, poisoned engine or interop contract failure with optional native diagnostics.</summary>
/// <remarks>Construction is a cold error path. Instances own immutable managed diagnostic values and retain no native owner.
/// Ordinary domain errors use their domain exception. A failure never proves rollback, retirement or safe retry.</remarks>
public class NativeEngineException : Exception
{
    /// <summary>Creates an exception without native diagnostic metadata.</summary>
    public NativeEngineException() { }

    /// <summary>Creates an exception without native diagnostic metadata.</summary>
    /// <param name="message">The diagnostic message, or null.</param>
    public NativeEngineException(string? message) : base(message) { }

    /// <summary>Preserves a managed cause without inventing a native record.</summary>
    /// <param name="message">The diagnostic message, or null.</param>
    /// <param name="innerException">The managed cause, or null.</param>
    public NativeEngineException(string? message, Exception? innerException) : base(message, innerException) { }

    private protected NativeEngineException(string engineId, string operation, string? message, Exception? innerException)
        : base(message, innerException)
    {
        ErrorIdentity.ValidateEngineId(engineId);
        ArgumentException.ThrowIfNullOrEmpty(operation);
        EngineId = engineId;
        Operation = operation;
    }

    /// <summary>Creates an exception preserving a complete native failure record.</summary>
    /// <param name="engineId">The ordinal engine identifier, using the documented ASCII identity grammar.</param>
    /// <param name="operation">The nonempty stable adapter operation name.</param>
    /// <param name="statusCode">The original wire failure, including unknown values; zero and one are invalid.</param>
    /// <param name="errorDomain">The original unsigned error domain.</param>
    /// <param name="nativeCode">The original signed provider code.</param>
    /// <param name="correlationId">The original opaque correlation value; zero is permitted.</param>
    /// <param name="message">A bounded, redacted diagnostic message, or null.</param>
    /// <param name="innerException">The managed bridge cause, or null.</param>
    /// <exception cref="ArgumentException">An identity or operation is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The status denotes success or pending.</exception>
    public NativeEngineException(string engineId, string operation, uint statusCode, uint errorDomain,
        int nativeCode, ulong correlationId, string? message, Exception? innerException = null)
        : this(engineId, operation, message, innerException)
    {
        if (statusCode <= 1)
            throw new ArgumentOutOfRangeException(nameof(statusCode), "A native failure cannot be success or pending.");
        StatusCode = statusCode;
        ErrorDomain = errorDomain;
        NativeCode = nativeCode;
        CorrelationId = correlationId;
    }

    /// <summary>Gets the immutable engine identity, or null without engine context.</summary>
    public string? EngineId { get; }
    /// <summary>Gets the immutable operation name, or null without engine context.</summary>
    public string? Operation { get; }
    /// <summary>Gets the unmodified wire status, or null without a native record.</summary>
    public uint? StatusCode { get; }
    /// <summary>Gets the original error domain, or null without a native record.</summary>
    public uint? ErrorDomain { get; }
    /// <summary>Gets the signed provider code, or null without a native record.</summary>
    public int? NativeCode { get; }
    /// <summary>Gets the correlation value, or null without a native record.</summary>
    public ulong? CorrelationId { get; }
}

/// <summary>Classifies a managed module preflight failure; these values are not C ABI status values.</summary>
public enum EngineLoadFailure
{
    /// <summary>The current runtime identifier has no supported asset.</summary>
    UnsupportedPlatform = 1,
    /// <summary>The native asset or an OS dependency is missing.</summary>
    MissingAsset = 2,
    /// <summary>The format, architecture or CPU baseline is incompatible.</summary>
    InvalidBinary = 3,
    /// <summary>Content differs from the package manifest.</summary>
    AssetIntegrityMismatch = 4,
    /// <summary>A required bootstrap export is absent.</summary>
    MissingExport = 5,
    /// <summary>The ABI major or required record shape is incompatible.</summary>
    IncompatibleAbi = 6,
    /// <summary>A required capability is absent.</summary>
    MissingCapability = 7,
    /// <summary>Another content identity was selected for this engine.</summary>
    ConflictingModule = 8,
    /// <summary>The manifest is malformed or contradicts the selected asset.</summary>
    InvalidManifest = 9,
}

/// <summary>An actionable loading failure before domain resources are published.</summary>
/// <remarks>EngineId and Operation identify preflight; native-record fields stay null. No fallback or retry is performed.</remarks>
public sealed class EngineLoadException : NativeEngineException
{
    /// <summary>Creates a managed preflight diagnostic without fabricating a native record.</summary>
    /// <param name="failure">A defined load-failure classification.</param>
    /// <param name="engineId">The requested engine identity.</param>
    /// <param name="runtimeIdentifier">The requested nonempty runtime identifier.</param>
    /// <param name="message">A redacted message with an installation or compatibility remedy.</param>
    /// <param name="innerException">The optional loader cause.</param>
    /// <exception cref="ArgumentException">An identity, runtime identifier or message is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The failure classification is unknown.</exception>
    public EngineLoadException(EngineLoadFailure failure, string engineId, string runtimeIdentifier,
        string message, Exception? innerException = null) : base(engineId, "load", message, innerException)
    {
        if (failure < EngineLoadFailure.UnsupportedPlatform || failure > EngineLoadFailure.InvalidManifest)
            throw new ArgumentOutOfRangeException(nameof(failure));
        ArgumentException.ThrowIfNullOrEmpty(runtimeIdentifier);
        ArgumentNullException.ThrowIfNull(message);
        Failure = failure;
        RuntimeIdentifier = runtimeIdentifier;
    }

    /// <summary>Gets the immutable preflight classification.</summary>
    public EngineLoadFailure Failure { get; }
    /// <summary>Gets the requested runtime identifier.</summary>
    public string RuntimeIdentifier { get; }
}

internal static class ErrorIdentity
{
    internal static void ValidateEngineId(string engineId)
    {
        ArgumentNullException.ThrowIfNull(engineId);
        if (engineId.Length is < 1 or > 128 || engineId[0] is < 'a' or > 'z')
            throw new ArgumentException("Use an ASCII engine identity of 1 to 128 characters.", nameof(engineId));
        bool separator = false;
        foreach (char c in engineId)
        {
            if (c is '.' or '-')
            {
                if (separator)
                    throw new ArgumentException("Identity segments must be nonempty.", nameof(engineId));
                separator = true;
            }
            else
            {
                if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9'))
                    throw new ArgumentException("Identity contains an unsupported character.", nameof(engineId));
                separator = false;
            }
        }
        if (separator)
            throw new ArgumentException("Identity segments must be nonempty.", nameof(engineId));
    }
}
