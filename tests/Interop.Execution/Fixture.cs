using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NativeWeave.Interop;
using NativeWeave.Interop.Abi;
using NativeWeave.Interop.Async;
using NativeWeave.Interop.Callbacks;
using NativeWeave.Interop.Execution;
using NativeWeave.Interop.Lifetime;

internal sealed class EngineHandle : SafeHandle
{
    internal EngineHandle() : base(0, true) { }
    internal void Adopt(nint value) => SetHandle(value);
    public override bool IsInvalid => handle == 0;
    protected override bool ReleaseHandle() { Fixture.EngineRelease(handle); return true; }
}
internal sealed class JobHandle : SafeHandle
{
    internal JobHandle() : base(0, true) { }
    internal void Adopt(nint value) => SetHandle(value);
    public override bool IsInvalid => handle == 0;
    protected override bool ReleaseHandle() { Fixture.JobRelease(handle); return true; }
}
internal sealed class CloseHandle : SafeHandle
{
    internal CloseHandle() : base(0, true) { }
    internal void Adopt(nint value) => SetHandle(value);
    public override bool IsInvalid => handle == 0;
    protected override bool ReleaseHandle() { Fixture.CloseRelease(handle); return true; }
}
internal sealed class SourceHandle : SafeHandle
{
    internal SourceHandle() : base(0, true) { }
    internal void Adopt(nint value) => SetHandle(value);
    public override bool IsInvalid => handle == 0;
    protected override bool ReleaseHandle() { Fixture.SourceRelease(handle); return true; }
}
internal sealed class EngineLease : IDisposable
{
    private readonly EngineHandle _handle;
    private bool _acquired;
    internal nint Pointer { get; }
    internal EngineLease(EngineHandle handle)
    {
        _handle = handle;
        handle.DangerousAddRef(ref _acquired);
        Pointer = handle.DangerousGetHandle();
    }
    public void Dispose()
    {
        if (!_acquired) return;
        _acquired = false;
        _handle.DangerousRelease();
    }
}
internal sealed unsafe class FixtureCalls(EngineLease engine, bool close) : AsyncNativeCalls
{
    internal int Starts;
    internal int Cancels;
    internal override uint Start(nint operation, in NwOperationCallbacksV1 callbacks)
    {
        Interlocked.Increment(ref Starts);
        fixed (NwOperationCallbacksV1* pointer = &callbacks)
            return close ? Fixture.CloseStart(operation, pointer) : Fixture.JobStart(operation, pointer);
    }
    internal override uint RequestCancel(nint operation)
    {
        Interlocked.Increment(ref Cancels);
        return close ? Fixture.CloseCancel(operation) : Fixture.JobCancel(operation);
    }
    internal override uint TryRetire(nint operation) => close ? Fixture.CloseRetire(operation) : Fixture.JobRetire(operation);
    internal override void Poison() => Fixture.RequestClose(engine.Pointer);
}

internal sealed class FixtureEngine : IDisposable, IAsyncDisposable
{
    [ThreadStatic] private static FixtureEngine? s_ownedCallback;
    private readonly EngineHandle _handle;
    private readonly CallbackCoordinator _coordinator;
    internal ExecutorLifetime Lifetime { get; }
    internal FixtureCalls CloseCalls { get; }
    internal Action? BeforeTake { get; set; }
    internal int CleanupSlots => _coordinator.Count;

