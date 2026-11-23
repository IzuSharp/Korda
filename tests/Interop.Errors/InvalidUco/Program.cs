using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// Deliberately illegal under enabled runtime marshalling. Isolated negative only.
internal static unsafe partial class Program
{
    [LibraryImport("korda_errors_fixture", EntryPoint = "nw_errors_invalid_uco")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial uint Invoke(nint callback);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static bool Invalid(char input)
    {
        Console.WriteLine("INVALID_CALLBACK_BODY_ENTERED");
        return input == 'x';
    }

    private static int Main()
    {
        Console.WriteLine("CALLING_INVALID_UCO");
        return unchecked((int)Invoke((nint)(delegate* unmanaged[Cdecl]<char, bool>)&Invalid));
    }
}
