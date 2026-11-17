using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NativeWeave.Interop.Abi;
using NativeWeave.Interop.Async;
using NativeWeave.Interop.Callbacks;
using NativeWeave.Interop.Lifetime;
using NativeWeave.Server.Native;

namespace NativeWeave.Server;

internal static partial class NativeHostFactory
{
    static partial void CreateCore(ServerConfiguration configuration, ref ServerHost? host) => host = new NativeHost(configuration);
}

/// <summary>Private typed protocol registration; adapters compose this same instance and executor.</summary>
internal interface INativeServerProtocolDescriptor { byte[] RegistrationUtf8 { get; } }

internal sealed class NativeHost : ServerHost
{
    internal readonly ServerNativeCalls Native;
    internal readonly ServerConfiguration Configuration;
    internal readonly NativeResourceOwner Owner;
    private readonly CallbackCoordinator _coordinator;
    private readonly object _gate = new();
    private readonly Dictionary<ulong, HostTicket> _tickets;
    private readonly HashSet<AsyncOperation> _operations = [];
    private readonly TaskCompletionSource _lifetime = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly HostControl _control;
    private readonly int _controlSlot;
    private GCHandle _root;
    private Func<HostRequest, Task>? _dispatch;
    private IReadOnlyList<Uri> _addresses = Array.Empty<Uri>();
    private Task? _dispose;
    private bool _released;
    private int _callbackEntries;
    private uint _fatalStatus;
    private NwErrorInfoV1 _fatalError;

    internal unsafe NativeHost(ServerConfiguration configuration)
    {
        Configuration = configuration;
        Capabilities = NativePackage.Inspect(); Native = NativePackage.Calls.Value;
        int active = configuration.Options.Budget.MaxActiveRequests;
        _tickets = new(active); _coordinator = new(checked(active * 9 + 33));
        _control = new(this); _controlSlot = _coordinator.Reserve(_control);
        _root = GCHandle.Alloc(this, GCHandleType.Normal);
        nint pointer = 0;
        try
        {
            using var input = new NativeInputs();
            var listeners = configuration.Listeners.Select(input.Listener).ToArray();
            var o = configuration.Options; var b = o.Budget;
            var config = new NwServerConfigV1
            {
                struct_size = (uint)sizeof(NwServerConfigV1),
                version = 1,
                request_timeout_ns = NativeCodec.Nanoseconds(o.RequestTimeout),
                shutdown_timeout_ns = NativeCodec.Nanoseconds(o.ShutdownTimeout),
                header_timeout_ns = NativeCodec.Nanoseconds(o.HeaderReadTimeout),
                keepalive_ns = NativeCodec.Nanoseconds(o.KeepAliveTimeout),
                max_request_bytes = checked((ulong)o.MaxRequestBodyBytes),
                max_json_bytes = checked((uint)o.MaxJsonBytes),
                max_header_bytes = checked((uint)o.MaxHeaderBytes),
                managed_payload_bytes = checked((ulong)b.ManagedPayloadBytes),
                native_payload_bytes = checked((ulong)b.NativePayloadBytes),
                request_retained_bytes = checked((ulong)b.RetainedBytesPerRequest),
                max_chunk_bytes = checked((uint)b.MaxChunkBytes),
                max_connections = checked((uint)b.MaxConnections),
                max_h2_streams = checked((uint)b.MaxHttp2StreamsPerConnection),
                active_requests = checked((uint)b.MaxActiveRequests),
                queued_requests = checked((uint)b.MaxQueuedRequests),
                listener_count = checked((uint)listeners.Length),
                listeners = input.Array(listeners),
                trusted_proxies = input.Bytes(NativeCodec.Encode(o.TrustedProxies?.KnownProxies.Select(ip => new KeyValuePair<string, string>("", ip.ToString())) ?? [])),
                forward_limit = checked((uint)(o.TrustedProxies?.ForwardLimit ?? 1))
            };
            var dispatch = new NwServerDispatchV1
            {
                struct_size = (uint)sizeof(NwServerDispatchV1),
                version = 1,
                cookie = (nuint)GCHandle.ToIntPtr(_root),
                on_request = (nint)(delegate* unmanaged[Cdecl]<nuint, ulong, ulong, uint, uint>)&OnRequest,
                on_control = (nint)(delegate* unmanaged[Cdecl]<nuint, uint, uint, ulong, NwErrorInfoV1*, uint>)&OnControl
            };
            var error = new NwErrorInfoV1 { StructSize = 24 };
            ServerError.Check(Native.nw_server_create(&config, &dispatch, &pointer, &error), in error);
            Owner = Adopt(pointer, Native.nw_server_release); pointer = 0;
        }
        catch
        {
            if (pointer != 0) Native.nw_server_release(pointer);
            _root.Free(); _coordinator.Release(_controlSlot, _control); _coordinator.Dispose(); throw;
        }
    }