    internal FixtureEngine(uint workers = 1, uint admitted = 2, uint waiters = 2, ulong bytes = 8, uint cleanup = 2)
    {
        _handle = new();
        CallbackCoordinator? coordinator = null;
        AsyncOperation<ExecutorJoinResult>? close = null;
        try
        {
            uint status = Fixture.Create(workers, admitted, waiters, bytes, cleanup, out nint pointer);
            if (status != NativeStatus.Ok) throw Fixture.Error("create", status);
            _handle.Adopt(pointer);
            coordinator = new(checked((int)admitted + 1));
            _coordinator = coordinator;
            var handle = new CloseHandle();
            EngineLease? lease = null;
            NativeResourceOwner? owner = null;
            try
            {
                lease = new(_handle);
                status = Fixture.CloseCreate(lease.Pointer, out pointer);
                if (status != NativeStatus.Ok) throw Fixture.Error("close-create", status);
                handle.Adopt(pointer);
                owner = new(handle);
                CloseCalls = new(lease, close: true);
                close = new(owner, CloseCalls, coordinator, Fixture.TakeClose, inputLifetime: lease);
            }
            catch { owner?.Dispose(); handle.Dispose(); lease?.Dispose(); throw; }
            Lifetime = new(close, () => ReferenceEquals(s_ownedCallback, this));
        }
        catch { close?.Dispose(); coordinator?.Dispose(); _handle.Dispose(); throw; }
    }

