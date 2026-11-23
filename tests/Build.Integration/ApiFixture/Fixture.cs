namespace Korda.Build.Conformance;

/// <summary>Compiled signature/XML conformance cases, never a distributed application API.</summary>
/// <remarks>Stateless, thread safe and allocation free; no native owners or cancellation apply.</remarks>
public static class Fixture
{
    /// <summary>Adds a documented optional offset.</summary>
    /// <param name="value">The value in arbitrary fixture units.</param>
    /// <param name="offset">The offset; the reviewed default is 37.</param>
    /// <returns>The sum.</returns>
    /// <exception cref="System.OverflowException">The sum cannot fit in an integer.</exception>
    public static int Add(int value, int offset = 37) => checked(value + offset);

    /// <summary>Preserves the explicit nullable contract.</summary>
    /// <param name="value">A possibly absent string.</param>
    /// <returns>The same string reference, or null.</returns>
    public static string? Nullable(string? value) => value;

    /// <summary>Preserves a closed unmanaged generic value.</summary>
    /// <typeparam name="T">A supported unmanaged value type.</typeparam>
    /// <param name="value">The value to return.</param>
    /// <returns>The unchanged value.</returns>
    public static T Echo<T>(T value) where T : unmanaged => value;
}
