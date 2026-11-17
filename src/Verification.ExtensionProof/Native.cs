using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NativeWeave.Interop.Abi;

namespace NativeWeave.Verification.ExtensionProof;

[StructLayout(LayoutKind.Sequential)]
internal struct AffinitySnapshot
{
    internal ulong Queued, Running, Completed, ClosedItems, Touches, ReleaseRequested, Retired, OwnerExited, NativeCallbacks, NativeSources;
}

/// <summary>Private fixed Linux conformance imports; no GUI/GPU/platform object crosses this ABI.</summary>
internal static unsafe partial class Native
{
    private const string Library = "nativeweave_engine_extensionproof";
    [LibraryImport(Library, EntryPoint = "nw_extension_create_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint Create(uint capacity, out nint handle, out nint observer);
    [LibraryImport(Library, EntryPoint = "nw_extension_start_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial uint StartNative(nint handle, NwCallbacksV1* callbacks);
    internal static uint Start(nint handle, in NwCallbacksV1 callbacks)
    {
        fixed (NwCallbacksV1* table = &callbacks) return StartNative(handle, table);
    }
    [LibraryImport(Library, EntryPoint = "nw_extension_work_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint NewWork(ulong value, out nint work);
    [LibraryImport(Library, EntryPoint = "nw_extension_post_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint Post(nint handle, nint work);
    [LibraryImport(Library, EntryPoint = "nw_extension_read_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint Read(nint observer, out ulong value);
    [LibraryImport(Library, EntryPoint = "nw_extension_pump_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint Pump(nint observer, uint budget);
    [LibraryImport(Library, EntryPoint = "nw_extension_stop_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint Stop(nint handle);
    [LibraryImport(Library, EntryPoint = "nw_extension_retire_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint Retire(nint handle);
    [LibraryImport(Library, EntryPoint = "nw_extension_snapshot_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint Snapshot(nint observer, out AffinitySnapshot snapshot);
    [LibraryImport(Library, EntryPoint = "nw_extension_release_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void Release(nint handle);
    [LibraryImport(Library, EntryPoint = "nw_extension_observer_release_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void ReleaseObserver(nint observer);
    [LibraryImport(Library, EntryPoint = "nw_extension_work_release_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void ReleaseWork(nint work);
    [LibraryImport(Library, EntryPoint = "nw_extension_count_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ulong Count(uint kind);
}
