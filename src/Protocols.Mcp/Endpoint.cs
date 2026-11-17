using System.Text.Json;
using NativeWeave.Interop.Abi;
using NativeWeave.Server.Native;

namespace NativeWeave.Server.Mcp;

/// <summary>Maps a frozen optional rmcp endpoint onto the existing Rust Server Host.</summary>
/// <remarks>One pre-parse authorization gate covers automatic methods and typed calls. The same request, principal, optional scope and deadline survive one nested business continuation. GET/DELETE return405; legacy sessions, event stores and replay are disabled. This package adds no second server runtime.</remarks>
public static class McpEndpointExtensions
{
    /// <summary>Maps a literal MCP mount path and freezes the registry and copied endpoint options.</summary>
    /// <param name="routes">The application or route group configured before startup.</param>
    /// <param name="pattern">A literal route without placeholders or wildcards.</param>
    /// <param name="registry">Finite static tools/resources/prompts; later mutation fails.</param>
    /// <param name="options">Optional exact modern protocol limits and authority/origin policy, copied now.</param>
    /// <returns>The endpoint builder for names, generic timeout and authorization policies.</returns>
    /// <exception cref="ArgumentException">Path, limits, authority/origin or registry metadata is invalid.</exception>
    /// <exception cref="InvalidOperationException">The route registry is frozen or an HTTP filter/body override is inherited.</exception>
    /// <remarks>Raw input is always unavailable. Before allow, middleware may configure a denial response; calling next afterward rejects. After allow, rmcp exclusively owns response framing, while OnCompleted/disposal hooks remain available. Empty AllowedHosts derives exact bound loopback authorities; non-loopback listeners require an explicit allowlist.</remarks>
    public static ServerEndpointBuilder MapMcp(this ServerRoutes routes, string pattern, McpRegistry registry, McpEndpointOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(routes); ArgumentNullException.ThrowIfNull(registry); ArgumentNullException.ThrowIfNull(pattern);
        if ((routes.Group.Prefix + pattern).IndexOfAny(['{', '}', '*']) >= 0) throw new ArgumentException("MCP requires a literal mount path.", nameof(pattern));
        routes.Registry.Mutable();
        var descriptor = new McpDescriptor(registry.Snapshot(), options ?? new());
        descriptor.Register(routes.Registry.Routes.Count + 1L);
        var definition = routes.Registry.Add(routes.Group, pattern, ["POST"], null, descriptor.AllowAsync);
        definition.Kind = 2; definition.ProtocolDescriptor = descriptor; registry.Freeze();
        return new(routes.Registry, definition);
    }
}
internal sealed class McpDescriptor : INativeServerProtocolDescriptor
{
    private readonly McpEntry[] _entries;
    private readonly Dictionary<ulong, McpEntry> _handlers = [];
    private readonly string[] _hosts, _origins;
    private readonly int _wire, _depth, _nodes, _strings;
    public byte[] RegistrationUtf8 { get; private set; } = [];
    internal McpDescriptor(McpEntry[] entries, McpEndpointOptions options)
    {
        _entries = entries;
        if (options.ProtocolVersion != McpProtocolVersion.V2026_07_28 || options.MaxRequestBytes is < 1 or > 1_048_576 || options.MaxJsonDepth is < 1 or > 32 || options.MaxJsonNodes is < 1 or > 4096 || options.MaxJsonStringBytes is < 1 or > 262_144) throw new ArgumentException("MCP supports only the finite2026-07-28 stateless profile.", nameof(options));
        _wire = options.MaxRequestBytes; _depth = options.MaxJsonDepth; _nodes = options.MaxJsonNodes; _strings = options.MaxJsonStringBytes;
        _hosts = options.AllowedHosts.ToArray(); _origins = options.AllowedOrigins.ToArray();
        if (_hosts.Length > 256 || _origins.Length > 256 || _hosts.Distinct(StringComparer.OrdinalIgnoreCase).Count() != _hosts.Length || _origins.Distinct(StringComparer.Ordinal).Count() != _origins.Length) throw new ArgumentException("Invalid/duplicate MCP authority or origin list.");
        foreach (var host in _hosts)
        {
            McpValidation.Text(host, 1024, nameof(options));
            if (!Authority(host)) throw new ArgumentException("An allowed host must be an exact authority with an explicit port.");
        }
        foreach (var origin in _origins)
        {
            McpValidation.Text(origin, 2048, nameof(options));
            int separator = origin.IndexOf("://", StringComparison.Ordinal);
            if (separator < 0 || origin[..separator] is not ("http" or "https") || !Authority(origin[(separator + 3)..])) throw new ArgumentException("An allowed origin must be an exact HTTP(S) scheme/host/explicit port.");
        }
    }
    private static bool Authority(string authority)
    {
        int separator = authority.LastIndexOf(':');
        return separator > 0 && authority[(separator + 1)..].All(char.IsAsciiDigit)
            && int.TryParse(authority[(separator + 1)..], out int port) && port is > 0 and <= 65535
            && authority.IndexOfAny(['*', '@', '/', '?', '#', '\\']) < 0 && !authority.Any(char.IsWhiteSpace)
            && Uri.TryCreate("http://" + authority, UriKind.Absolute, out var uri) && uri.Host.Length > 0 && uri.Port == port;
    }
    internal void Register(long routeId)
    {
        using var buffer = new BoundedJsonBuffer(4 * 1024 * 1024);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject(); writer.WriteNumber("version", 1); writer.WriteString("kind", "mcp"); writer.WriteNumber("handler_id", routeId); writer.WriteString("protocol_version", "2026-07-28"); writer.WriteBoolean("legacy_sessions", false); writer.WriteBoolean("event_store", false); writer.WriteBoolean("strict_metadata", true);
            writer.WriteStartArray("allowed_hosts"); foreach (var host in _hosts) writer.WriteStringValue(host); writer.WriteEndArray(); writer.WriteStartArray("allowed_origins"); foreach (var origin in _origins) writer.WriteStringValue(origin); writer.WriteEndArray();
            writer.WriteNumber("max_request_bytes", _wire); writer.WriteNumber("max_json_depth", _depth); writer.WriteNumber("max_json_nodes", _nodes); writer.WriteNumber("max_json_string_bytes", _strings);
            string[] names = ["tools", "resources", "prompts"]; ulong index = 0;
            for (uint kind = 0; kind < 3; kind++)
            {
                writer.WriteStartArray(names[kind]);
                foreach (var entry in _entries.Where(e => e.Kind == kind)) { ulong id = checked(((ulong)routeId << 32) + ++index); _handlers.Add(id, entry); entry.Metadata(writer, id); }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
        RegistrationUtf8 = buffer.WrittenMemory.ToArray();
    }
    public void Freeze(RouteDefinition route, ServerConfiguration configuration)
    {
        route.EffectiveMaxRequestBytes = _wire; route.EffectiveMaxJsonBytes = 0;
        if (_hosts.Length == 0 && configuration.Listeners.Any(l => !System.Net.IPAddress.IsLoopback(l.Address))) throw new InvalidOperationException("A non-loopback MCP listener requires explicit AllowedHosts.");
    }
    private Gate State(RequestContext context)
    {
        if (context.ProtocolState is Gate state) return state;
        if (context.Native is not NativeRequest native || (native.Engine.Capabilities & ServerCapabilities.Mcp) == 0) throw new NotSupportedException("The selected native Server asset lacks MCP capability.");
        state = new(this, context, native); context.ProtocolState = state; context.Native.ProtocolContinuation = state.InvokeAsync; return state;
    }
    internal Task AllowAsync(RequestContext context, CancellationToken token) => State(context).AllowAsync(token);
    public Task BeforeHttpResponseStartAsync(RequestContext context, CancellationToken token) => State(context).DenyAsync(token);
    public async Task CompleteDispatchAsync(RequestContext context, Exception? failure)
    {
        var state = State(context);
        try
        {
            if (state.Allowed) { if (failure is not null) context.Native.Abort(); return; }
            await state.DenyAsync(CancellationToken.None).ConfigureAwait(false);
            if (failure is not null && !context.Response.HasPendingConfiguration) context.Response.StatusCode = 500;
            await context.Response.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally { await context.Native.JoinProtocolContinuationAsync().ConfigureAwait(false); }
    }
    private sealed class Gate(McpDescriptor descriptor, RequestContext context, NativeRequest native)
    {
        private readonly object _gate = new(); private int _phase; private Task? _decision;
        internal bool Allowed => Volatile.Read(ref _phase) == 1;
        internal Task AllowAsync(CancellationToken token)
        {
            lock (_gate)
            {
                if (_phase != 0 || context.Response.HasPendingConfiguration) throw new InvalidOperationException("An MCP allow requires an undecided gate without pending HTTP response configuration.");
                var pointer = Create(1); _phase = 1; context.ProtocolHttpAllowed = false;
                return _decision = RunAsync(pointer, token);
            }
        }
        internal Task DenyAsync(CancellationToken token)
        {
            lock (_gate)
            {
                if (_phase == 1) throw new InvalidOperationException("Raw response output is forbidden after MCP allow.");
                if (_decision is not null) return _decision;
                var pointer = Create(0); _phase = 2; return _decision = RunAsync(pointer, token);
            }
        }
        private unsafe nint Create(uint decision) => native.Engine.Create(native.Owner, (nint owner, nint* output, NwErrorInfoV1* error) => native.Engine.Native.nw_server_mcp_gate_resume_create(owner, decision, output, error));
        private async Task RunAsync(nint pointer, CancellationToken token)
        {
            var operation = native.Engine.Begin(pointer, native.Engine.TakeVoid, token, native);
            try { await operation.Task.ConfigureAwait(false); }
            finally { await operation.Retirement.ConfigureAwait(false); }
        }
        internal async Task InvokeAsync(ulong handler)
        {
            if (!Allowed || !descriptor._handlers.TryGetValue(handler, out var entry)) throw new InvalidOperationException("Unknown or out-of-sequence MCP continuation.");
            var arguments = TakeArguments(entry.Kind);
            var reply = await entry.InvokeAsync(arguments, new(context, entry.Name)).ConfigureAwait(false);
            // Both admissions precede encoding/copy. The native reservation stays
            // charged through SDK conversion and response production, independently
            // of the short reply operation's terminal notification.
            using var reservation = await native.ReserveProtocolWriteAsync(3 * 1024 * 1024, 3, context.RequestAborted).ConfigureAwait(false);
            // The full2MiB managed output overlap is already held by native
            // preflight before SDK parsing, and survives any canceled gate until
            // this nested invocation and the shared request have actually retired.
            byte[] bytes;
            try { bytes = reply.Encode(); }
            catch
            {
                reply = entry.Kind == 0 ? McpReply.ToolError("Tool output failed") : McpReply.Error(-32603, "Application output failed"); bytes = reply.Encode();
            }
            nint pointer;
            try { pointer = CreateReply(reservation, reply.Kind, bytes); }
            catch (ArgumentException)
            {
                // Native finite-schema validation can reject an otherwise valid
                // JSON result (for example a null non-nullable member). Failed
                // create leaves this exact reservation owned by the caller.
                reply = entry.Kind == 0 ? McpReply.ToolError("Tool output failed") : McpReply.Error(-32603, "Application output failed");
                pointer = CreateReply(reservation, reply.Kind, reply.Encode());
            }
            var operation = native.Engine.Begin(pointer, native.Engine.TakeVoid, context.RequestAborted, native);
            try { await operation.Task.ConfigureAwait(false); }
            finally { await operation.Retirement.ConfigureAwait(false); }
        }
        private unsafe byte[] TakeArguments(uint expected)
        {
            bool lease = false; nint buffer = 0;
            try
            {
                var error = new NwErrorInfoV1 { StructSize = 24 }; uint kind = 0;
                ServerError.Check(native.Engine.Native.nw_server_mcp_call_take(native.Owner.Acquire(ref lease), &kind, &buffer, &error), in error);
                if (kind != expected || buffer == 0) throw new InvalidOperationException("MCP argument owner/kind mismatch."); ulong length = 0;
                ServerError.Check(native.Engine.Native.nw_server_buffer_length(buffer, &length, &error), in error); if (length is 0 or > 1_048_576) throw new InvalidOperationException("MCP argument owner exceeded its pre-reserved bound.");
                var bytes = GC.AllocateUninitializedArray<byte>(checked((int)length)); fixed (byte* data = bytes) { ulong copied = 0; ServerError.Check(native.Engine.Native.nw_server_buffer_copy(buffer, data, length, &copied, &error), in error); if (copied != length) throw new InvalidOperationException("MCP argument copy mismatch."); }
                return bytes;
            }
            finally { if (buffer != 0) native.Engine.Native.nw_server_buffer_release(buffer); native.Owner.Release(ref lease); }
        }
        private unsafe nint CreateReply(ServerHandle reservation, uint kind, byte[] bytes)
        {
            bool ownerLease = false, tokenLease = false;
            try
            {
                reservation.DangerousAddRef(ref tokenLease); var error = new NwErrorInfoV1 { StructSize = 24 }; nint operation = 0;
                fixed (byte* data = bytes) ServerError.Check(native.Engine.Native.nw_server_mcp_reply_create(native.Owner.Acquire(ref ownerLease), reservation.DangerousGetHandle(), kind, new NwBytesViewV1 { Data = data, Length = (ulong)bytes.Length }, &operation, &error), in error);
                reservation.Transfer(); return operation;
            }
            finally { if (tokenLease) reservation.DangerousRelease(); native.Owner.Release(ref ownerLease); }
        }
    }
}
