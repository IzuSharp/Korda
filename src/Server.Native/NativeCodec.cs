using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using NativeWeave.Interop;
using NativeWeave.Interop.Abi;
using NativeWeave.Interop.Extensibility;

namespace NativeWeave.Server.Native;

internal static class NativePackage
{
    private static readonly string[] Names = ["http1", "http2", "tls", "trailers", "grpc", "mcp", "sse"];
    internal static readonly Lazy<EngineDescriptor> Descriptor = new(Create);
    internal static readonly Lazy<ServerNativeCalls> Calls = new(() => new(symbol => EngineModuleLoader.ResolveExport(Descriptor.Value, symbol)));
    internal static ServerCapabilities Inspect()
    {
        EngineInfo info = Descriptor.Value.Inspect();
        ServerCapabilities result = 0;
        for (int i = 0; i < Names.Length; i++)
            if (info.Supports(new EngineCapability(Names[i]))) result |= (ServerCapabilities)(1UL << i);
        if ((result & (ServerCapabilities)15) != (ServerCapabilities)15) throw new NotSupportedException("The Server asset must implement HTTP/1, HTTP/2, TLS and trailers.");
        return result;
    }
    private static EngineDescriptor Create()
    {
        using Stream input = typeof(NativeHost).Assembly.GetManifestResourceStream("NativeWeave.Server.native-sha256")
            ?? throw new InvalidOperationException("The Server package is missing its native asset fingerprint.");
        using var reader = new StreamReader(input);
        string hash = reader.ReadToEnd().Trim().ToLowerInvariant();
        return EnginePackage.CreateDescriptor(new EnginePackageManifest("server", "NativeWeave.Server", "0.1.0-preview.1", new AbiVersion(1, 0),
            "nw_server_get_abi_v1", EngineThreadingModel.EngineLocalAsync,
            [new NativeAssetDescriptor("linux-x64", "runtimes/linux-x64/native/libnativeweave_server.so", hash, "x86-64")],
            Names.Select((name, bit) => new EngineCapabilityDefinition(new EngineCapability(name), bit)).ToArray()), typeof(NativeHost).Assembly);
    }
}

internal static class ServerError
{
    internal static void Check(uint status, in NwErrorInfoV1 error, CancellationToken token = default)
    { if (status != 0) throw Create(status, in error, token); }
    internal static Exception Create(uint status, in NwErrorInfoV1 error, CancellationToken token = default) => status switch
    {
        2 => new OperationCanceledException("The native Server operation confirmed cancellation.", token),
        3 => new ArgumentException("The native Server rejected invalid configuration, metadata or bounds."),
        4 => new InvalidOperationException("The native Server operation is not valid in this ownership or response phase."),
        5 => new OutOfMemoryException("The native Server could not allocate bounded operation metadata."),
        6 => new NotSupportedException("The native Server asset does not implement this protocol capability."),
        7 => new ArgumentException("The native Server output capacity is too small."),
        12 => new TimeoutException("The native whole-request deadline expired."),
        13 when error.Domain == 0x53525652 && error.Code is >= 1 and <= 6 => new ServerException(error.Code switch { 1 => "resource_limit", 2 => "body_limit_exceeded", 3 => "listener_bind_failed", 5 => "tls_configuration_failed", 6 => "content_length_mismatch", _ => "transport_failed" }, "The native Server operation failed."),
        _ => new NativeEngineException("server", "operation", status, error.Domain, error.Code, error.CorrelationId, "The native Server failed; cleanup remains required.")
    };
}

