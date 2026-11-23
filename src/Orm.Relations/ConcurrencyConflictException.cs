namespace Korda.Data;

/// <summary>A native optimistic write did not match its exact expected affected-row count.</summary>
/// <remarks>The original key and expected token predicate ran once in the database. This exception never authorizes a write replay.</remarks>
public sealed class ConcurrencyConflictException : DatabaseException
{
    /// <summary>Creates an immutable expected/actual count diagnostic.</summary>
    /// <param name="message">The non-null diagnostic message.</param>
    /// <param name="expectedRows">The nonnegative expected affected-row count.</param>
    /// <param name="actualRows">The nonnegative actual affected-row count.</param>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A count is negative.</exception>
    public ConcurrencyConflictException(string message, long expectedRows, long actualRows)
        : base(message, DatabaseErrorKind.Constraint)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRows); ArgumentOutOfRangeException.ThrowIfNegative(actualRows);
        ExpectedRows = expectedRows; ActualRows = actualRows;
    }
    /// <summary>Gets the expected affected-row count.</summary>
    public long ExpectedRows { get; }
    /// <summary>Gets the actual affected-row count.</summary>
    public long ActualRows { get; }
}
