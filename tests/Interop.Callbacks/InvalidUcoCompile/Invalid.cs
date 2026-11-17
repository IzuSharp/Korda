using System.Runtime.InteropServices;

internal static class Invalid
{
    [UnmanagedCallersOnly]
    internal static void Callback(string value) => throw new InvalidOperationException(value);
}
