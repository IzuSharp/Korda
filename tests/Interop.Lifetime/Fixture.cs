using System.Runtime.InteropServices;
using NativeWeave.Interop.Lifetime;

internal sealed class FixtureHandle : SafeHandle
{
    internal FixtureHandle(nint pointer) : base(0, true) => SetHandle(pointer);
    public override bool IsInvalid => handle == 0;
    protected override bool ReleaseHandle() { Fixture.Release(handle); return true; }
}
internal static partial class Fixture
{
    private const string Library = "nativeweave_interop_lifetime";
    [LibraryImport(Library, EntryPoint = "nw_lifetime_fixture_create_v1")]
    internal static partial uint Create(ulong value, out nint output);
    [LibraryImport(Library, EntryPoint = "nw_lifetime_fixture_child_v1")]
    internal static partial uint Child(nint parent, uint fail, out nint output);
    [LibraryImport(Library, EntryPoint = "nw_lifetime_fixture_read_v1")]
    internal static partial uint Read(nint pointer, out ulong value, out ulong id);
    [LibraryImport(Library, EntryPoint = "nw_lifetime_fixture_release_v1")]
    internal static partial void Release(nint pointer);
    [LibraryImport(Library, EntryPoint = "nw_lifetime_fixture_counter_v1")]
    internal static partial ulong Counter(uint kind);

    internal static NativeResourceOwner New(ulong value = 42)
    {
        if (Create(value, out nint p) != 0) throw new InvalidOperationException("Native fixture creation failed.");
        FixtureHandle? handle = null;
        try { handle = new(p); return new(handle); }
        catch { if (handle is null) Release(p); else handle.Dispose(); throw; }
    }
    internal static ulong Invoke(NativeResourceOwner owner)
    {
        bool admitted = false;
        try
        {
            nint p = owner.Acquire(ref admitted);
            if (Read(p, out ulong value, out _) != 0) throw new InvalidOperationException("Native fixture read failed.");
            return value;
        }
        finally { owner.Release(ref admitted); }
    }
}
