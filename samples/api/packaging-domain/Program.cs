using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using NativeWeave.Interop;
using NativeWeave.Interop.Extensibility;
#if SQLITE || POSTGRES
using NativeWeave.Data;
using NativeWeave.Database;
#endif
#if SQLITE
using NativeWeave.Database.Sqlite;
#endif
#if POSTGRES
using NativeWeave.Database.PostgreSql;
#endif
#if SERVER
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using NativeWeave.Server;
#endif
#if GRPC
using System.Buffers.Binary;
using NativeWeave.Server.Grpc;
#endif
#if MCP
using NativeWeave.Server.Mcp;
#endif

// The controller compiles this same application against six independently
// restored PackageReference graphs. Conditional sections never import an
// unselected provider or protocol facade. All domain execution uses Rust.
internal static class Program
{
    private static readonly List<string> Checks = [];
    internal static void Check(string name, bool value)
    {
        if (!value) throw new InvalidOperationException(name);
        Checks.Add(name);
    }

    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Contains("--require-aot"))
                Check("actual-native-aot", !RuntimeFeature.IsDynamicCodeSupported);
            // This is a real bootstrap call through Core's public maintainer
            // descriptor API, before any source/server/domain owner exists.
            if (args.Contains("--reject-abi"))
            {
                RejectAbi();
                Console.WriteLine("B24_ABI_REJECTED_BEFORE_DOMAIN_OWNER");
                return 0;
            }
            string provider = Environment.GetEnvironmentVariable("B24_PROVIDER") ?? "none";
#if SQLITE || POSTGRES
            await using var database = await DatabaseFixture.Create(provider);
            Check("typed-native-database-roundtrip", await database.Count() == 5);
#if MANAGED_EDIT
            await AddedModels.Verify(database.Source);
            Check("ordinary-added-entity-dto-handler", true);
#endif
#endif
#if SERVER
            await ServerCases(
#if SQLITE || POSTGRES
                database.Count
#else
                static () => Task.FromResult(5L)
#endif
            );
#endif
#if SQLITE || POSTGRES
            await database.DisposeAsync();
#endif
            var receipt = new ConsumerReceipt(1, "B24", provider,
                RuntimeFeature.IsDynamicCodeSupported, Checks.ToArray());
            Console.WriteLine("B24_RESULT " + JsonSerializer.Serialize(receipt, ConsumerJson.Default.ConsumerReceipt));
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void RejectAbi()
    {
        string file = Path.Combine(AppContext.BaseDirectory, "NativeWeave.database.sqlite.assets.json");
        using var json = JsonDocument.Parse(File.ReadAllText(file));
        var record = json.RootElement;
        string native = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, record.GetProperty("outputPath").GetString()!));
        var manifest = new EnginePackageManifest("database.sqlite", record.GetProperty("packageId").GetString()!,
            record.GetProperty("packageVersion").GetString()!, new AbiVersion(99, 0),
            "nw_database_sqlite_get_abi_v1", EngineThreadingModel.EngineLocalAsync,
            [new NativeAssetDescriptor("linux-x64", record.GetProperty("sourcePath").GetString()!,
                record.GetProperty("sha256").GetString()!, "x86-64")], []);
        var descriptor = EnginePackage.CreateDescriptor(manifest, typeof(Program).Assembly);
        try { descriptor.Inspect(new() { NativeAssetPath = native }); }
        catch (EngineLoadException error) when (error.Failure == EngineLoadFailure.IncompatibleAbi)
        { Check("actual-domain-bootstrap-incompatible-abi", true); return; }
        throw new InvalidOperationException("Expected actual incompatible domain bootstrap rejection.");
    }

