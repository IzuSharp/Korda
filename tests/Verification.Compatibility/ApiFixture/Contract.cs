namespace NativeWeave.Compatibility.Conformance;

/// <summary>Unpublished compiled evolution fixtures, never a product facade.</summary>
/// <remarks>All calls are synchronous, thread-safe and retain no state or ownership. Scalar operations allocate no memory; Normalize returns its input reference. No native call, cancellation, resource or executor is involved.</remarks>
public static class Contract
{
#if !REMOVED
    /// <summary>Adds the declared optional offset.</summary>
    /// <param name="value">The input value.</param>
    /// <param name="offset">The offset applied synchronously.</param>
    /// <returns>The sum without retained state.</returns>
#if OBSOLETE
    [System.Obsolete("Use Sum(value, offset) before the next major version.", false, DiagnosticId = "NWCOMP001")]
#endif
#if DEFAULT
    public static int Add(int value, int offset = 8) => value + offset;
#elif BEHAVIOR
    public static int Add(int value, int offset = 7) => value + offset + 1;
#else
    public static int Add(int value, int offset = 7) => value + offset;
#endif
#endif
    /// <summary>Preserves the input reference without retaining it.</summary>
    /// <param name="value">The caller's value.</param>
    /// <returns>The input value.</returns>
#if NULLABILITY
    public static string Normalize(string value) => value;
#else
    public static string? Normalize(string? value) => value;
#endif

    /// <summary>Copies a bounded unmanaged value.</summary>
    /// <typeparam name="T">The caller's statically selected value type.</typeparam>
    /// <param name="value">The value to copy.</param>
    /// <returns>The supplied value.</returns>
#if GENERIC
    public static T Echo<T>(T value) where T : struct => value;
#else
    public static T Echo<T>(T value) where T : unmanaged => value;
#endif

    /// <summary>Returns zero for a missing name.</summary>
    /// <param name="key">The optional lookup key.</param>
    /// <returns>The key length or zero.</returns>
    public static int Lookup(string? key) => key?.Length ?? 0;

#if NEW
    /// <summary>Reports the optional fixture generation.</summary>
    /// <returns>Two for the additive fixture.</returns>
    public static int Generation() => 2;
#endif
#if AMBIGUOUS
    /// <summary>A deliberately source-incompatible overload.</summary>
    /// <param name="key">The optional URI.</param>
    /// <returns>The URI length or zero.</returns>
    public static int Lookup(System.Uri? key) => key?.OriginalString.Length ?? 0;
#endif
#if OBSOLETE
    /// <summary>Demonstrates an explicit migration destination.</summary>
    /// <param name="value">The input.</param>
    /// <param name="offset">The required offset; no new default is implied.</param>
    /// <returns>The sum.</returns>
    public static int Sum(int value, int offset) => value + offset;
#endif
}
