using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Korda.Interop.Abi;

namespace Korda.Verification.Interop;

/// <summary>Statically rooted maintainer-only ABI adapters; all buffers are borrowed for one call.</summary>
internal static unsafe partial class Native
{
    private const string Library = "korda_verification_interop";
    [LibraryImport(Library, EntryPoint = "nw_a20_layout_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ulong Layout(uint index);
    [LibraryImport("korda_verification_interop_c", EntryPoint = "nw_a20_c_layout_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ulong CLayout(uint index);
    [LibraryImport(Library, EntryPoint = "nw_a20_extent_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint Extent(ulong address, ulong length, ulong count, ulong width, ulong offset, ulong capacity, ulong limit, uint alignment, out ulong start);
    [LibraryImport(Library, EntryPoint = "nw_a20_utf8_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint Utf8(byte* input, ulong length, uint limit, byte* output, uint capacity, out ulong written);
    [LibraryImport(Library, EntryPoint = "nw_a20_error_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint Error(uint status, NwErrorInfoV1* output, uint domain, int code, ulong correlation);
    [LibraryImport(Library, EntryPoint = "nw_a20_notify_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint Notify(NwOperationCallbacksV1* table);
}

/// <summary>Fixed SplitMix64 generator; its algorithm, seed and serialized cases are versioned together.</summary>
internal sealed class Sequence(ulong seed)
{
    private ulong _state = seed;
    internal ulong Next()
    {
        ulong z = _state += 0x9e3779b97f4a7c15;
        z = (z ^ (z >> 30)) * 0xbf58476d1ce4e5b9;
        z = (z ^ (z >> 27)) * 0x94d049bb133111eb;
        return z ^ (z >> 31);
    }
}

/// <summary>A failed invariant retains its full generated case through the runner's raw output.</summary>
internal static class Check
{
    internal static long Assertions;
    internal static void That(bool value, string reason)
    {
        Interlocked.Increment(ref Assertions);
        if (!value) throw new InvalidOperationException(reason);
    }
    internal static void Throws<T>(Action body, string reason) where T : Exception
    {
        try { body(); } catch (T) { That(true, reason); return; }
        throw new InvalidOperationException(reason);
    }
    internal static async Task Until(Func<bool> condition, string reason)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(1, timeout.Token).ConfigureAwait(false);
        That(true, reason);
    }
}