internal static unsafe class NativeCodec
{
    internal static readonly UTF8Encoding Utf8 = new(false, true);
    internal static ulong Nanoseconds(TimeSpan time) => checked((ulong)time.Ticks * 100UL);
    internal static string Text(NwBytesViewV1 view, int maximum = 1_048_576)
    {
        if (view.Length > (ulong)maximum || (view.Length != 0 && view.Data == null)) throw new InvalidOperationException("Invalid native metadata view.");
        return Utf8.GetString(new ReadOnlySpan<byte>(view.Data, checked((int)view.Length)));
    }
    internal static byte[] Encode(IEnumerable<KeyValuePair<string, string>> values, int maximum = 1_048_576)
    {
        var fields = values.ToArray();
        long length = 4;
        foreach (var field in fields) length = checked(length + 8 + Utf8.GetByteCount(field.Key) + Utf8.GetByteCount(field.Value));
        if (length > maximum) throw new ArgumentException("Metadata exceeds its bounded profile.");
        byte[] result = new byte[checked((int)length)];
        BinaryPrimitives.WriteUInt32LittleEndian(result, checked((uint)fields.Length));
        int offset = 4;
        foreach (var field in fields)
        {
            int name = Utf8.GetByteCount(field.Key), value = Utf8.GetByteCount(field.Value);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(offset), (uint)name);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(offset + 4), (uint)value); offset += 8;
            offset += Utf8.GetBytes(field.Key, result.AsSpan(offset)); offset += Utf8.GetBytes(field.Value, result.AsSpan(offset));
        }
        return result;
    }
    internal static byte[] Headers(IReadOnlyDictionary<string, IReadOnlyList<string>> values, int maximum) =>
        Encode(values.SelectMany(pair => pair.Value.Select(value => new KeyValuePair<string, string>(pair.Key, value))), maximum);
    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> Decode(NwBytesViewV1 view, int maximum = 1_048_576, bool caseInsensitive = true)
    {
        if (view.Length > (ulong)maximum || view.Length < 4 || view.Data == null) throw new InvalidOperationException("Invalid native metadata list.");
        var bytes = new ReadOnlySpan<byte>(view.Data, checked((int)view.Length));
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(bytes); if (count > 16_384) throw new InvalidOperationException("Invalid native entry count.");
        StringComparer comparer = caseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var values = new Dictionary<string, List<string>>(comparer); int offset = 4;
        for (uint i = 0; i < count; i++)
        {
            if (offset > bytes.Length - 8) throw new InvalidOperationException("Truncated native metadata entry.");
            int name = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]));
            int value = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 4)..])); offset += 8;
            if (name > bytes.Length - offset || value > bytes.Length - offset - name) throw new InvalidOperationException("Truncated native metadata text.");
            string key = Utf8.GetString(bytes.Slice(offset, name)); offset += name;
            string text = Utf8.GetString(bytes.Slice(offset, value)); offset += value;
            if (!values.TryGetValue(key, out var entries)) values.Add(key, entries = []);
            entries.Add(text);
        }
        if (offset != bytes.Length) throw new InvalidOperationException("Trailing native metadata bytes.");
        return values.ToDictionary(x => x.Key, x => (IReadOnlyList<string>)x.Value.AsReadOnly(), comparer);
    }
}

/// <summary>Call-only pins. No address in this owner survives a synchronous ABI create call.</summary>
internal sealed unsafe class NativeInputs : IDisposable
{
    private readonly List<GCHandle> _pins = [];
    internal NwBytesViewV1 Text(string? value) => value is null or "" ? default : Bytes(NativeCodec.Utf8.GetBytes(value));
    internal NwBytesViewV1 Bytes(byte[] value)
    {
        if (value.Length == 0) return default;
        var pin = GCHandle.Alloc(value, GCHandleType.Pinned); _pins.Add(pin);
        return new() { Data = (byte*)pin.AddrOfPinnedObject(), Length = (ulong)value.Length };
    }
    internal T* Array<T>(T[] values) where T : unmanaged
    {
        if (values.Length == 0) return null;
        var pin = GCHandle.Alloc(values, GCHandleType.Pinned); _pins.Add(pin); return (T*)pin.AddrOfPinnedObject();
    }
    internal NwServerListenerV1 Listener(ListenerSnapshot listener)
    {
        var value = new NwServerListenerV1
        {
            struct_size = (uint)sizeof(NwServerListenerV1),
            version = 1,
            address_family = listener.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 4u : 6u,
            port = checked((uint)listener.Port),
            protocols = (uint)listener.Protocols,
            name = Text(listener.Name)
        };
        listener.Address.GetAddressBytes().CopyTo(new Span<byte>(value.address, 16));
        if (listener.Tls is { } tls)
        {
            value.flags = 1u | (tls.ClientCertificateAuthorityFile is null ? 0u : 2u);
            value.certificate_chain_file = Text(tls.CertificateChainFile); value.private_key_file = Text(tls.PrivateKeyFile); value.client_ca_file = Text(tls.ClientCertificateAuthorityFile);
            value.handshake_timeout_ns = NativeCodec.Nanoseconds(tls.HandshakeTimeout); value.max_handshakes = checked((uint)tls.MaxConcurrentHandshakes);
        }
        return value;
    }
    public void Dispose() { foreach (var pin in _pins) if (pin.IsAllocated) pin.Free(); _pins.Clear(); }
}

/// <summary>Creator-specific synchronous release. The caller supplies the native retirement gate.</summary>
internal sealed unsafe class ServerHandle : SafeHandle
{
    private readonly delegate* unmanaged[Cdecl]<nint, void> _release;
    internal ServerHandle(nint pointer, delegate* unmanaged[Cdecl]<nint, void> release) : base(0, true) { SetHandle(pointer); _release = release; }
    internal void Adopt(nint pointer) => SetHandle(pointer);
    public override bool IsInvalid => handle == 0;
    internal void Transfer() => SetHandleAsInvalid();
    protected override bool ReleaseHandle() { _release(handle); return true; }
}
