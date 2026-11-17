using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// Isolated fatal negative with runtime marshalling enabled. Body catch cannot
// repair an invalid reverse-P/Invoke ABI, because the body is never entered.
internal static unsafe partial class Program
{
    [LibraryImport("nativeweave_interop_callbacks", EntryPoint = "nw_callbacks_fixture_invalid_uco_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial uint Invoke(nint callback);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static bool Invalid(char input)
    {
        try { Console.WriteLine("INVALID_CALLBACK_BODY_ENTERED"); return input == 'x'; }
        catch (Exception) { return false; }
    }
    private static int Main()
    {
        Console.WriteLine("CALLING_INVALID_UCO");
        return unchecked((int)Invoke((nint)(delegate* unmanaged[Cdecl]<char, bool>)&Invalid));
    }
}
