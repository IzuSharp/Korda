namespace Korda.Data;

/// <summary>Provider-neutral database failure categories; cancellation and timeouts use their standard CLR exceptions.</summary>
public enum DatabaseErrorKind
{
    /// <summary>The provider reported a failure.</summary>
    Provider,
    /// <summary>A database constraint or expected affected-row condition failed.</summary>
    Constraint,
    /// <summary>A value cannot be represented exactly by its declared codec.</summary>
    Conversion,
    /// <summary>The complete result shape differs from the declared projection.</summary>
    ShapeMismatch,
    /// <summary>The selected engine or provider cannot perform the requested operation.</summary>
    UnsupportedCapability,
    /// <summary>A configured finite resource limit was exceeded.</summary>
    ResourceLimit,
    /// <summary>A single-command owner already admits another operation.</summary>
    ConcurrentUse,
    /// <summary>The operation is invalid for the owner's current state or metadata.</summary>
    InvalidState,
    /// <summary>The external outcome cannot be established; automatic retry is unsafe.</summary>
    UnknownOutcome
}

/// <summary>Preserves a bounded provider-neutral failure and correlation identity.</summary>
/// <remarks>Diagnostic values are immutable and remain readable after cleanup. Construction loads no native engine.
/// A transient cause is not permission to retry a write; outcome and application idempotency remain separate.</remarks>
public class DatabaseException : Exception
{
    /// <summary>Creates a database failure without inferring provider transience.</summary>
    /// <param name="message">The diagnostic message, without credentials or parameter values.</param>
    /// <param name="kind">The provider-neutral category.</param>
    /// <param name="providerCode">An optional provider error code.</param>
    /// <param name="correlationId">The native diagnostic correlation, or zero when absent.</param>
    /// <param name="innerException">The original cause, if available.</param>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is undefined.</exception>
    public DatabaseException(string message, DatabaseErrorKind kind, string? providerCode = null, ulong correlationId = 0, Exception? innerException = null)
        : base(message ?? throw new ArgumentNullException(nameof(message)), innerException)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        Kind = kind;
        ProviderCode = providerCode;
        CorrelationId = correlationId;
    }

    /// <summary>Gets the immutable provider-neutral category.</summary>
    public DatabaseErrorKind Kind { get; }
    /// <summary>Gets the optional provider code without interpreting it as retry authorization.</summary>
    public string? ProviderCode { get; }
    /// <summary>Gets the native correlation identity, or zero when absent.</summary>
    public ulong CorrelationId { get; }
    /// <summary>Gets the provider's explicit classification; unknown outcomes are always nontransient.</summary>
    public bool IsTransient => Kind != DatabaseErrorKind.UnknownOutcome && _isTransient;
    private readonly bool _isTransient;

    internal DatabaseException(string message, DatabaseErrorKind kind, string? providerCode, ulong correlationId, bool transient)
        : this(message, kind, providerCode, correlationId) => _isTransient = transient;

    internal DatabaseException(string message, DatabaseErrorKind kind, string? providerCode, ulong correlationId, bool transient, Exception innerException)
        : this(message, kind, providerCode, correlationId, innerException) => _isTransient = transient;
}

/// <summary>Identifies a required capability missing from an engine or provider profile.</summary>
/// <remarks>The owned exception retains validated managed identity strings and no native handle.
/// Constructing or reading it performs no provider call; capability admission rejects before business dispatch.</remarks>
public sealed class DatabaseCapabilityException : DatabaseException
{
    /// <summary>Creates a failure that must be reported before a native business side effect.</summary>
    /// <param name="capability">The nonempty stable capability name.</param>
    /// <param name="provider">The nonempty provider identity.</param>
    /// <exception cref="ArgumentException">An identity is empty or contains invalid Unicode.</exception>
    /// <exception cref="ArgumentNullException">An identity is null.</exception>
    public DatabaseCapabilityException(string capability, string provider)
        : base($"Provider '{provider}' does not support '{capability}'.", DatabaseErrorKind.UnsupportedCapability)
    {
        Capability = Validation.Name(capability, nameof(capability));
        Provider = Validation.Name(provider, nameof(provider));
    }
    /// <summary>Gets the exact required capability name.</summary>
    public string Capability { get; }
    /// <summary>Gets the provider identity.</summary>
    public string Provider { get; }
}
