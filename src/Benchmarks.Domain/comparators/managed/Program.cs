using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Data.Sqlite;

namespace NativeWeave.Reference.TaskService;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.Error.WriteLine("B23_RUNTIME dynamic=" + RuntimeFeature.IsDynamicCodeSupported);
        try
        {
            if (args is ["--golden-vectors"]) { Canonical.WriteGoldenVectors(Console.OpenStandardOutput()); return 0; }
            if (args.Length is < 2 or > 3 || args[0] != "--config" || args.Length == 3 && args[2] is not ("--migrate-only" or "--outbox-once"))
                throw new ArgumentException("Usage: --config FILE [--migrate-only|--outbox-once], or --golden-vectors.");
            if (new FileInfo(args[1]).Length > 65_536) throw new ArgumentException("Configuration exceeds its bound.");
            var configuration = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(args[1]), AppJson.Default.ServiceConfiguration)
                ?? throw new ArgumentException("Configuration is required.");
            configuration.Validate(); ServiceConfiguration.ConfigureSqlite();
            await using DatabaseStore database = new(configuration); await using DatabaseStore consumer = new(configuration, true);
            await database.MigrateAsync(configuration.Provider, default); await consumer.MigrateAsync("consumer", default);
            if (args.Length == 3 && args[2] == "--migrate-only") { Console.WriteLine("MIGRATED"); return 0; }
            OutboxWorker outbox = new(database, consumer);
            if (args.Length == 3) { using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5)); Console.WriteLine("{\"delivered\":" + await outbox.RunOnceAsync(timeout.Token) + "}"); return 0; }
            using BlobStore blobs = new(configuration);
            TaskService service = new(database, new(Convert.FromBase64String(configuration.CursorKey)), blobs);
            try { await ServeAsync(configuration, service, outbox, blobs); }
            finally { await Task.WhenAll(database.DisposeAsync().AsTask(), consumer.DisposeAsync().AsTask()); SqliteConnection.ClearAllPools(); }
            if (!database.IsRetired || !consumer.IsRetired) throw new InvalidOperationException("A comparator database owner remains quarantined.");
            Console.WriteLine("CLOSED " + JsonSerializer.Serialize(new ShutdownReport("Stopped", "Succeeded", database.IsRetired, "Succeeded", consumer.IsRetired), AppJson.Default.ShutdownReport));
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("B23_COMPARATOR_FAILED " + error.GetType().Name); return 1; }
    }

    private static async Task ServeAsync(ServiceConfiguration configuration, TaskService service, OutboxWorker outbox, BlobStore blobs)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false; options.Limits.MaxConcurrentConnections = 512; options.Limits.MaxRequestHeadersTotalSize = 32_768;
            options.Limits.MaxRequestHeaderCount = 200; options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(60);
            options.Limits.MaxRequestBodySize = BlobStore.MaximumAttachment; options.Limits.MaxResponseBufferSize = 32_768;
            options.Limits.Http2.MaxStreamsPerConnection = 32;
            options.Limits.Http2.InitialStreamWindowSize = 65_536; options.Limits.Http2.InitialConnectionWindowSize = 65_536;
            options.Limits.Http2.MaxFrameSize = 32_768; options.Limits.Http2.MaxRequestHeaderFieldSize = 32_768;
            options.Listen(IPAddress.Parse(configuration.ListenAddress), configuration.Port, listen =>
            {
                if (configuration.TlsCertificatePem is not null)
                {
                    listen.Protocols = HttpProtocols.Http1AndHttp2;
                    listen.UseHttps(X509Certificate2.CreateFromPemFile(configuration.TlsCertificatePem, configuration.TlsPrivateKeyPem));
                }
                else listen.Protocols = HttpProtocols.Http1;
            });
        });
        await using var app = builder.Build();
        Authentication authentication = new(configuration.Principals);
        using SemaphoreSlim requests = new(256, 256), waiters = new(128, 128);
        app.Run(async context =>
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            deadline.CancelAfter(TimeSpan.FromSeconds(configuration.RequestTimeoutSeconds));
            bool entered = requests.Wait(0);
            try
            {
                if (!entered)
                {
                    if (!waiters.Wait(0)) throw new ApplicationProblem(503, "request_capacity");
                    try { entered = await requests.WaitAsync(TimeSpan.FromSeconds(5), deadline.Token); }
                    finally { waiters.Release(); }
                    if (!entered) throw new ApplicationProblem(503, "request_admission_timeout");
                }
                var identity = authentication.Authenticate(context);
                if (context.Request.Method is "POST" or "PUT" && !identity.CanWrite) throw new ApplicationProblem(403, "forbidden");
                await HandleAsync(context, identity, service, blobs, deadline.Token);
            }
            catch (ApplicationProblem error) { await ProblemAsync(context, error.Status, error.Code); }
            catch (JsonException) { await ProblemAsync(context, 400, "invalid_json"); }
            catch (Microsoft.AspNetCore.Http.BadHttpRequestException error) { await ProblemAsync(context, error.StatusCode, "invalid_request"); }
            catch (OperationCanceledException) { context.Abort(); }
            catch (Exception error) { Console.Error.WriteLine("B23_REQUEST_FAILED " + error.GetType().Name); await ProblemAsync(context, 500, "request_failed"); }
            finally { if (entered) requests.Release(); }
        });
        using var stop = new CancellationTokenSource(); Task? worker = null;
        try
        {
            await app.StartAsync();
            string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            Console.WriteLine("READY " + address);
            if (configuration.RunWorkers) worker = WorkAsync(outbox, stop.Token);
            Task shutdown = app.WaitForShutdownAsync();
            if (worker is not null) await (await Task.WhenAny(shutdown, worker)); else await shutdown;
        }
        finally
        {
            stop.Cancel();
            if (worker is not null) await worker;
            await app.StopAsync();
        }
    }

    private static async Task HandleAsync(HttpContext context, AuthIdentity identity, TaskService service, BlobStore blobs, CancellationToken token)
    {
        string path = context.Request.Path.Value ?? ""; string method = context.Request.Method;
        if (path == "/v1/items" && method == "POST")
        {
            context.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = 1_048_576;
            if (context.Request.ContentLength > 1_048_576) throw new ApplicationProblem(413, "json_limit");
            var input = await JsonSerializer.DeserializeAsync(context.Request.Body, AppJson.Default.CreateItem, token)
                ?? throw new ApplicationProblem(400, "invalid_json");
            await ReplayAsync(context, await service.CreateAsync(identity, Canonical.Key(Header(context, "Idempotency-Key")), input, token), token); return;
        }
        if (path == "/v1/items" && method == "GET")
        {
            string? limitText = Query(context, "limit"), cursor = Query(context, "after");
            if (limitText is not null && !int.TryParse(limitText, NumberStyles.None, CultureInfo.InvariantCulture, out _)) throw new ApplicationProblem(400, "invalid_limit");
            int limit = limitText is null ? 100 : int.Parse(limitText, CultureInfo.InvariantCulture);
            var page = await service.ListAsync(identity, limit, cursor, token);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(page, AppJson.Default.ItemPage);
            context.Response.ContentType = "application/json"; context.Response.ContentLength = bytes.Length;
            await context.Response.Body.WriteAsync(bytes, token); return;
        }
        if (path.StartsWith("/v1/operations/", StringComparison.Ordinal) && method == "GET")
        { await ReplayAsync(context, await service.RecoverAsync(identity, Canonical.Id(path[15..]), token), token); return; }
        if (path.StartsWith("/v1/items/", StringComparison.Ordinal) && path.EndsWith("/attachment", StringComparison.Ordinal))
        {
            string item = Canonical.Id(path[10..^11]);
            if (method == "GET")
            {
                var attachment = await service.AttachmentAsync(identity, item, token);
                context.Response.ContentType = "application/octet-stream"; context.Response.ContentLength = attachment.ContentLength;
                context.Response.Headers.ETag = "\"" + attachment.Version.ToString(CultureInfo.InvariantCulture) + "\"";
                context.Response.Headers["Content-SHA256"] = attachment.ContentHash;
                await using var file = blobs.Open(attachment); await file.CopyToAsync(context.Response.Body, 32_768, token); return;
            }
            if (method == "PUT")
            {
                string key = Canonical.Key(Header(context, "Idempotency-Key")); string? expected = Header(context, "If-Match");
                if (expected is null || !long.TryParse(expected.Trim('"'), NumberStyles.None, CultureInfo.InvariantCulture, out long version) || version is < 1 or long.MaxValue)
                    throw new ApplicationProblem(400, "invalid_expected_version");
                long length = context.Request.ContentLength ?? throw new ApplicationProblem(411, "content_length_required");
                if (Header(context, "Content-Encoding") is { } encoding && !encoding.Equals("identity", StringComparison.OrdinalIgnoreCase)) throw new ApplicationProblem(415, "unsupported_content_encoding");
                await service.AuthorizeItemAsync(identity, item, token);
                await using var upload = blobs.Begin(identity, item, key, version, length);
                await upload.StageAsync(new BoundedReadStream(context.Request.Body), token);
                var result = await service.BindAttachmentAsync(identity, key, item, version, upload, token); upload.Complete(result);
                await ReplayAsync(context, result, token); return;
            }
        }
        throw new ApplicationProblem(404, "not_found");
    }

    private static string? Header(HttpContext context, string name)
    { var values = context.Request.Headers[name]; return values.Count switch { 0 => null, 1 => values[0], _ => throw new ApplicationProblem(400, "ambiguous_header") }; }
    private static string? Query(HttpContext context, string name)
    { var values = context.Request.Query[name]; return values.Count switch { 0 => null, 1 => values[0], _ => throw new ApplicationProblem(400, "ambiguous_query") }; }
    private static async Task ReplayAsync(HttpContext context, Replay replay, CancellationToken token)
    {
        context.Response.StatusCode = replay.Status; context.Response.ContentType = "application/json"; context.Response.ContentLength = replay.Bytes.Length;
        context.Response.Headers["Idempotency-Retention-Seconds"] = "604800"; context.Response.Headers.Location = "/v1/operations/" + replay.OperationId;
        if (replay.Status == 503) context.Response.Headers.RetryAfter = "1";
        await context.Response.Body.WriteAsync(replay.Bytes, token);
    }
    private static async Task ProblemAsync(HttpContext context, int status, string code)
    {
        if (context.Response.HasStarted) { context.Abort(); return; }
        context.Response.StatusCode = status; context.Response.ContentType = "application/json";
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new Problem(code), AppJson.Default.Problem);
        context.Response.ContentLength = bytes.Length; await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
    }
    private static async Task WorkAsync(OutboxWorker worker, CancellationToken token)
    {
        try { while (!token.IsCancellationRequested) { await worker.RunOnceAsync(token); await Task.Delay(1000, token); } }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    // Kestrel may override CopyToAsync using pipe segments. The shared filesystem sink requires
    // an actual 32KiB staging bound, so use Stream's bounded copy loop over this borrowed reader.
    private sealed class BoundedReadStream(Stream input) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => input.ReadAsync(buffer[..Math.Min(buffer.Length, 32_768)], token);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => input.ReadAsync(buffer, offset, Math.Min(count, 32_768), token);
        public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, Math.Min(count, 32_768));
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
