using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Korda.Interop;
using Korda.Interop.Abi;
using Korda.Interop.Async;
using Korda.Interop.Extensibility;
using Korda.Interop.Lifetime;

internal sealed class OperationHandle : SafeHandle
{
    internal OperationHandle() : base(0, true) { }
    internal void Adopt(nint value) => SetHandle(value);
    public override bool IsInvalid => handle == 0;
    protected override bool ReleaseHandle() { Fixture.Release(handle); return true; }
}
internal sealed class ResultHandle : SafeHandle
{
    internal ResultHandle() : base(0, true) { }
    internal void Adopt(nint pointer) => SetHandle(pointer);
    public override bool IsInvalid => handle == 0;
    protected override bool ReleaseHandle() { Fixture.BufferRelease(handle); return true; }
}
internal sealed class SourceHandle : SafeHandle
{
    internal SourceHandle(nint value) : base(0, true) => SetHandle(value);
    public override bool IsInvalid => handle == 0;
    protected override bool ReleaseHandle() { Fixture.SourceRelease(handle); return true; }
}
internal sealed unsafe class FixtureCalls : AsyncNativeCalls
{
    internal int Poisoned;
    internal int Cancels;
    internal int Retires;
    internal Exception? MappedFailure { get; set; }
    internal bool ThrowAfterStart { get; set; }
    internal override uint Start(nint operation, in NwOperationCallbacksV1 callbacks)
    {
        uint status = Fixture.Start(operation, in callbacks);
        if (ThrowAfterStart) throw new ApplicationException("indeterminate-start");
        return status;
    }
    internal override uint RequestCancel(nint operation)
    {
        Interlocked.Increment(ref Cancels);
        uint status = Fixture.Cancel(operation);
        return status;
    }
    internal override uint TryRetire(nint operation) { Interlocked.Increment(ref Retires); return Fixture.Retire(operation); }
    internal override void Poison() => Interlocked.Increment(ref Poisoned);
    internal override Exception MapFailure(nint operation, uint status, in NwErrorInfoV1 error, CancellationToken token) =>
        MappedFailure ?? base.MapFailure(operation, status, in error, token);
}

internal static unsafe partial class Fixture
{
    private const string Library = "korda_interop_async";
    [ThreadStatic] internal static bool InNativeCall;
    // Loader metadata is an explicit component fixture, not new A02 acceptance.
    private static readonly EngineInfo Engine = new(new EnginePackageManifest("interop.async.fixture", "Korda.Core", "0.1.0",
        new AbiVersion(1, 0), "nw_async_fixture_bootstrap_v1", EngineThreadingModel.EngineLocalAsync,
        [new NativeAssetDescriptor("linux-x64", "runtimes/linux-x64/native/libkorda_interop_async.so", new string('0', 64), "x86-64")], []), "linux-x64", new AbiVersion(1, 0), 0);

