// Maintainer adapters reused from tests/Interop.Memory/Fixture.cs; all tests execute the actual Core/native primitives.
using System.Runtime.InteropServices;
using NativeWeave.Interop;
using NativeWeave.Interop.Extensibility;

namespace NativeWeave.Verification.Interop;

internal sealed class BufferHandle : SafeHandle
{
    internal BufferHandle(nint value) : base(0, true) => SetHandle(value);
    public override bool IsInvalid => handle == 0;
    protected override bool ReleaseHandle() { MemoryNative.Release(handle); return true; }
}
internal static unsafe partial class MemoryNative
{
    private const string Library = "nativeweave_interop_memory";
    // Component metadata fixture only: A02's independently accepted real loader is
    // not reimplemented or re-accepted by these buffer tests. The C++ buffer
    // allocation, views, capacity charges and creator releases below are actual.
    internal static readonly EngineInfo Engine = new(new EnginePackageManifest("interop.memory.fixture", "NativeWeave.Core", "0.1.0",
        new AbiVersion(1, 0), "nw_memory_fixture_bootstrap_v1", EngineThreadingModel.Synchronous,
        [new NativeAssetDescriptor("linux-x64", "runtimes/linux-x64/native/libnativeweave_interop_memory.so", new string('0', 64), "x86-64")], []), "linux-x64", new AbiVersion(1, 0), 0);

    [LibraryImport(Library, EntryPoint = "nw_memory_fixture_copy_v1")]
    internal static partial uint Copy(byte* data, ulong length, ulong reserve, uint fail, out nint output);
    [LibraryImport(Library, EntryPoint = "nw_memory_fixture_slice_v1")]
    internal static partial uint Slice(nint source, ulong offset, ulong length, out nint output);
    [LibraryImport(Library, EntryPoint = "nw_memory_fixture_view_v1")]
    internal static partial uint View(nint owner, out nint address, out ulong length, out ulong capacity);
    [LibraryImport(Library, EntryPoint = "nw_memory_fixture_release_v1")]
    internal static partial void Release(nint owner);
    [LibraryImport(Library, EntryPoint = "nw_memory_fixture_counter_v1")]
    internal static partial ulong Counter(uint kind);
    [LibraryImport(Library, EntryPoint = "nw_memory_fixture_validate_v1")]
    internal static partial uint Validate(ulong address, ulong length, ulong count, ulong width, ulong offset, ulong capacity, ulong limit, uint alignment, out ulong start);

    internal static BufferHandle Create(ReadOnlySpan<byte> bytes, ulong reserve = 0)
    {
        fixed (byte* p = bytes)
        {
            uint status = Copy(p, (ulong)bytes.Length, reserve, 0, out nint owner);
            if (status != 0) throw new InvalidOperationException("Native fixture create status=" + status);
            try { return new BufferHandle(owner); }
            catch { Release(owner); throw; }
        }
    }
    internal static NativeBuffer Adopt(BufferHandle owner)
    {
        bool lease = false;
        try
        {
            owner.DangerousAddRef(ref lease);
            if (View(owner.DangerousGetHandle(), out nint address, out ulong length, out _) != 0) throw new InvalidOperationException("Native fixture view failed.");
            return NativeBufferFactory.Adopt(Engine, owner, address, checked((int)length));
        }
        finally { if (lease) owner.DangerousRelease(); }
    }
    internal static NativeBuffer New(ReadOnlySpan<byte> bytes, ulong reserve = 0)
    {
        BufferHandle handle = Create(bytes, reserve);
        try { return Adopt(handle); } catch { handle.Dispose(); throw; }
    }
}
