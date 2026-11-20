using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Serialization;
using Google.Protobuf;
using NativeWeave.Grpc.Tests.Messages;
using NativeWeave.Interop.Async;
using NativeWeave.Server;
using NativeWeave.Server.Grpc;
using G = Grpc.Core;

await Acceptance.Run(args);

internal static partial class Acceptance
{
    private static int _checks, _unary, _server, _retirements, _retirementFailures;
    private static readonly GrpcMarshaller<EchoMessage> Echo = new(static (v, w) => v.WriteTo(w), static b => EchoMessage.Parser.ParseFrom(b));
    private static readonly GrpcMarshaller<CountMessage> Count = new(static (v, w) => v.WriteTo(w), static b => CountMessage.Parser.ParseFrom(b));
    private static GrpcMethod<EchoMessage, EchoMessage> Method(string name, GrpcMethodKind kind = GrpcMethodKind.Unary) => new("/nativeweave.Test/" + name, kind, Echo, Echo);
    private static GrpcMethodOptions Limits(int decoded = 8192, long stream = 65536) => new() { MaxEncodedMessageBytes = 8192, MaxDecodedMessageBytes = decoded, MaxStreamBytes = stream, Timeout = TimeSpan.FromSeconds(3) };
    internal static void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); Interlocked.Increment(ref _checks); Console.WriteLine("PASS " + name); }
    private static async Task Throws<T>(Func<Task> work) where T : Exception
    { try { await work(); } catch (T) { Interlocked.Increment(ref _checks); return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Observe(AsyncSnapshot s)
    {
        if (!s.Released || s.RootLive || s.Controls != 0 || s.CallbackEntries != 0 || s.RetainedInputBytes != 0 || s.Start is AsyncStartOutcome.AcceptedReturned && !s.NativeQuiet)
            Interlocked.Increment(ref _retirementFailures);
        Interlocked.Increment(ref _retirements);
    }
    internal static async Task Run(string[] args)
    {
        if (args.Contains("--require-aot")) Check(!RuntimeFeature.IsDynamicCodeSupported, "Actual NativeAOT process");
        string native = Path.Combine(AppContext.BaseDirectory, "runtimes/linux-x64/native/libnativeweave_server.so");
        string hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(native)));
        string directory = Path.Combine(Path.GetTempPath(), "nw-b18-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        using var key = RSA.Create(2048);
        var certificate = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); san.AddIpAddress(IPAddress.Loopback); certificate.CertificateExtensions.Add(san.Build());
        using var signed = certificate.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        string certPath = Path.Combine(directory, "certificate.pem"), keyPath = Path.Combine(directory, "private-key.pem");
        File.WriteAllText(certPath, signed.ExportCertificatePem()); File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
        try
        {
            await StartupContracts();
            foreach (bool tls in new[] { false, true }) await RunHost(tls, certPath, keyPath);
            await WriterContracts(certPath, keyPath);
            await PersistentHeaderCredits(certPath, keyPath);
            await WriterAcrossCalls(certPath, keyPath);
            await PressureContracts();
            Check(_retirements > 50 && _retirementFailures == 0, "Separately observed managed A07 retirement fields");
            Check(hash == Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(native))), "Native asset unchanged across services and transports");
            Console.WriteLine($"B18 actual Rust/Tonic: assertions={_checks}; managed retirements={_retirements}; dynamic={RuntimeFeature.IsDynamicCodeSupported}; native={hash}");
        }
        finally { Directory.Delete(directory, true); }
    }
    private static async Task RunHost(bool tls, string certPath, string keyPath)
    {
        Console.WriteLine("TRANSPORT " + (tls ? "HTTP2+rustls" : "HTTP2-clear"));
        var builder = NativeServer.CreateBuilder();
        builder.Listen(IPAddress.Loopback, 0, options => { options.Protocols = ServerProtocols.Http2; if (tls) options.Tls = ServerTlsOptions.FromPemFiles(certPath, keyPath); });
        builder.UseAuthentication(new Authenticator()).UseAuthorization(new Authorizer());
        builder.Options.RequestTimeout = TimeSpan.FromSeconds(3);
        await using var app = builder.Build();
        app.Use(async (request, next, token) => { ((NativeRequest)request.Native).Engine.TestRetirement = Observe; await next(request, token); });
        app.MapGrpcUnary(Method("Unary"), (v, _) => { Interlocked.Increment(ref _unary); return Task.FromResult(v); }, Limits());
        app.MapGrpcServerStreaming(Method("Server", GrpcMethodKind.ServerStreaming), Stream, Limits());
        app.MapGrpcClientStreaming(new GrpcMethod<EchoMessage, CountMessage>("/nativeweave.Test/Client", GrpcMethodKind.ClientStreaming, Echo, Count), Sum, Limits());
        app.MapGrpcDuplex(Method("Duplex", GrpcMethodKind.DuplexStreaming), Duplex, Limits());
        app.MapGrpcUnary(Method("Denied"), static (v, _) => Task.FromResult(v), Limits()).RequireAuthorization("admin");
        app.MapGrpcUnary(Method("Metadata"), Metadata, Limits());
        app.MapGrpcUnary(Method("EmptyMetadata"), static (v, context) => Task.FromResult(new EchoMessage { Text = string.Join(',', context.RequestHeaders.GetBinaryValues("values-bin").Select(value => value.Length)) }), Limits());
        app.MapGrpcUnary(Method("RawGuards"), RawGuards, Limits());
        app.MapGrpcUnary(Method("ExplicitFailure"), static (_, _) => throw new GrpcException(GrpcStatusCode.NotFound, "missing", ErrorTrailers()), Limits());
        app.MapGrpcUnary(Method("UnexpectedFailure"), static (_, _) => throw new InvalidOperationException("sensitive application detail"), Limits());
        app.MapGrpcUnary(Method("DecodedLimit"), static (v, _) => Task.FromResult(v), Limits(decoded: 64));
        app.MapGrpcServerStreaming(Method("Deadline", GrpcMethodKind.ServerStreaming), Deadline, Limits());
        app.MapGrpcClientStreaming(Method("Early", GrpcMethodKind.ClientStreaming), Early, Limits());
        // A second independent static message/parser and registration uses the same asset.
        app.MapGrpc(new CountService());
        var edges = MapEdges(app);
        await app.StartAsync();
        using var trustedCertificate = X509CertificateLoader.LoadCertificateFromFile(certPath);
        string fingerprint = trustedCertificate.GetCertHashString();
        using var handler = new SocketsHttpHandler { SslOptions = { RemoteCertificateValidationCallback = (_, presented, _, _) => presented?.GetCertHashString() == fingerprint } };
        using var client = new HttpClient(handler) { BaseAddress = app.BoundAddresses.Single(), DefaultRequestVersion = HttpVersion.Version20, DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact, Timeout = TimeSpan.FromSeconds(8) };
        using var channel = global::Grpc.Net.Client.GrpcChannel.ForAddress(client.BaseAddress!, new global::Grpc.Net.Client.GrpcChannelOptions { HttpClient = client });
        var invoker = channel.CreateCallInvoker();
        var message = new EchoMessage { Text = "linux", Number = 4, Payload = ByteString.CopyFrom([0, 1, 255]) };
        using (var call = invoker.AsyncUnaryCall(ClientMethod("Unary", G.MethodType.Unary), null, new G.CallOptions(), message))
        { Check((await call.ResponseAsync).Equals(message), "Independent grpc-dotnet unary client"); Check(call.GetStatus().StatusCode == G.StatusCode.OK, "Unary OK trailers"); }
        using (var call = invoker.AsyncServerStreamingCall(ClientMethod("Server", G.MethodType.ServerStreaming), null, new G.CallOptions(), message))
        { var values = new List<int>(); while (await call.ResponseStream.MoveNext(default)) values.Add(call.ResponseStream.Current.Number); Check(values.SequenceEqual([4, 5]), "Independent server streaming boundaries"); }
        var countMarshaller = G.Marshallers.Create<CountMessage>(static v => v.ToByteArray(), static b => CountMessage.Parser.ParseFrom(b));
        var clientMethod = new G.Method<EchoMessage, CountMessage>(G.MethodType.ClientStreaming, "nativeweave.Test", "Client", ClientMarshaller(), countMarshaller);
        using (var call = invoker.AsyncClientStreamingCall(clientMethod, null, new G.CallOptions()))
        { await call.RequestStream.WriteAsync(new() { Number = 3 }); await call.RequestStream.WriteAsync(new() { Number = 7 }); await call.RequestStream.CompleteAsync(); Check((await call.ResponseAsync).Total == 10, "Client streaming and half-close"); }
        using (var call = invoker.AsyncDuplexStreamingCall(ClientMethod("Duplex", G.MethodType.DuplexStreaming), null, new G.CallOptions()))
        {
            await call.RequestStream.WriteAsync(message); Check(await call.ResponseStream.MoveNext(default) && call.ResponseStream.Current.Equals(message), "Duplex emits before input half-close");
            await call.RequestStream.WriteAsync(new() { Text = "second" }); await call.RequestStream.CompleteAsync();
            Check(await call.ResponseStream.MoveNext(default) && call.ResponseStream.Current.Text == "second" && !await call.ResponseStream.MoveNext(default), "Duplex bounded ordered messages and EOF");
        }
        using (var call = invoker.AsyncUnaryCall(new G.Method<CountMessage, CountMessage>(G.MethodType.Unary, "nativeweave.Counter", "Increment", countMarshaller, countMarshaller), null, new(), new CountMessage { Total = 40 }))
            Check((await call.ResponseAsync).Total == 41, "Second static generated message service");

        var wire = Frame(message.ToByteArray());
        foreach (string path in new[] { "Unary", "Server" })
        {
            int before = Volatile.Read(ref _unary) + Volatile.Read(ref _server);
            Check((await Raw(client, path, [])).Status == 13, "Zero frames rejected by Tonic " + path);
            Check((await Raw(client, path, [0, 0, 0, 0, 0])).Status == 0, "Zero-byte protobuf message admitted " + path);
            int afterEmpty = Volatile.Read(ref _unary) + Volatile.Read(ref _server);
            Check(afterEmpty == before + 1, "Empty message invokes exactly once " + path);
            Check((await Raw(client, path, [.. wire, .. wire])).Status == 3, "Second unary frame rejected " + path);
            Check(Volatile.Read(ref _unary) + Volatile.Read(ref _server) == afterEmpty, "Invalid cardinality never invokes handler " + path);
            Check((await Raw(client, path, wire)).Status == 0, "Fresh decoder state after failure " + path);
        }
        Check((await Raw(client, "Unknown", wire)).Status == 12, "Unknown method is Unimplemented");
        Check((await Raw(client, "Unary", [0, 0, 0, 32, 1])).Status == 11, "Framed length exceeds encoded cap: OutOfRange");
        Check((await Raw(client, "DecodedLimit", Frame(new EchoMessage { Text = new string('x', 100) }.ToByteArray()))).Status == 8, "Decoded cap: ResourceExhausted");
        Check((await Raw(client, "Unary", [0, 0, 0, 0, 3, 8])).Status == 13, "Truncated protobuf frame preserves Tonic classification");
        Check((await Raw(client, "Unary", [1, 0, 0, 0, 0])).Status != 0, "Unnegotiated compressed frame rejects");
        Check((await Raw(client, "Denied", wire)).Status == 16, "Anonymous authorization is framed Unauthenticated");
        Check((await Raw(client, "Denied", wire, [("x-user", "reader")])).Status == 7, "Authenticated denial is framed PermissionDenied");
        Check((await Raw(client, "Denied", wire, [("x-user", "admin")])).Status == 0, "Authorized gRPC handler");
        var explicitError = await Raw(client, "ExplicitFailure", wire);
        Check(explicitError.Status == 5 && (explicitError.Trailers.Contains("x-error") || explicitError.Headers.Contains("x-error")), "Explicit status and trailers-only metadata survive");
        using (var errorCall = invoker.AsyncUnaryCall(ClientMethod("ExplicitFailure", G.MethodType.Unary), null, new(), message))
        {
            try { await errorCall.ResponseAsync; throw new InvalidOperationException("Expected explicit gRPC error"); }
            catch (G.RpcException error) { Check(error.StatusCode == G.StatusCode.NotFound && error.Trailers.GetValue("x-error") == "explicit", "Independent gRPC client recognizes error trailers"); }
        }
        var hidden = await Raw(client, "UnexpectedFailure", wire);
        Check(hidden.Status == 13 && !hidden.Detail.Contains("sensitive", StringComparison.Ordinal), "Unexpected exception is redacted");
        Check((await Raw(client, "Unary", Frame([128]))).Status == 3, "Static protobuf decoder failure is InvalidArgument");
        Check((await Raw(client, "RawGuards", wire)).Status == 0, "Forbidden raw operations leave typed response usable");
        foreach (string bad in new[] { "", "1", "123456789n", "-1S", "1Q", "1S,2S" })
            Check((await Raw(client, "Unary", wire, [("grpc-timeout", bad)])).Status == 3, "Strict timeout rejects " + bad);
        var metadata = await Raw(client, "Metadata", wire, [("values-bin", "AQI, /w=="), ("x-repeat", "first"), ("x-repeat", "second")]);
        Check(metadata.Status == 0 && metadata.Headers.Contains("initial-bin") && metadata.Trailers.Contains("ending-bin"), "Binary metadata headers/trailers round trip");
        foreach (var row in new[] { ("", "0"), ("AQI,", "2,0"), (",AQI", "0,2"), ("AQI, ,/w==", "2,0,1") })
        {
            var empty = await Raw(client, "EmptyMetadata", wire, [("values-bin", row.Item1)]);
            Check(empty.Status == 0 && EchoMessage.Parser.ParseFrom(empty.Payload.AsSpan(5)).Text == row.Item2, "Empty binary segments remain ordered entries");
        }
        foreach (string bad in new[] { "%%%", "A===", "YQ=", "A QI" })
            Check((await Raw(client, "Unary", wire, [("values-bin", bad)])).Status == 3, "Invalid binary metadata rejects: " + bad);
        var deadline = await Raw(client, "Deadline", wire, [("grpc-timeout", "80m")]);
        Check(deadline.Status == 4 && deadline.Headers.Contains("x-started"), "Whole-call deadline survives committed response headers");
        Check((await Raw(client, "Early", [.. wire, .. wire])).Status == 0, "Stopping input preserves output status");
        await RunEdges(app, client, invoker, edges, wire);
        var close = await app.CloseAsync(TimeSpan.FromSeconds(3));
        Check(close.Status == ServerCloseStatus.Completed && close.PendingRequests == 0 && close.RetainedManagedBytes == 0 && close.RetainedNativeBytes == 0, "Close separately joins native and managed protocol ownership");
    }
    private static G.Marshaller<EchoMessage> ClientMarshaller() => G.Marshallers.Create<EchoMessage>(static v => v.ToByteArray(), static b => EchoMessage.Parser.ParseFrom(b));
    private static G.Method<EchoMessage, EchoMessage> ClientMethod(string name, G.MethodType kind) => new(kind, "nativeweave.Test", name, ClientMarshaller(), ClientMarshaller());
    private static async IAsyncEnumerable<EchoMessage> Stream(EchoMessage value, GrpcCallContext context)
    { Interlocked.Increment(ref _server); yield return value; await Task.CompletedTask; yield return new() { Number = value.Number + 1 }; }
    private static async Task<CountMessage> Sum(IAsyncEnumerable<EchoMessage> values, GrpcCallContext context)
    { long total = 0; await foreach (var value in values.WithCancellation(context.CancellationToken)) total += value.Number; return new() { Total = total }; }
    private static async IAsyncEnumerable<EchoMessage> Duplex(IAsyncEnumerable<EchoMessage> values, GrpcCallContext context)
    { await foreach (var value in values.WithCancellation(context.CancellationToken)) yield return value; }
    private static async Task<EchoMessage> Early(IAsyncEnumerable<EchoMessage> values, GrpcCallContext context)
    { await using var iterator = values.GetAsyncEnumerator(context.CancellationToken); return await iterator.MoveNextAsync() ? iterator.Current : new(); }
    private static GrpcMetadata ErrorTrailers() { var result = new GrpcMetadata(); result.Add("x-error", "explicit"); return result; }
    private static async Task<EchoMessage> Metadata(EchoMessage value, GrpcCallContext context)
    {
        var values = context.RequestHeaders.GetBinaryValues("values-bin");
        Check(values.Count == 2 && values[0].AsSpan().SequenceEqual(new byte[] { 1, 2 }) && values[1].AsSpan().SequenceEqual(new byte[] { 255 }), "Rust splits OWS comma binary metadata before Tonic decoding");
        Check(context.RequestContext.Request.Headers["values-bin"].Single().Contains("AQI", StringComparison.Ordinal), "Ordinary HTTP metadata preserves wire text");
        var initial = new GrpcMetadata(); initial.AddBinary("initial-bin", [0, 3, 255]); await context.WriteResponseHeadersAsync(initial);
        await Throws<InvalidOperationException>(() => context.WriteResponseHeadersAsync(initial));
        context.ResponseTrailers.AddBinary("ending-bin", [4, 5]);
        return value;
    }
    private static async Task<EchoMessage> RawGuards(EchoMessage value, GrpcCallContext call)
    {
        var c = call.RequestContext;
        foreach (Action work in new Action[]
        {
            () => _ = c.Request.Body, () => _ = c.Request.GetTrailers(),
            () => _ = c.Response.Body, () => _ = c.Response.Headers, () => _ = c.Response.Trailers,
            () => _ = c.Response.StatusCode, () => c.Response.StatusCode = 201,
            () => _ = c.Response.ContentType, () => c.Response.ContentType = "text/plain",
            () => _ = c.Response.ContentLength, () => c.Response.ContentLength = 0,
            () => _ = c.Response.HasStarted, () => c.Response.OnStarting(_ => Task.CompletedTask)
        }) await Throws<InvalidOperationException>(() => { work(); return Task.CompletedTask; });
        foreach (Func<Task> work in new Func<Task>[]
        {
            async () => { await c.Request.ReadChunkAsync(); }, async () => { await c.Request.ReadJsonAsync(GuardJson.Default.JsonProbe); },
            () => c.Response.StartAsync(), () => c.Response.WriteAsync("x"u8.ToArray()), () => c.Response.FlushAsync(), () => c.Response.CompleteAsync(),
            () => c.Response.WriteJsonAsync(new JsonProbe("x"), GuardJson.Default.JsonProbe),
            () => Results.Text("x").ExecuteAsync(c), () => Results.Json(new JsonProbe("x"), GuardJson.Default.JsonProbe).ExecuteAsync(c),
            () => Results.Bytes("x"u8.ToArray()).ExecuteAsync(c)
        }) await Throws<InvalidOperationException>(work);
        return value;
    }
    private static async IAsyncEnumerable<EchoMessage> Deadline(EchoMessage value, GrpcCallContext context)
    { var headers = new GrpcMetadata(); headers.Add("x-started", "yes"); await context.WriteResponseHeadersAsync(headers); yield return value; await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken); }
    private static byte[] Frame(byte[] payload)
    { byte[] bytes = new byte[payload.Length + 5]; BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(1), (uint)payload.Length); payload.CopyTo(bytes, 5); return bytes; }
    private static async Task<WireResult> Raw(HttpClient client, string method, byte[] bytes, (string, string)[]? headers = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/nativeweave.Test/" + method) { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact, Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc"); request.Headers.TryAddWithoutValidation("te", "trailers");
        if (headers is not null) foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);
        using var response = await client.SendAsync(request); byte[] payload = await response.Content.ReadAsByteArrayAsync();
        Check(response.Version == HttpVersion.Version20 && response.StatusCode == HttpStatusCode.OK, "gRPC HTTP2 response head");
        string Get(string name) => response.TrailingHeaders.TryGetValues(name, out var tail) ? tail.Single() : response.Headers.TryGetValues(name, out var head) ? head.Single() : "";
        string status = Get("grpc-status"); if (!int.TryParse(status, out int code)) throw new InvalidOperationException("No gRPC terminal status for " + method);
        return new(code, Uri.UnescapeDataString(Get("grpc-message")), payload, response.Headers.Select(x => x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase), response.TrailingHeaders.Select(x => x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase));
    }
    private sealed record WireResult(int Status, string Detail, byte[] Payload, HashSet<string> Headers, HashSet<string> Trailers);
    private sealed class Authenticator : IServerAuthenticator
    { public Task<ClaimsPrincipal?> AuthenticateAsync(RequestContext context, CancellationToken cancellationToken = default) => Task.FromResult(context.Request.Headers.GetSingle("x-user") is { } user ? new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, user)], "test")) : null); }
    private sealed class Authorizer : IServerAuthorizer
    { public Task<bool> AuthorizeAsync(RequestContext context, string policy, CancellationToken cancellationToken = default) => Task.FromResult(context.User.Identity?.Name == "admin"); }
    private sealed class CountService : IGeneratedGrpcService
    { public void Map(ServerRoutes routes) => routes.MapGrpcUnary(new GrpcMethod<CountMessage, CountMessage>("/nativeweave.Counter/Increment", GrpcMethodKind.Unary, Count, Count), static (v, _) => Task.FromResult(new CountMessage { Total = v.Total + 1 }), Limits()); }
}
internal sealed record JsonProbe(string Value);
[JsonSerializable(typeof(JsonProbe))]
internal partial class GuardJson : JsonSerializerContext { }
