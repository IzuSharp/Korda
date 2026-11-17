using System.Text;

namespace NativeWeave.Analyzers.Example;

internal static class AdvancedExample
{
    internal static unsafe string Greet(string value)
    {
        byte[] name = Encoding.UTF8.GetBytes(value);
        byte[] output = new byte[4096 + 32];
        nuint written = 0;
        fixed (byte* namePointer = name)
        fixed (byte* outputPointer = output)
        {
            // This accepted bootstrap function copies into caller-owned storage
            // synchronously and retains neither pointer after returning.
            uint status = NativeMethods.Greet(namePointer, (nuint)name.Length, outputPointer, (nuint)output.Length, &written);
            if (status != 0 || written > (nuint)output.Length) throw new InvalidOperationException("Native greeting failed or returned an invalid extent.");
        }
        return Encoding.UTF8.GetString(output.AsSpan(0, checked((int)written)));
    }
}
