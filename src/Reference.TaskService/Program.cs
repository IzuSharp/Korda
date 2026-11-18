using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using NativeWeave.Data;
using NativeWeave.Server;

namespace NativeWeave.Reference.TaskService;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] == "--golden-vectors") { Canonical.WriteGoldenVectors(Console.OpenStandardOutput()); return 0; }
            string? configPath = null; string mode = "serve";
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--config" && i + 1 < args.Length) configPath = args[++i];
                else if (args[i] is "--migrate-only" or "--outbox-once" or "--maintenance-once" && mode == "serve") mode = args[i];
                else throw new ArgumentException("Usage: --config <file> [--migrate-only|--outbox-once|--maintenance-once], or --golden-vectors.");
            }
            if (configPath is null || new FileInfo(configPath).Length > 65_536) throw new ArgumentException("A bounded configuration file is required.");
            var configuration = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(configPath).ConfigureAwait(false), AppJson.Default.ServiceConfiguration)
                ?? throw new ArgumentException("A configuration object is required.");
            configuration.Validate();
            await using var database = configuration.CreateSource();
            await using var consumer = configuration.CreateConsumer();
            // Starting the executable is an explicit application deployment command, not data-source automatic migration.
            await TaskService.MigrateAsync(database, configuration.Provider).ConfigureAwait(false);
            await TaskService.MigrateAsync(consumer, "consumer").ConfigureAwait(false);
            if (mode == "--migrate-only") { Console.WriteLine("MIGRATED"); return 0; }
            OutboxWorker outbox = new(database, consumer);
            if (mode == "--outbox-once")
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                Console.WriteLine("{\"delivered\":" + await outbox.RunOnceAsync(timeout.Token).ConfigureAwait(false) + "}"); return 0;
            }
            using BlobStore blobs = new(configuration);
            TaskService service = new(database, new(Convert.FromBase64String(configuration.CursorKey)), blobs);
            if (mode == "--maintenance-once")
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                Console.WriteLine(JsonSerializer.Serialize(await service.MaintainAsync(timeout.Token).ConfigureAwait(false), AppJson.Default.MaintenanceReport)); return 0;
            }
            string serverState = await ServeAsync(configuration, service, outbox, blobs).ConfigureAwait(false);
            await consumer.DisposeAsync().ConfigureAwait(false);
            await database.DisposeAsync().ConfigureAwait(false);
            Console.WriteLine("CLOSED " + JsonSerializer.Serialize(new ShutdownReport(serverState, database.Cleanup.Status.ToString(),
                database.Cleanup.LocalResourcesReleased, consumer.Cleanup.Status.ToString(), consumer.Cleanup.LocalResourcesReleased), AppJson.Default.ShutdownReport));
            return 0;
        }
        catch (Exception error)
        {
            // Connection strings, bearer credentials and persisted request bytes never enter default diagnostics.
            Console.Error.WriteLine("reference_service_failed type=" + error.GetType().Name);
            if (error is ArgumentException) Console.Error.WriteLine("Check the explicit configuration and command syntax in the reference guide.");
            return 1;
        }
    }

    private static async Task<string> ServeAsync(ServiceConfiguration configuration, TaskService service, OutboxWorker outbox, BlobStore blobs)
    {
        var auth = new Authentication(configuration.Principals);
        var builder = NativeServer.CreateBuilder().UseJson(AppJson.Default).UseAuthentication(auth).UseAuthorization(auth);
        builder.Options.RequestTimeout = TimeSpan.FromSeconds(configuration.RequestTimeoutSeconds);
        builder.Listen(IPAddress.Parse(configuration.ListenAddress), configuration.Port, options =>
        {
            options.Name = "reference";
            if (configuration.TlsCertificatePem is not null)
            {
                options.Protocols = ServerProtocols.Http1AndHttp2;
                options.Tls = ServerTlsOptions.FromPemFiles(configuration.TlsCertificatePem, configuration.TlsPrivateKeyPem!);
            }
            else options.Protocols = ServerProtocols.Http1;
        });
        await using var application = builder.Build();
        application.Use(async (context, next, token) =>
        {
            try { await next(context, token).ConfigureAwait(false); }
            catch (ApplicationProblem error) { await ProblemAsync(context, error.Status, error.Code, token).ConfigureAwait(false); }
            catch (ConcurrencyConflictException) { await ProblemAsync(context, 412, "version_conflict", token).ConfigureAwait(false); }
            catch (JsonException) { await ProblemAsync(context, 400, "invalid_json", token).ConfigureAwait(false); }
        });
        application.MapPost<CreateItem>("/v1/items", async (input, context, token) =>
            new ReplayResult(await service.CreateAsync(Authentication.Identity(context), Canonical.Key(context.Request.Headers.GetSingle("Idempotency-Key")), input, token).ConfigureAwait(false)))
            .RequireAuthorization("write").WithName("items.create").WithMaxRequestBodyBytes(1_048_576).WithMaxJsonBytes(1_048_576);
        application.MapGet("/v1/items", async (context, token) =>
        {
            var limits = context.Request.Query.GetValues("limit");
            int limit = limits.Count == 0 ? 100 : context.Request.Query.Get<int>("limit");
            var afters = context.Request.Query.GetValues("after");
            string? after = afters.Count == 0 ? null : context.Request.Query.Get<string>("after");
            return Results.Json(await service.ListAsync(Authentication.Identity(context), limit, after, token).ConfigureAwait(false), AppJson.Default.ItemPage);
        }).RequireAuthorization("read").WithName("items.list");
        application.MapGet("/v1/operations/{id}", async (context, token) =>
            new ReplayResult(await service.RecoverAsync(Authentication.Identity(context), context.Request.RouteValues.Get<string>("id"), token).ConfigureAwait(false)))
            .RequireAuthorization("read").WithName("operations.recover");
        application.MapPut("/v1/items/{id}/attachment", async (context, token) =>
        {
            var identity = Authentication.Identity(context);
            string itemId = Canonical.Id(context.Request.RouteValues.Get<string>("id"));
            string key = Canonical.Key(context.Request.Headers.GetSingle("Idempotency-Key"));
            string? expected = context.Request.Headers.GetSingle("If-Match");
            if (expected is null || !long.TryParse(expected.Trim('"'), NumberStyles.None, CultureInfo.InvariantCulture, out long version) || version is < 1 or long.MaxValue)
                throw new ApplicationProblem(400, "invalid_expected_version");
            long length = context.Request.ContentLength ?? throw new ApplicationProblem(411, "content_length_required");
            if (context.Request.Headers.GetSingle("Content-Encoding") is { } encoding && !encoding.Equals("identity", StringComparison.OrdinalIgnoreCase))
                throw new ApplicationProblem(415, "unsupported_content_encoding");
            _ = await service.AuthorizeItemAsync(identity, itemId, token).ConfigureAwait(false);
            // No DB session survives while the client is uploading.
            await using var upload = blobs.Begin(identity, itemId, key, version, length);
            await upload.StageAsync(context.Request.Body, token).ConfigureAwait(false);
            var result = await service.BindAttachmentAsync(identity, key, itemId, version, upload, token).ConfigureAwait(false);
            upload.Complete(result);
            return new ReplayResult(result);
        }).RequireAuthorization("write").WithName("attachments.put").WithMaxRequestBodyBytes(BlobStore.MaximumAttachment);
        application.MapGet("/v1/items/{id}/attachment", async (context, token) =>
        {
            var attachment = await service.AttachmentAsync(Authentication.Identity(context), context.Request.RouteValues.Get<string>("id"), token).ConfigureAwait(false);
            context.Response.ContentLength = attachment.ContentLength;
            context.Response.Headers.Set("ETag", "\"" + attachment.Version.ToString(CultureInfo.InvariantCulture) + "\"");
            context.Response.Headers.Set("Content-SHA256", attachment.ContentHash);
            return Results.Stream(blobs.Open(attachment));
        }).RequireAuthorization("read").WithName("attachments.get");

        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, value) => { value.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        using var signal = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, value => { value.Cancel = true; stop.Cancel(); });
        Task? worker = null;
        try
        {
            await application.StartAsync(stop.Token).ConfigureAwait(false);
            Console.WriteLine("READY " + application.BoundAddresses.Single());
            if (configuration.RunWorkers) worker = WorkAsync(outbox, stop.Token);
            try
            {
                Task wait = Task.Delay(Timeout.InfiniteTimeSpan, stop.Token);
                if (worker is not null) await (await Task.WhenAny(wait, worker).ConfigureAwait(false)).ConfigureAwait(false);
                else await wait.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
        finally
        {
            Console.CancelKeyPress -= cancel; stop.Cancel();
            if (worker is not null) await worker.ConfigureAwait(false);
            var result = await application.CloseAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Console.WriteLine("SERVER_CLOSE status=" + result.Status);
            // Top-level process shutdown may join; individual HTTP handlers use finite session closure only.
        }
        await application.DisposeAsync().ConfigureAwait(false);
        return application.State.ToString();
    }

    private static async Task WorkAsync(OutboxWorker outbox, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await outbox.RunOnceAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (DatabaseException error) { Console.Error.WriteLine("outbox_cycle_unconfirmed kind=" + error.Kind); }
            try { await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
        }
    }

    private static async Task ProblemAsync(RequestContext context, int status, string code, CancellationToken token)
    {
        if (context.Response.HasStarted) { context.Abort(); return; }
        context.Response.StatusCode = status;
        await context.Response.WriteJsonAsync(new Problem(code), AppJson.Default.Problem, token).ConfigureAwait(false);
    }

    private sealed class ReplayResult(Replay replay) : ServerResult
    {
        public override async Task ExecuteAsync(RequestContext context, CancellationToken cancellationToken = default)
        {
            context.Response.StatusCode = replay.Status;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength = replay.Bytes.Length;
            context.Response.Headers.Set("Idempotency-Retention-Seconds", "604800");
            context.Response.Headers.Set("Location", "/v1/operations/" + replay.OperationId);
            if (replay.Status == 503) context.Response.Headers.Set("Retry-After", "1");
            await context.Response.WriteAsync(replay.Bytes, cancellationToken).ConfigureAwait(false);
        }
    }
}