    internal override ServerCapabilities Capabilities { get; }
    internal override IReadOnlyList<Uri> BoundAddresses => _addresses;
    internal override Task Lifetime => _lifetime.Task;
    internal override unsafe Task StartAsync(IReadOnlyList<RouteDefinition> routes, Func<HostRequest, Task> dispatch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(dispatch);
        lock (_gate) { ObjectDisposedException.ThrowIf(_released || _dispose is not null, this); if (_dispatch is not null) throw new InvalidOperationException("Native startup is single use."); _dispatch = dispatch; }
        Register(routes);
        nint operation = Create(Owner, StartCreate);
        return StartCoreAsync(operation, cancellationToken);
    }
    private unsafe uint StartCreate(nint owner, nint* output, NwErrorInfoV1* error) => Native.nw_server_start_create(owner, output, error);
    private async Task StartCoreAsync(nint operation, CancellationToken token)
    {
        var started = Begin(operation, TakeVoid, token);
        try { await started.Task.ConfigureAwait(false); }
        catch { await started.Retirement.ConfigureAwait(false); await DisposeAsync().ConfigureAwait(false); throw; }
        await started.Retirement.ConfigureAwait(false);
        _addresses = ReadAddresses();
    }
    private unsafe void Register(IReadOnlyList<RouteDefinition> routes)
    {
        using var input = new NativeInputs();
        var native = routes.Select(route =>
        {
            if (route.Kind == 1 && (Capabilities & ServerCapabilities.Grpc) == 0 || route.Kind == 2 && (Capabilities & ServerCapabilities.Mcp) == 0) throw new NotSupportedException("The native asset does not support this protocol route.");
            return new NwServerRouteV1
            {
                struct_size = (uint)sizeof(NwServerRouteV1),
                version = 1,
                handler_id = checked((ulong)route.Id),
                pattern = input.Text(route.Pattern),
                methods = input.Bytes(NativeCodec.Encode(route.Methods.Select(method => new KeyValuePair<string, string>("", method)))),
                endpoint_name = input.Text(route.Name),
                kind = checked((uint)route.Kind),
                flags = route.Anonymous ? 1u : 0u,
                timeout_ns = NativeCodec.Nanoseconds(route.EffectiveTimeout),
                max_request_bytes = checked((ulong)route.EffectiveMaxRequestBytes),
                max_json_bytes = checked((uint)route.EffectiveMaxJsonBytes),
                protocol_descriptor = route.ProtocolDescriptor is INativeServerProtocolDescriptor descriptor ? input.Bytes(descriptor.RegistrationUtf8) : default
            };
        }).ToArray();
        bool lease = false;
        try { var error = new NwErrorInfoV1 { StructSize = 24 }; ServerError.Check(Native.nw_server_routes_set(Owner.Acquire(ref lease), input.Array(native), checked((uint)native.Length), &error), in error); }
        finally { Owner.Release(ref lease); }
    }
    private unsafe IReadOnlyList<Uri> ReadAddresses()
    {
        bool lease = false; nint metadata = 0;
        try
        {
            var error = new NwErrorInfoV1 { StructSize = 24 };
            ServerError.Check(Native.nw_server_bound_addresses(Owner.Acquire(ref lease), &metadata, &error), in error);
            NwBytesViewV1 entries = default; ServerError.Check(Native.nw_server_metadata_headers(metadata, &entries, &error), in error);
            return Array.AsReadOnly(NativeCodec.Decode(entries).Values.SelectMany(x => x).Select(value => new Uri(value, UriKind.Absolute)).ToArray());
        }
        finally { if (metadata != 0) Native.nw_server_metadata_release(metadata); Owner.Release(ref lease); }
    }
    internal override Task<ServerCloseResult> CloseAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); ServerValidation.Duration(timeout, true, nameof(timeout));
        lock (_gate) if (_released) return Task.FromResult(new ServerCloseResult(ServerCloseStatus.Completed, 0, 0, 0));
        return CloseCoreAsync(CreateClose(timeout), cancellationToken);
    }
    private unsafe nint CreateClose(TimeSpan timeout) => Create(Owner, (nint pointer, nint* output, NwErrorInfoV1* error) => Native.nw_server_close_create(pointer, NativeCodec.Nanoseconds(timeout), output, error));
    private async Task<ServerCloseResult> CloseCoreAsync(nint pointer, CancellationToken token)
    {
        var operation = Begin(pointer, TakeClose, token);
        ServerCloseResult result = await operation.Task.ConfigureAwait(false);
        await operation.Retirement.ConfigureAwait(false);
        lock (_gate)
        {
            if (result.Status == ServerCloseStatus.Completed && (_tickets.Count != 0 || _operations.Count > 1))
                result = result with { Status = ServerCloseStatus.Pending, PendingRequests = Math.Max(result.PendingRequests, _tickets.Count) };
        }
        if (result.Status == ServerCloseStatus.Completed) _lifetime.TrySetResult();
        return result;
    }
    private unsafe ServerCloseResult TakeClose(nint pointer)
    {
        var result = new NwServerCloseResultV1 { struct_size = (uint)sizeof(NwServerCloseResultV1), version = 1 }; var error = new NwErrorInfoV1 { StructSize = 24 };
        ServerError.Check(Native.nw_server_operation_take_close(pointer, &result, &error), in error);
        return new((ServerCloseStatus)result.close_status, checked((int)result.pending_requests), checked((long)result.retained_managed_bytes), checked((long)result.retained_native_bytes));
    }
    internal override Task ReloadTlsAsync(string listenerName, TlsSnapshot options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Begin(CreateReload(listenerName, options), TakeVoid, cancellationToken).Task;
    }
    private unsafe nint CreateReload(string name, TlsSnapshot options)
    {
        var original = Configuration.Listeners.SingleOrDefault(x => x.Name == name) ?? throw new ArgumentException("Unknown listener.", nameof(name));
        using var inputs = new NativeInputs(); NwBytesViewV1 listenerName = inputs.Text(name);
        NwServerListenerV1 snapshot = inputs.Listener(original with { Tls = options });
        bool lease = false; nint operation = 0;
        try { var error = new NwErrorInfoV1 { StructSize = 24 }; ServerError.Check(Native.nw_server_tls_reload_create(Owner.Acquire(ref lease), listenerName, &snapshot, &operation, &error), in error); return operation; }
        finally { Owner.Release(ref lease); }
    }
    internal unsafe NwServerSnapshotV1 Snapshot()
    {
        bool lease = false;
        try { var snapshot = new NwServerSnapshotV1 { struct_size = (uint)sizeof(NwServerSnapshotV1), version = 1 }; var error = new NwErrorInfoV1 { StructSize = 24 }; ServerError.Check(Native.nw_server_get_snapshot(Owner.Acquire(ref lease), &snapshot, &error), in error); return snapshot; }
        finally { Owner.Release(ref lease); }
    }
    internal unsafe delegate uint CreateCall(nint owner, nint* operation, NwErrorInfoV1* error);
    internal unsafe nint Create(NativeResourceOwner owner, CreateCall call)
    {
        bool lease = false; nint operation = 0;
        try { var error = new NwErrorInfoV1 { StructSize = 24 }; ServerError.Check(call(owner.Acquire(ref lease), &operation, &error), in error); if (operation == 0) throw new InvalidOperationException("Native create transferred no operation."); return operation; }
        finally { owner.Release(ref lease); }
    }
    internal AsyncOperation<T> Begin<T>(nint pointer, Func<nint, T> take, CancellationToken token, NativeRequest? request = null)
    {
        NativeResourceOwner owner = OperationOwner(pointer); AsyncOperation<T>? operation = null;
        try
        {
            operation = new AsyncOperation<T>(owner, new OperationCalls(this), _coordinator, take, token);
            lock (_gate) _operations.Add(operation);
            _ = ObserveRetirementAsync(operation, request);
            request?.Track(operation);
            operation.Start(); return operation;
        }
        catch { if (operation is null) owner.Dispose(); else operation.Dispose(); throw; }
    }
    private unsafe NativeResourceOwner OperationOwner(nint pointer) => Adopt(pointer, Native.nw_server_operation_release);
    internal static unsafe NativeResourceOwner Adopt(nint pointer, delegate* unmanaged[Cdecl]<nint, void> release)
    {
        if (pointer == 0) throw new InvalidOperationException("Successful native ownership transfer was null.");
        var handle = new ServerHandle(pointer, release);
        try { return new(handle); } catch { handle.Dispose(); throw; }
    }
    private async Task ObserveRetirementAsync(AsyncOperation operation, NativeRequest? request)
    {
        await operation.Retirement.ConfigureAwait(false);
        request?.Untrack(operation); lock (_gate) _operations.Remove(operation);
    }
    internal unsafe int TakeVoid(nint operation)
    { var error = new NwErrorInfoV1 { StructSize = 24 }; ServerError.Check(Native.nw_server_operation_take_void(operation, &error), in error); return 0; }

    internal override void ReleaseUnstarted()
    {
        lock (_gate) { if (_released) return; if (_dispatch is not null) throw new InvalidOperationException("Use asynchronous retirement after startup."); _released = true; }
        Owner.Dispose(); if (_root.IsAllocated) _root.Free(); _control.Stop(); _coordinator.Release(_controlSlot, _control); _coordinator.Dispose(); _lifetime.TrySetResult();
    }
    public override ValueTask DisposeAsync()
    { lock (_gate) return new(_dispose ??= DisposeCoreAsync()); }
    private async Task DisposeCoreAsync()
    {
        await Task.Yield();
        lock (_gate) if (_released) return;
        await CloseAsync(Configuration.Options.ShutdownTimeout, CancellationToken.None).ConfigureAwait(false);
        while (true)
        {
            bool managed; lock (_gate) managed = _operations.Count == 0 && _tickets.Count == 0;
            if (managed && Volatile.Read(ref _callbackEntries) == 0 && TryRetire() == 0) break;
            await Task.Delay(5).ConfigureAwait(false);
        }
        await Owner.DisposeAsync().ConfigureAwait(false);
        lock (_gate) _released = true;
        _control.Stop(); _coordinator.Release(_controlSlot, _control); _coordinator.Dispose();
        if (_root.IsAllocated) _root.Free(); _lifetime.TrySetResult();
    }
    private unsafe uint TryRetire()
    { bool lease = false; try { return Native.nw_server_try_retire(Owner.Acquire(ref lease)); } finally { Owner.Release(ref lease); } }
    internal void Fatal(Exception failure)
    {
        _lifetime.TrySetException(failure);
        foreach (var ticket in TicketSnapshot()) ticket.Fail();
    }
    private HostTicket[] TicketSnapshot() { lock (_gate) return _tickets.Values.ToArray(); }
    private uint Notify(ulong id, ulong handler, uint kind)
    {
        HostTicket ticket;
        lock (_gate)
        {
            if (_released || _tickets.Count >= Configuration.Options.Budget.MaxActiveRequests || _tickets.ContainsKey(id) || kind != 0) return 8;
            ticket = new(this, id, handler, kind); _tickets.Add(id, ticket);
        }
        ticket.Signal(); return 0;
    }
    private void Cancel(ulong id, uint status)
    { HostTicket? ticket; lock (_gate) _tickets.TryGetValue(id, out ticket); ticket?.Cancel(status); }
    private void Remove(HostTicket ticket)
    { lock (_gate) _tickets.Remove(ticket.Id); _coordinator.Release(ticket.Slot, ticket); }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint OnRequest(nuint cookie, ulong id, ulong handler, uint kind)
    {
        NativeHost? host = null;
        try { host = (NativeHost?)GCHandle.FromIntPtr((nint)cookie).Target; if (host is null) return 8; Interlocked.Increment(ref host._callbackEntries); return host.Notify(id, handler, kind); }
        catch { return 8; }
        finally { if (host is not null) Interlocked.Decrement(ref host._callbackEntries); }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe uint OnControl(nuint cookie, uint kind, uint status, ulong id, NwErrorInfoV1* error)
    {
        NativeHost? host = null;
        try
        {
            host = (NativeHost?)GCHandle.FromIntPtr((nint)cookie).Target; if (host is null) return 8; Interlocked.Increment(ref host._callbackEntries);
            if (kind == 1 && status is 2 or 12) host.Cancel(id, status);
            else if (kind == 2 && status > 1)
            {
                lock (host._gate) if (host._fatalStatus == 0) { host._fatalError = error != null && error->StructSize >= 24 ? *error : default; host._fatalStatus = status; }
                host._control.Signal();
            }
            else return 8;
            return 0;
        }
        catch { return 8; }
        finally { if (host is not null) Interlocked.Decrement(ref host._callbackEntries); }
    }

    private sealed class HostControl(NativeHost host) : CoordinatedCallback(null)
    {
        protected override void Process()
        { uint status; NwErrorInfoV1 error; lock (host._gate) { status = host._fatalStatus; error = host._fatalError; } if (status != 0) host.Fatal(ServerError.Create(status, in error)); }
        protected override void SchedulingFault(Exception error) => host.Fatal(error);
        internal void Stop() => CompleteScheduling();
    }
    private sealed class HostTicket : CoordinatedCallback
    {
        private readonly NativeHost _host;
        private readonly ulong _handler;
        private readonly uint _kind;
        private readonly object _gate = new();
        private bool _started, _ended;
        private uint _cancellation;
        private NativeRequest? _request;
        internal readonly ulong Id;
        internal readonly int Slot;
        internal HostTicket(NativeHost host, ulong id, ulong handler, uint kind) : base(null)
        { _host = host; Id = id; _handler = handler; _kind = kind; Slot = host._coordinator.Reserve(this); }
        internal void Cancel(uint status) { lock (_gate) { if (_ended) return; if (_cancellation == 0) _cancellation = status; } Signal(); }
        internal void Fail() { lock (_gate) _request?.Abort(); Cancel(2); }
        protected override void Process()
        {
            bool start; NativeRequest? request; uint cancellation;
            lock (_gate) { if (_ended) return; start = !_started; _started = true; request = _request; cancellation = _cancellation; }
            if (start) _ = RunAsync();
            else if (request is not null && cancellation != 0) request.NotifyCancellation(cancellation);
        }
        protected override void SchedulingFault(Exception error) { _host.Fatal(error); Fail(); }
        private async Task RunAsync()
        {
            NativeRequest? request = null;
            try
            {
                request = NativeRequest.Take(_host, Id, _handler, _kind);
                if (request.InitializationFailure is { } failure) throw failure;
                uint cancellation; lock (_gate) { _request = request; cancellation = _cancellation; }
                if (cancellation != 0) request.NotifyCancellation(cancellation);
                await request.CancellationDrain().ConfigureAwait(false);
                await (_host._dispatch ?? throw new InvalidOperationException("No dispatch registry."))(request).ConfigureAwait(false);
            }
            catch (Exception error) { if (request is null) _host.Fatal(error); else request.Abort(); }
            finally
            {
                try { if (request is not null) await request.CompleteDispatchAsync().ConfigureAwait(false); }
                catch (Exception error) { _host.Fatal(error); }
                finally { lock (_gate) { _ended = true; _request = null; } CompleteScheduling(); _host.Remove(this); }
            }
        }
    }

    private sealed unsafe class OperationCalls(NativeHost host) : AsyncNativeCalls
    {
        internal override uint Start(nint operation, in NwOperationCallbacksV1 callbacks)
        { var error = new NwErrorInfoV1 { StructSize = 24 }; fixed (NwOperationCallbacksV1* pointer = &callbacks) return host.Native.nw_server_operation_start(operation, pointer, &error); }
        internal override uint RequestCancel(nint operation) => host.Native.nw_server_operation_request_cancel(operation);
        internal override uint TryRetire(nint operation) => host.Native.nw_server_operation_try_retire(operation);
        internal override Exception MapFailure(nint operation, uint status, in NwErrorInfoV1 error, CancellationToken token) => ServerError.Create(status, in error, token);
        internal override void Poison() => host.Fatal(new InvalidOperationException("A native Server operation violated its completion contract."));
    }
}