    [LibraryImport(Library, EntryPoint = "nw_async_fixture_create_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial uint CreateNative(uint mode, byte* data, ulong length, uint fail, out nint pointer);
    [LibraryImport(Library, EntryPoint = "nw_async_fixture_start_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial uint StartNative(nint operation, NwOperationCallbacksV1* callbacks);
    [LibraryImport(Library, EntryPoint = "nw_async_fixture_cancel_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint Cancel(nint operation);
    [LibraryImport(Library, EntryPoint = "nw_async_fixture_complete_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint CompleteNative(nint operation, uint status);
    [LibraryImport(Library, EntryPoint = "nw_async_fixture_spawn_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial uint SpawnNative(nint operation, uint status);
    [LibraryImport(Library, EntryPoint = "nw_async_fixture_take_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial uint TakeNative(nint operation, out nint result);
    [LibraryImport(Library, EntryPoint = "nw_async_fixture_view_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial uint View(nint result, out nint address, out ulong length);
    [LibraryImport(Library, EntryPoint = "nw_async_fixture_buffer_release_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void BufferRelease(nint result);
    [LibraryImport(Library, EntryPoint = "nw_async_fixture_retire_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint Retire(nint operation);
    [LibraryImport(Library, EntryPoint = "nw_async_fixture_source_create_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial uint SourceCreate(nint operation, out nint source);
    [LibraryImport(Library, EntryPoint = "nw_async_fixture_source_release_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SourceRelease(nint source);
    [LibraryImport(Library, EntryPoint = "nw_async_fixture_duplicate_notice_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint Duplicate(nint operation);
    [LibraryImport(Library, EntryPoint = "nw_async_fixture_release_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void Release(nint operation);
    [LibraryImport(Library, EntryPoint = "nw_async_fixture_count_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ulong Count(uint kind);

    internal static uint Start(nint operation, in NwOperationCallbacksV1 callbacks)
    {
        bool previous = InNativeCall;
        InNativeCall = true;
        try { fixed (NwOperationCallbacksV1* value = &callbacks) return StartNative(operation, value); }
        finally { InNativeCall = previous; }
    }
    internal static uint Create(uint mode, byte[] bytes, uint fail, out nint pointer)
    {
        fixed (byte* data = bytes) return CreateNative(mode, data, (ulong)bytes.Length, fail, out pointer);
    }
    internal static NativeResourceOwner New(uint mode = 0, byte[]? bytes = null)
    {
        var handle = NewHandle(mode, bytes);
        try { return new(handle); }
        catch { handle.Dispose(); throw; }
    }
    internal static OperationHandle NewHandle(uint mode = 0, byte[]? bytes = null)
    {
        var handle = new OperationHandle();
        try
        {
            if (Create(mode, bytes ?? [1, 2, 3], 0, out nint pointer) != NativeStatus.Ok)
                throw new InvalidOperationException("Fixture create failed.");
            handle.Adopt(pointer);
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }
    internal static uint Complete(AsyncOperation operation, uint status = NativeStatus.Ok) =>
        Control(operation, pointer => CompleteNative(pointer, status));
    internal static uint Spawn(AsyncOperation operation, uint status = NativeStatus.Ok) =>
        Control(operation, pointer => SpawnNative(pointer, status));
    private static uint Control(AsyncOperation operation, Func<nint, uint> call)
    {
        bool lease = false;
        try
        {
            nint pointer = operation.AcquireControl(ref lease);
            InNativeCall = true;
            return call(pointer);
        }
        finally { InNativeCall = false; operation.ReleaseControl(ref lease); }
    }
    internal static SourceHandle HoldSource(AsyncOperation operation)
    {
        bool lease = false;
        try
        {
            nint pointer = operation.AcquireControl(ref lease);
            if (SourceCreate(pointer, out nint source) != NativeStatus.Ok) throw new Exception("Source create failed.");
            try { return new(source); } catch { SourceRelease(source); throw; }
        }
        finally { operation.ReleaseControl(ref lease); }
    }

    internal static NativeBuffer TakeBuffer(nint operation) => TakeBuffer(operation, throwAfterTake: false);
    internal static NativeBuffer TakeBuffer(nint operation, bool throwAfterTake)
    {
        // The owning managed SafeHandle exists before native ownership moves.
        // Every post-take conversion/adoption failure releases that exact owner.
        var handle = new ResultHandle();
        NativeBuffer? buffer = null;
        try
        {
            uint status = TakeNative(operation, out nint result);
            if (status != NativeStatus.Ok)
            {
                if (result != 0) throw new Exception("Failed take leaked a published owner.");
                throw new NativeEngineException("async.fixture", "take", status, 0, 0, 0, "Typed take failed before ownership transfer.");
            }
            handle.Adopt(result);
            if (TakeNative(operation, out nint duplicate) != NativeStatus.InvalidState || duplicate != 0)
                throw new Exception("One-time typed take published a second owner.");
            if (View(result, out nint address, out ulong length) != NativeStatus.Ok) throw new Exception("Result view failed.");
            buffer = NativeBufferFactory.Adopt(Engine, handle, address, checked((int)length));
            if (throwAfterTake) throw new ApplicationException("materialization-failure-after-take");
            return buffer;
        }
        catch { if (buffer is null) handle.Dispose(); else buffer.Dispose(); throw; }
    }
}
