using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NativeWeave.Interop.Abi;
using NativeWeave.Interop.Async;
using NativeWeave.Interop.Callbacks;
using NativeWeave.Interop.Lifetime;
using NativeWeave.Server;

internal static unsafe partial class Fixture
{
    private const string Library = "nativeweave_server_streaming";
    [LibraryImport(Library, EntryPoint = "nw_streaming_fixture_session_create")]
    internal static partial uint CreateSession(ulong native, ulong managed, ulong perRequest, out nint output);
    [LibraryImport(Library, EntryPoint = "nw_streaming_fixture_session_child")]
    internal static partial uint Child(nint parent, ulong perRequest, out nint output);
    [LibraryImport(Library, EntryPoint = "nw_streaming_fixture_session_close")]
    internal static partial uint CloseSession(nint session);
    [LibraryImport(Library, EntryPoint = "nw_streaming_fixture_session_release")]
    internal static partial void ReleaseSession(nint session);
    [LibraryImport(Library, EntryPoint = "nw_streaming_fixture_snapshot")]
    private static partial uint SnapshotNative(nint session, ulong* output);
    [LibraryImport(Library, EntryPoint = "nw_streaming_fixture_read_create")]
    private static partial uint ReadCreateNative(nint session, byte* bytes, uint length, uint manual, out nint output);
    [LibraryImport(Library, EntryPoint = "nw_streaming_fixture_start")]
    private static partial uint StartNative(nint operation, NwOperationCallbacksV1* callbacks);
    [LibraryImport(Library, EntryPoint = "nw_streaming_fixture_cancel")]
    internal static partial uint Cancel(nint operation);
    [LibraryImport(Library, EntryPoint = "nw_streaming_fixture_take")]
    internal static partial uint Take(nint operation, out nint result);
    [LibraryImport(Library, EntryPoint = "nw_streaming_fixture_retire")]
    internal static partial uint Retire(nint operation);
    [LibraryImport(Library, EntryPoint = "nw_streaming_fixture_release")]
    internal static partial void Release(nint operation);
    [LibraryImport(Library, EntryPoint = "nw_streaming_fixture_buffer_length")]
    internal static partial uint BufferLength(nint buffer, out ulong length);
    [LibraryImport(Library, EntryPoint = "nw_streaming_fixture_buffer_copy")]
    private static partial uint CopyNative(nint buffer, byte* destination, ulong capacity);
    [LibraryImport(Library, EntryPoint = "nw_streaming_fixture_buffer_credit")]
    internal static partial uint BufferCredit(nint buffer, uint capacity, out nint credit);
    [LibraryImport(Library, EntryPoint = "nw_streaming_fixture_managed_credit")]
    internal static partial uint ManagedCredit(nint session, uint capacity, out nint credit);
    [LibraryImport(Library, EntryPoint = "nw_streaming_fixture_credit_release")]
    internal static partial void ReleaseCredit(nint credit);
    [LibraryImport(Library, EntryPoint = "nw_streaming_fixture_buffer_release")]
    internal static partial void ReleaseBuffer(nint buffer);
    [LibraryImport(Library, EntryPoint = "nw_streaming_fixture_live")]
    internal static partial ulong Live(uint kind);

    internal static ulong[] Snapshot(nint session)
    {
        ulong[] result = new ulong[4];
        fixed (ulong* value = result) Check(SnapshotNative(session, value));
        return result;
    }
    internal static void Check(uint status)
    {
        if (status != NativeStatus.Ok) throw new InvalidOperationException(status == NativeStatus.DomainError ? "resource_limit" : "native_status_" + status);
    }
    internal static uint CreateRead(nint session, ReadOnlySpan<byte> bytes, bool manual, out nint output)
    {
        fixed (byte* value = bytes) return ReadCreateNative(session, value, (uint)bytes.Length, manual ? 1U : 0U, out output);
    }
    internal static uint Start(nint operation, in NwOperationCallbacksV1 callbacks)
    {
        fixed (NwOperationCallbacksV1* value = &callbacks) return StartNative(operation, value);
    }
    internal static void Copy(nint buffer, Span<byte> destination)
    {
        fixed (byte* value = destination) Check(CopyNative(buffer, value, (ulong)destination.Length));
    }
}

internal sealed class FixtureHandle : SafeHandle
{
    private readonly uint _kind;
    internal FixtureHandle(uint kind) : base(0, true) => _kind = kind;
    internal void Adopt(nint value) => SetHandle(value);
    public override bool IsInvalid => handle == 0;
    protected override bool ReleaseHandle()
    {
        switch (_kind)
        {
            case 0: Fixture.ReleaseSession(handle); break;
            case 1: Fixture.Release(handle); break;
            case 2: Fixture.ReleaseBuffer(handle); break;
            case 3: Fixture.ReleaseCredit(handle); break;
            default: return false;
        }
        return true;
    }
}

internal sealed class FixtureChunkOwner : BodyChunkOwner
{
    internal Action? CopyEntered;
    internal bool FailCopy;
    internal FixtureChunkOwner(FixtureHandle handle, int length) : base(handle, length) { }
    protected override void CopyLeased(nint pointer, Span<byte> destination)
    {
        CopyEntered?.Invoke();
        if (FailCopy) throw new IOException("Injected copy failure.");
        Fixture.Copy(pointer, destination);
    }
    protected override IDisposable AcquireManagedCreditLeased(nint pointer, int capacity)
    {
        var credit = new FixtureHandle(3);
        try { Fixture.Check(Fixture.BufferCredit(pointer, (uint)capacity, out nint value)); credit.Adopt(value); return credit; }
        catch { credit.Dispose(); throw; }
    }
}

