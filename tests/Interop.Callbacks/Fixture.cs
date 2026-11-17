using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NativeWeave.Interop.Abi;
using NativeWeave.Interop.Callbacks;
using NativeWeave.Interop.Lifetime;

internal sealed class FixtureHandle : SafeHandle
{
    internal FixtureHandle(nint value) : base(0, ownsHandle: true) => SetHandle(value);
    public override bool IsInvalid => handle == 0;
    protected override bool ReleaseHandle() { Fixture.Release(handle); return true; }
}

internal sealed unsafe class FixtureCalls : CallbackNativeCalls
{
    internal Action? AfterStart { get; init; }
    internal int Stops;
    internal int Retires;
    internal override uint Start(nint subscription, in NwCallbacksV1 callbacks)
    {
        uint status = Fixture.Start(subscription, in callbacks);
        AfterStart?.Invoke();
        return status;
    }
    internal override uint RequestStop(nint subscription)
    {
        Interlocked.Increment(ref Stops);
        return Fixture.Stop(subscription);
    }
    internal override uint TryRetire(nint subscription)
    {
        Interlocked.Increment(ref Retires);
        return Fixture.Retire(subscription);
    }
}

internal static unsafe partial class Fixture
{
    private const string Library = "nativeweave_interop_callbacks";
    [ThreadStatic] internal static bool InNativeCall;

    [LibraryImport(Library, EntryPoint = "nw_callbacks_fixture_create_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint Create(uint mode, out nint output);
    [LibraryImport(Library, EntryPoint = "nw_callbacks_fixture_start_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial uint StartNative(nint pointer, NwCallbacksV1* callbacks);
    [LibraryImport(Library, EntryPoint = "nw_callbacks_fixture_emit_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial uint EmitNative(nint pointer, uint kind, byte* payload, ulong bytes);
    [LibraryImport(Library, EntryPoint = "nw_callbacks_fixture_stop_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint Stop(nint pointer);
    [LibraryImport(Library, EntryPoint = "nw_callbacks_fixture_retire_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint Retire(nint pointer);
    [LibraryImport(Library, EntryPoint = "nw_callbacks_fixture_source_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint Source(nint pointer, uint hold);
    [LibraryImport(Library, EntryPoint = "nw_callbacks_fixture_gated_emit_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint GatedEmit(nint pointer);
    [LibraryImport(Library, EntryPoint = "nw_callbacks_fixture_gate_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint Gate(nint pointer, uint release);
    [LibraryImport(Library, EntryPoint = "nw_callbacks_fixture_release_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void Release(nint pointer);
    [LibraryImport(Library, EntryPoint = "nw_callbacks_fixture_count_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ulong Count(uint kind);

    internal static NativeResourceOwner New(uint mode = 0)
    {
        if (Create(mode, out nint pointer) != 0) throw new InvalidOperationException("Fixture create failed.");
        FixtureHandle? handle = null;
        try { handle = new(pointer); return new(handle); }
        catch { if (handle is null) Release(pointer); else handle.Dispose(); throw; }
    }

    internal static uint Start(nint pointer, in NwCallbacksV1 callbacks)
    {
        fixed (NwCallbacksV1* table = &callbacks) return StartNative(pointer, table);
    }

    internal static uint Emit(CallbackRegistration registration, byte[] bytes, uint kind = 1)
    {
        bool lease = false;
        try
        {
            nint pointer = registration.AcquireControl(ref lease);
            InNativeCall = true;
            fixed (byte* data = bytes) return EmitNative(pointer, kind, data, (ulong)bytes.Length);
        }
        catch (ObjectDisposedException) { return NativeStatus.InvalidState; }
        finally { InNativeCall = false; registration.ReleaseControl(ref lease); }
    }
}