#if SERVER
    private static async Task ServerCases(Func<Task<long>> count)
    {
        string tls = Path.Combine(Path.GetTempPath(), "nativeweave-b24-tls-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tls);
        try
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("localhost"); names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            string cert = Path.Combine(tls, "localhost.pem"), privateKey = Path.Combine(tls, "localhost.private.pem");
            await File.WriteAllTextAsync(cert, certificate.ExportCertificatePem());
            await File.WriteAllTextAsync(privateKey, key.ExportPkcs8PrivateKeyPem());
            var builder = NativeServer.CreateBuilder();
            builder.Options.RequestTimeout = TimeSpan.FromSeconds(15);
            builder.Listen(IPAddress.Loopback, 0, options =>
            {
                options.Protocols = ServerProtocols.Http1AndHttp2;
                options.Tls = ServerTlsOptions.FromPemFiles(cert, privateKey);
            });
            await using var app = builder.Build();
            app.MapGet("/count", async (_, _) => Results.Text((await count()).ToString(System.Globalization.CultureInfo.InvariantCulture)));
#if MANAGED_EDIT
            app.MapGet("/ordinary-edit", static () => Results.Json(AddedModels.Handle(new AddedEntity(9, "ordinary edit")), AddedJson.Default.AddedDto));
#endif
#if GRPC
            // Reuses B22 ApiProbe's bounded protobuf Value codec and real
            // HTTP/2 gRPC wire observation; Tonic owns protocol execution.
            var tiny = new GrpcMarshaller<TinyMessage>(
                static (value, writer) => { var span = writer.GetSpan(2); span[0] = 8; span[1] = value.Value; writer.Advance(2); },
                static bytes => bytes.Length == 0 ? new(0) : bytes.Length == 2 && bytes[0] == 8 && bytes[1] < 128
                    ? new(bytes[1]) : throw new ArgumentException("Invalid bounded Value protobuf"));
            app.MapGrpcUnary(new GrpcMethod<TinyMessage, TinyMessage>("/b24.Probe/Count", GrpcMethodKind.Unary, tiny, tiny),
                async (_, _) => new(checked((byte)await count())), new() { MaxEncodedMessageBytes = 64, MaxDecodedMessageBytes = 64 });
#endif
#if MCP
            app.MapMcp("/mcp", new McpRegistry().AddTool("count", "Actual domain count",
                ConsumerJson.Default.ToolInput, ConsumerJson.Default.ToolOutput,
                async (input, _) => McpToolResult<ToolOutput>.Success(new(input.Text, await count()))));
#endif
            await app.StartAsync();
            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, observed, _, errors) => observed is not null
                    && observed.Thumbprint == certificate.Thumbprint
                    && (errors & SslPolicyErrors.RemoteCertificateNameMismatch) == 0
            };
            using var client = new HttpClient(handler) { BaseAddress = app.BoundAddresses.Single(), Timeout = TimeSpan.FromSeconds(20) };
            foreach (var version in new[] { HttpVersion.Version11, HttpVersion.Version20 })
            {
                using var message = new HttpRequestMessage(HttpMethod.Get, "/count") { Version = version, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
                using var response = await client.SendAsync(message);
                Check("actual-http-" + version + "-tls", response.IsSuccessStatusCode && response.Version == version
                    && response.RequestMessage!.RequestUri!.Scheme == "https" && await response.Content.ReadAsStringAsync() == "5");
            }
#if MANAGED_EDIT
            using (var response = await client.GetAsync("/ordinary-edit"))
            {
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Check("ordinary-added-http-handler", response.IsSuccessStatusCode && body.RootElement.GetProperty("number").GetInt64() == 9
                    && body.RootElement.GetProperty("label").GetString() == "ordinary edit");
            }
#endif
#if GRPC
            using (var message = new HttpRequestMessage(HttpMethod.Post, "/b24.Probe/Count")
            {
                Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Content = new ByteArrayContent([0, 0, 0, 0, 2, 8, 7])
            })
            {
                message.Content.Headers.ContentType = new("application/grpc"); message.Headers.Add("TE", "trailers");
                using var response = await client.SendAsync(message);
                byte[] body = await response.Content.ReadAsByteArrayAsync();
                string? status = response.TrailingHeaders.TryGetValues("grpc-status", out var trailing) ? trailing.Single()
                    : response.Headers.TryGetValues("grpc-status", out var initial) ? initial.Single() : null;
                Check("actual-grpc-http2-terminal-status-and-result", response.IsSuccessStatusCode && response.Version == HttpVersion.Version20
                    && status == "0" && body.Length == 7 && body[0] == 0
                    && BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(1)) == 2 && body[5] == 8 && body[6] == 5);
            }