    internal AsyncOperation<ulong> NewJob(uint mode = 0, byte[]? bytes = null, CancellationToken token = default)
    {
        if (!TryNewJob(mode, bytes ?? [1, 2, 3], token, out var operation, out uint status)) throw Fixture.Error("job-create", status);
        return operation!;
    }
    internal bool TryNewJob(uint mode, byte[] bytes, CancellationToken token, out AsyncOperation<ulong>? operation, out uint status)
    {
        operation = null;
        var handle = new JobHandle();
        EngineLease? lease = null;
        NativeResourceOwner? owner = null;
        try
        {
            lease = new(_handle);
            status = Fixture.CreateJob(lease.Pointer, mode, bytes, out nint pointer);
            if (status != NativeStatus.Ok)
            {
                if (pointer != 0) throw new InvalidOperationException("Rejected admission published a native owner.");
                handle.Dispose(); lease.Dispose(); return false;
            }
            handle.Adopt(pointer);
            owner = new(handle);
            operation = new(owner, new FixtureCalls(lease, close: false), _coordinator, Take,
                cancellationToken: token, inputLifetime: lease);
            return true;
        }
        catch { owner?.Dispose(); handle.Dispose(); lease?.Dispose(); throw; }
    }
    private ulong Take(nint pointer)
    {
        FixtureEngine? previous = s_ownedCallback;
        s_ownedCallback = this;
        try { BeforeTake?.Invoke(); return Fixture.TakeJob(pointer); }
        finally { s_ownedCallback = previous; }
    }
    internal ulong Read(uint kind)
    {
        using var lease = new EngineLease(_handle);
        return Fixture.Snapshot(lease.Pointer, kind);
    }
    internal void OpenGate()
    {
        using var lease = new EngineLease(_handle);
        Fixture.OpenGate(lease.Pointer);
    }
    internal SourceHandle StartWithHeldSource(AsyncOperation<ulong> operation)
    {
        var source = new SourceHandle();
        bool lease = false;
        try
        {
            nint pointer = operation.AcquireControl(ref lease);
            operation.Start();
            uint status = Fixture.SourceTake(pointer, out nint held);
            if (status != NativeStatus.Ok) throw Fixture.Error("source-take", status);
            source.Adopt(held);
            return source;
        }
        catch { source.Dispose(); throw; }
        finally { operation.ReleaseControl(ref lease); }
    }
    public void Dispose()
    {
        Lifetime.Dispose();
        _coordinator.Dispose();
        _handle.Dispose();
    }
    public async ValueTask DisposeAsync()
    {
        await Lifetime.DisposeAsync();
        Dispose();
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeJoin
{
    internal ulong Joined;
    internal ulong Started;
    internal ulong Stopped;
    internal ulong Outstanding;
    internal ulong Bytes;
}
internal static unsafe partial class Fixture
{
    private const string Library = "nativeweave_interop_execution";
    [LibraryImport(Library, EntryPoint = "nw_execution_fixture_create_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint Create(uint workers, uint admitted, uint waiters, ulong bytes, uint cleanup, out nint engine);
    [LibraryImport(Library, EntryPoint = "nw_execution_fixture_request_close_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void RequestClose(nint engine);
    [LibraryImport(Library, EntryPoint = "nw_execution_fixture_engine_release_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void EngineRelease(nint engine);
    [LibraryImport(Library, EntryPoint = "nw_execution_fixture_job_create_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial uint JobCreate(nint engine, uint mode, byte* data, ulong length, out nint operation);
    [LibraryImport(Library, EntryPoint = "nw_execution_fixture_job_start_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint JobStart(nint operation, NwOperationCallbacksV1* callbacks);
    [LibraryImport(Library, EntryPoint = "nw_execution_fixture_job_cancel_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint JobCancel(nint operation);
    [LibraryImport(Library, EntryPoint = "nw_execution_fixture_job_take_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial uint JobTake(nint operation, out ulong result);
    [LibraryImport(Library, EntryPoint = "nw_execution_fixture_job_retire_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint JobRetire(nint operation);
    [LibraryImport(Library, EntryPoint = "nw_execution_fixture_job_release_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void JobRelease(nint operation);
    [LibraryImport(Library, EntryPoint = "nw_execution_fixture_source_take_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint SourceTake(nint operation, out nint source);
    [LibraryImport(Library, EntryPoint = "nw_execution_fixture_source_release_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SourceRelease(nint source);
    [LibraryImport(Library, EntryPoint = "nw_execution_fixture_open_gate_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void OpenGate(nint engine);
    [LibraryImport(Library, EntryPoint = "nw_execution_fixture_snapshot_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ulong Snapshot(nint engine, uint kind);
    [LibraryImport(Library, EntryPoint = "nw_execution_fixture_close_create_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint CloseCreate(nint engine, out nint close);
    [LibraryImport(Library, EntryPoint = "nw_execution_fixture_close_start_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint CloseStart(nint close, NwOperationCallbacksV1* callbacks);
    [LibraryImport(Library, EntryPoint = "nw_execution_fixture_close_cancel_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint CloseCancel(nint close);
    [LibraryImport(Library, EntryPoint = "nw_execution_fixture_close_take_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial uint CloseTake(nint close, out NativeJoin result);
    [LibraryImport(Library, EntryPoint = "nw_execution_fixture_close_retire_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint CloseRetire(nint close);
    [LibraryImport(Library, EntryPoint = "nw_execution_fixture_close_release_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void CloseRelease(nint close);
    [LibraryImport(Library, EntryPoint = "nw_execution_fixture_count_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ulong Count(uint kind);

    internal static uint CreateJob(nint engine, uint mode, byte[] bytes, out nint operation)
    {
        fixed (byte* pointer = bytes) return JobCreate(engine, mode, pointer, (ulong)bytes.Length, out operation);
    }
    internal static ulong TakeJob(nint operation)
    {
        uint status = JobTake(operation, out ulong result);
        if (status != NativeStatus.Ok) throw Error("take", status);
        if (JobTake(operation, out ulong duplicate) != NativeStatus.InvalidState || duplicate != 0)
            throw new InvalidOperationException("Typed result must transfer exactly once.");
        return result;
    }
    internal static ExecutorJoinResult TakeClose(nint operation)
    {
        uint status = CloseTake(operation, out NativeJoin result);
        if (status != NativeStatus.Ok) throw Error("close-take", status);
        if (CloseTake(operation, out NativeJoin duplicate) != NativeStatus.InvalidState || duplicate.Joined != 0)
            throw new InvalidOperationException("Close result must transfer exactly once.");
        return new(result.Joined == 1, result.Started, result.Stopped, result.Outstanding, result.Bytes);
    }
    internal static NativeEngineException Error(string operation, uint status) => new("execution.fixture", operation, status, 0, 0, 0, "Execution fixture rejected the requested state or capacity.");
}
