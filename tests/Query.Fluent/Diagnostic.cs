using System.Runtime.InteropServices;

internal sealed class Diagnostic : IDisposable
{
    private readonly nint _library;
    private readonly nint _count;
    internal Diagnostic(string path)
    {
        _library = NativeLibrary.Load(path);
        _count = NativeLibrary.GetExport(_library, "nw_db_diagnostic_count_v1");
    }
    internal unsafe ulong[] Counts()
    {
        var result = new ulong[6]; for (uint i = 0; i < result.Length; i++) result[i] = ((delegate* unmanaged[Cdecl]<uint, ulong>)_count)(i); return result;
    }
    internal async Task WaitCursorZero()
    {
        for (var i = 0; i < 100; i++)
        {
            var counts = Counts(); if (counts[2] == 0 && counts[3] == 0 && counts[4] == 0) { Suite.Check(true, "Native operations/cursors/buffers zero"); return; }
            await Task.Delay(10);
        }
        throw new InvalidOperationException("Native cursor leak: " + string.Join(',', Counts()));
    }
    internal async Task WaitAllZero()
    {
        for (var i = 0; i < 100; i++)
        {
            if (Counts().All(v => v == 0)) { Suite.Check(true, "All six actual native owner counters zero"); return; }
            await Task.Delay(10);
        }
        throw new InvalidOperationException("Native owner leak: " + string.Join(',', Counts()));
    }
    public void Dispose() => NativeLibrary.Free(_library);
}