internal sealed unsafe class FixtureCalls : AsyncNativeCalls
{
    internal int PoisonCount;
    internal override uint Start(nint operation, in NwOperationCallbacksV1 callbacks) => Fixture.Start(operation, in callbacks);
    internal override uint RequestCancel(nint operation) => Fixture.Cancel(operation);
    internal override uint TryRetire(nint operation) => Fixture.Retire(operation);
    internal override void Poison() => Interlocked.Increment(ref PoisonCount);
}

/// <summary>Test-only transport over real native allocation and Core operation lifetimes; no HTTP is emulated.</summary>
internal sealed class FixtureTransport : ServerBodyTransport, IAsyncDisposable
{
    private readonly FixtureHandle _session;
    private readonly CallbackCoordinator _coordinator = new(256);
    private readonly FixtureCalls _calls = new();
    private readonly List<AsyncOperation<BodyChunk?>> _operations = [];
    private readonly byte[] _input;
    private int _offset;
    private bool _closed;
    private bool _readClosed;
    private bool _writeClosed;
    internal bool ManualRead;
    internal bool FailMapping;
    internal FixtureChunkOwner? LastOwner;
    internal TaskCompletionSource? WriteBlock;
    internal int Writes;
    internal int Flushes;
    internal int Finishes;
    internal FixtureTransport(byte[]? input = null, ulong native = 4_194_304, ulong managed = 4_194_304, ulong perRequest = 4_194_304)
    {
        _input = input ?? [];
        _session = new(0);
        try { Fixture.Check(Fixture.CreateSession(native, managed, perRequest, out nint value)); _session.Adopt(value); }
        catch { _session.Dispose(); throw; }
    }
    internal override int MaxChunkBytes => 32_768;
    internal nint Session => _session.DangerousGetHandle();
    internal ulong[] Snapshot => Fixture.Snapshot(Session);
    internal override void ThrowIfUnavailable() => ObjectDisposedException.ThrowIf(_closed, this);
    internal override Task<BodyChunk?> ReadChunkAsync(int maximumBytes, CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        ObjectDisposedException.ThrowIf(_readClosed, this);
        if (maximumBytes <= 0 || maximumBytes > MaxChunkBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        int count = Math.Min(maximumBytes, _input.Length - _offset);
        ReadOnlySpan<byte> source = _input.AsSpan(_offset, count);
        var handle = new FixtureHandle(1);
        NativeResourceOwner? owner = null;
        try
        {
            Fixture.Check(Fixture.CreateRead(Session, source, ManualRead, out nint pointer));
            handle.Adopt(pointer);
            owner = new(handle);
            var operation = new AsyncOperation<BodyChunk?>(owner, _calls, _coordinator, Take, cancellationToken);
            _operations.Add(operation);
            _offset += count;
            return operation.Start();
        }
        catch { if (owner is null) handle.Dispose(); else owner.Dispose(); throw; }
    }
    private BodyChunk? Take(nint operation)
    {
        var handle = new FixtureHandle(2);
        try
        {
            Fixture.Check(Fixture.Take(operation, out nint pointer));
            if (pointer == 0) { handle.Dispose(); return null; }
            handle.Adopt(pointer);
            Fixture.Check(Fixture.BufferLength(pointer, out ulong length));
            if (FailMapping) throw new IOException("Injected DTO/chunk wrapper mapping failure.");
            var owner = new FixtureChunkOwner(handle, checked((int)length));
            var chunk = new BodyChunk(owner);
            LastOwner = owner;
            return chunk;
        }
        catch { handle.Dispose(); throw; }
    }
    internal override Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        ThrowIfUnavailable(); ObjectDisposedException.ThrowIf(_writeClosed, this);
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled(cancellationToken);
        Interlocked.Increment(ref Writes);
        return WriteBlock?.Task.WaitAsync(cancellationToken) ?? Task.CompletedTask;
    }
    internal override Task FlushAsync(CancellationToken cancellationToken)
    {
        ThrowIfUnavailable(); ObjectDisposedException.ThrowIf(_writeClosed, this);
        cancellationToken.ThrowIfCancellationRequested(); Flushes++; return Task.CompletedTask;
    }
    internal override Task FinishAsync(CancellationToken cancellationToken)
    {
        ThrowIfUnavailable(); cancellationToken.ThrowIfCancellationRequested(); Finishes++; return Task.CompletedTask;
    }
    internal override IDisposable AcquireManagedCredit(int capacity)
    {
        ThrowIfUnavailable();
        var handle = new FixtureHandle(3);
        try { Fixture.Check(Fixture.ManagedCredit(Session, checked((uint)capacity), out nint pointer)); handle.Adopt(pointer); return handle; }
        catch { handle.Dispose(); throw; }
    }
    internal override void CloseRead() => _readClosed = true;
    internal override void CloseWrite() => _writeClosed = true;
    internal async Task RetireAsync()
    {
        foreach (AsyncOperation<BodyChunk?> operation in _operations) await operation.Retirement.WaitAsync(TimeSpan.FromSeconds(10));
        if (_calls.PoisonCount != 0) throw new Exception("Unexpected native poison.");
    }
    internal void CloseDispatch() { _closed = true; Fixture.Check(Fixture.CloseSession(Session)); }
    public async ValueTask DisposeAsync()
    {
        if (!_closed) CloseDispatch();
        foreach (AsyncOperation<BodyChunk?> operation in _operations) operation.Dispose();
        await RetireAsync();
        _coordinator.Dispose();
        _session.Dispose();
    }
}