#endif
#if MCP
            const string json = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"count\",\"arguments\":{\"text\":\"bounded\"},\"_meta\":{\"io.modelcontextprotocol/protocolVersion\":\"2026-07-28\",\"io.modelcontextprotocol/clientCapabilities\":{}}}}";
            using (var message = new HttpRequestMessage(HttpMethod.Post, "/mcp")
            {
                Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            })
            {
                message.Headers.Add("Accept", "application/json, text/event-stream");
                message.Headers.Add("MCP-Protocol-Version", "2026-07-28");
                message.Headers.Add("Mcp-Method", "tools/call"); message.Headers.Add("Mcp-Name", "count");
                using var response = await client.SendAsync(message);
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var result = body.RootElement.GetProperty("result").GetProperty("structuredContent");
                Check("actual-mcp-typed-result", response.IsSuccessStatusCode && !body.RootElement.TryGetProperty("error", out _)
                    && result.GetProperty("count").GetInt64() == 5 && result.GetProperty("text").GetString() == "bounded");
            }
#endif
            var closed = await app.CloseAsync(TimeSpan.FromSeconds(5));
            Check("server-owners-retired", closed.Status == ServerCloseStatus.Completed && closed.PendingRequests == 0
                && closed.RetainedManagedBytes == 0 && closed.RetainedNativeBytes == 0);
        }
        finally { Directory.Delete(tls, recursive: true); }
    }
#endif
}

internal sealed record ConsumerReceipt(int SchemaVersion, string Task, string Provider, bool DynamicCodeSupported, string[] Checks);
internal sealed record ToolInput(string Text);
internal sealed record ToolOutput(string Text, long Count);
internal sealed record TinyMessage(byte Value);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ConsumerReceipt)), JsonSerializable(typeof(ToolInput)), JsonSerializable(typeof(ToolOutput))]
internal partial class ConsumerJson : JsonSerializerContext;

#if SQLITE || POSTGRES
internal sealed record Item(long Id, string Name);
internal sealed class DatabaseFixture(DataSource source) : IAsyncDisposable
{
    internal DataSource Source { get; } = source;
    private readonly string _name = "nw_b24_" + Guid.NewGuid().ToString("N");
    private int _disposed;
    internal static async Task<DatabaseFixture> Create(string provider)
    {
        string location = Environment.GetEnvironmentVariable("B24_DATABASE") ?? throw new InvalidOperationException("Explicit B24_DATABASE required.");
        DataSourceBuilder builder;
        switch (provider)
        {
#if SQLITE
            case "sqlite": builder = new DataSourceBuilder().UseSqlite(location, options => options.BeginMode = SqliteBeginMode.Immediate); break;
#endif
#if POSTGRES
            case "postgresql": builder = new DataSourceBuilder().UsePostgreSql(location, options => options.TlsMode = PostgreSqlTlsMode.DisableForLocalDevelopment); break;
#endif
            default: throw new ArgumentException("Provider is not part of this independent package graph.");
        }
        var result = new DatabaseFixture(builder.ConfigureLimits(new() { MaxConnections = 2, MaxBufferedRows = 32 }).Build());
        try
        {
            await using var session = await result.Source.OpenSessionAsync();
            await session.ExecuteAsync(SqlCommand.Create("CREATE TABLE " + result._name + "(id BIGINT PRIMARY KEY,name TEXT NOT NULL)", kind: SqlCommandKind.Mutation));
            var table = new TableBuilder<Item>(result._name);
            var id = table.Column("id", Codecs.Int64, new() { IsPrimaryKey = true });
            var name = table.Column("name", Codecs.String);
            var metadata = table.Build(row => new(row.Get(id), row.Get(name)), (row, value) => { row.Set(id, value.Id); row.Set(name, value.Name); });
            for (long i = 1; i <= 5; i++) await session.InsertAsync(metadata, new Item(i, "typed " + i));
            var read = await session.FindAsync(metadata, metadata.Key(id, 3L));
            Program.Check("typed-entity-materialization", read == new Item(3, "typed 3"));
            return result;
        }
        catch { await result.DisposeAsync(); throw; }
    }
    internal async Task<long> Count()
    {
        await using var session = await Source.OpenSessionAsync();
        return await session.ScalarAsync(SqlCommand.Create("SELECT COUNT(*) FROM " + _name), Codecs.Int64);
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            await using var session = await Source.OpenSessionAsync();
            await session.ExecuteAsync(SqlCommand.Create("DROP TABLE IF EXISTS " + _name, kind: SqlCommandKind.Mutation));
        }
        finally
        {
            var closed = await Source.CloseAsync(TimeSpan.FromSeconds(5));
            await Source.DisposeAsync();
            Program.Check("database-local-owners-retired", closed.LocalResourcesReleased);
        }
    }
}
#endif
