using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace NativeWeave.Server;

/// <summary>Executes one HTTP result inside the active request lifetime without owning transport control.</summary>
/// <remarks>Single output writer; static codecs; direct Pipelines/static-file/range policies outside initial profile. Final status200..599, explicit HEAD/204/205/304 suppression/framing; LIFO failure-isolated cleanup and execution-admission stream ownership. Bounded metadata-only native queue wait followed by nonwaiting request/global payload grant and rollback before copy; serialized output chunks; results delay I/O until execution; streams/enumerables single-use; metadata callbacks bounded. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.</remarks>
public abstract class ServerResult
{
    /// <summary>Allows application-defined typed result composition.</summary>
    protected ServerResult() { }
    /// <summary>Writes a result using request-owned output; the dispatcher joins unfinished completion.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.ServerResult" path="/member/remarks" />
    /// <param name="context">The current borrowed request context; it cannot escape awaited dispatch and cleanup.</param>
    /// <param name="cancellationToken">Caller cancellation intent; cancellation never releases native owners before their retirement join.</param>
    /// <returns>An awaitable operation; caller completion is distinct from safe native retirement as specified by the declaring type.</returns>
    public abstract Task ExecuteAsync(RequestContext context, CancellationToken cancellationToken = default);
}

/// <summary>Creates HTTP results with explicit static serialization and stream ownership.</summary>
/// <remarks>Single output writer; static codecs; direct Pipelines/static-file/range policies outside initial profile. Final status200..599, explicit HEAD/204/205/304 suppression/framing; LIFO failure-isolated cleanup and execution-admission stream ownership. Bounded metadata-only native queue wait followed by nonwaiting request/global payload grant and rollback before copy; serialized output chunks; results delay I/O until execution; streams/enumerables single-use; metadata callbacks bounded. This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.</remarks>
public static class Results
{
    /// <summary>Creates reusable immutable text encoded in bounded UTF-8 chunks at execution.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Results" path="/member/remarks" />
    /// <param name="text">The text value described by this operation.</param>
    /// <param name="contentType">The contentType value described by this operation; omitted calls use &quot;text/plain; charset=utf-8&quot;.</param>
    /// <param name="statusCode">A final HTTP status in 200 through 599; bodyless status restrictions apply.</param>
    /// <returns>The typed result described in the summary, retaining the ownership policy of its declaring type.</returns>
    public static ServerResult Text(string text, string contentType = "text/plain; charset=utf-8", int statusCode = 200)
    { ArgumentNullException.ThrowIfNull(text); HeaderCollection.ValidateValue(contentType); ServerValidation.ContentStatus(statusCode); return new TextResult(text, contentType, statusCode); }
    /// <summary>Creates an empty final result with status 200..599.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Results" path="/member/remarks" />
    /// <param name="statusCode">A final HTTP status in 200 through 599; bodyless status restrictions apply.</param>
    /// <returns>The typed result described in the summary, retaining the ownership policy of its declaring type.</returns>
    public static ServerResult StatusCode(int statusCode) { ServerValidation.Status(statusCode); return new EmptyResult(statusCode); }
    /// <summary>Creates an empty 204 result.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Results" path="/member/remarks" />
    /// <returns>The typed result described in the summary, retaining the ownership policy of its declaring type.</returns>
    public static ServerResult NoContent() => StatusCode(204);
    /// <summary>Creates an empty 404 result.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Results" path="/member/remarks" />
    /// <returns>The typed result described in the summary, retaining the ownership policy of its declaring type.</returns>
    public static ServerResult NotFound() => StatusCode(404);
    /// <summary>Creates single-execution JSON with explicit static metadata and borrowed DTO state.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Results" path="/member/remarks" />
    /// <param name="value">The value value described by this operation.</param>
    /// <param name="typeInfo">Static JSON metadata; nullable registration arguments resolve from UseJson contexts at route freeze, without reflection fallback.</param>
    /// <param name="statusCode">A final HTTP status in 200 through 599; bodyless status restrictions apply.</param>
    /// <typeparam name="T">The statically known result type; JSON paths require explicit or registered static metadata.</typeparam>
    /// <returns>The typed result described in the summary, retaining the ownership policy of its declaring type.</returns>
    public static ServerResult Json<T>(T value, JsonTypeInfo<T> typeInfo, int statusCode = 200)
    { ArgumentNullException.ThrowIfNull(typeInfo); ServerValidation.ContentStatus(statusCode); return new JsonResult<T>(value, typeInfo, statusCode, null); }
    /// <summary>Creates a 201 static JSON result with a validated Location.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Results" path="/member/remarks" />
    /// <param name="location">The location value described by this operation.</param>
    /// <param name="value">The value value described by this operation.</param>
    /// <param name="typeInfo">Static JSON metadata; nullable registration arguments resolve from UseJson contexts at route freeze, without reflection fallback.</param>
    /// <typeparam name="T">The statically known result type; JSON paths require explicit or registered static metadata.</typeparam>
    /// <returns>The typed result described in the summary, retaining the ownership policy of its declaring type.</returns>
    public static ServerResult Created<T>(string location, T value, JsonTypeInfo<T> typeInfo)
    { ValidateLocation(location); ArgumentNullException.ThrowIfNull(typeInfo); return new JsonResult<T>(value, typeInfo, 201, location); }
    /// <summary>Creates a bounded problem response using framework-generated metadata.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Results" path="/member/remarks" />
    /// <param name="statusCode">A final HTTP status in 200 through 599; bodyless status restrictions apply.</param>
    /// <param name="title">The title value described by this operation; omitted calls use null.</param>
    /// <param name="detail">The detail value described by this operation; omitted calls use null.</param>
    /// <param name="type">The type value described by this operation; omitted calls use null.</param>
    /// <param name="instance">The instance value described by this operation; omitted calls use null.</param>
    /// <returns>The typed result described in the summary, retaining the ownership policy of its declaring type.</returns>
    public static ServerResult Problem(int statusCode = 500, string? title = null, string? detail = null, string? type = null, string? instance = null)
    {
        ServerValidation.ContentStatus(statusCode);
        foreach (var text in new[] { title, detail, type, instance }) if (text is not null && Encoding.UTF8.GetByteCount(text) > 4096) throw new ArgumentException("Problem text is limited to 4096 UTF-8 bytes per field.");
        return new ProblemResult(new(statusCode, title, detail, type, instance, null));
    }
    /// <summary>Creates a validation problem with at most 128 fields and 16 bounded messages per field.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Results" path="/member/remarks" />
    /// <param name="errors">The errors value described by this operation.</param>
    /// <returns>The typed result described in the summary, retaining the ownership policy of its declaring type.</returns>
    public static ServerResult ValidationProblem(IReadOnlyDictionary<string, string[]> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (errors.Count > 128) throw new ArgumentOutOfRangeException(nameof(errors));
        var copy = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var pair in errors)
        {
            ServerValidation.Text(pair.Key, 256, nameof(errors)); ArgumentNullException.ThrowIfNull(pair.Value);
            if (pair.Value.Length > 16 || pair.Value.Any(m => m is null || m.Length > 1024)) throw new ArgumentException("Validation messages exceed their finite limit.", nameof(errors));
            copy.Add(pair.Key, [.. pair.Value]);
        }
        return new ProblemResult(new(400, "Validation failed", null, null, null, copy));
    }
    /// <summary>Creates a validated redirect; the application explicitly chooses external destinations.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Results" path="/member/remarks" />
    /// <param name="location">The location value described by this operation.</param>
    /// <param name="permanent">The permanent value described by this operation; omitted calls use false.</param>
    /// <param name="preserveMethod">The preserveMethod value described by this operation; omitted calls use false.</param>
    /// <returns>The typed result described in the summary, retaining the ownership policy of its declaring type.</returns>
    public static ServerResult Redirect(string location, bool permanent = false, bool preserveMethod = false)
    { ValidateLocation(location); return new RedirectResult(location, preserveMethod ? permanent ? 308 : 307 : permanent ? 301 : 302); }
    /// <summary>Copies at most 1 MiB into a reusable immutable result.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Results" path="/member/remarks" />
    /// <param name="bytes">The payload or finite byte limit described in the summary; application-owned input is not an unbounded framework allocation.</param>
    /// <param name="contentType">The contentType value described by this operation; omitted calls use &quot;application/octet-stream&quot;.</param>
    /// <returns>The typed result described in the summary, retaining the ownership policy of its declaring type.</returns>
    public static ServerResult Bytes(ReadOnlySpan<byte> bytes, string contentType = "application/octet-stream")
    { HeaderCollection.ValidateValue(contentType); if (bytes.Length > 1048576) throw new ArgumentOutOfRangeException(nameof(bytes)); return new BytesResult(bytes.ToArray(), contentType); }
    /// <summary>Executes a producer once inside request lifetime with a borrowed asynchronous output stream.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Results" path="/member/remarks" />
    /// <param name="write">The write value described by this operation.</param>
    /// <param name="contentType">The contentType value described by this operation; omitted calls use &quot;application/octet-stream&quot;.</param>
    /// <returns>The typed result described in the summary, retaining the ownership policy of its declaring type.</returns>
    public static ServerResult Stream(Func<Stream, CancellationToken, Task> write, string contentType = "application/octet-stream")
    { ArgumentNullException.ThrowIfNull(write); HeaderCollection.ValidateValue(contentType); return new ProducerResult(write, contentType); }
    /// <summary>Transfers source disposal only after execution admission unless leaveOpen; an unused result transfers nothing.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Results" path="/member/remarks" />
    /// <param name="source">The source stream; successful result execution admission transfers disposal responsibility.</param>
    /// <param name="contentType">The contentType value described by this operation; omitted calls use &quot;application/octet-stream&quot;.</param>
    /// <param name="leaveOpen">The leaveOpen value described by this operation; omitted calls use false.</param>
    /// <returns>The typed result described in the summary, retaining the ownership policy of its declaring type.</returns>
    public static ServerResult Stream(Stream source, string contentType = "application/octet-stream", bool leaveOpen = false)
    { ArgumentNullException.ThrowIfNull(source); HeaderCollection.ValidateValue(contentType); if (!source.CanRead) throw new ArgumentException("The source must be readable.", nameof(source)); return new StreamResult(source, contentType, leaveOpen); }
    /// <summary>Streams a statically serialized JSON array with one element in flight.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Results" path="/member/remarks" />
    /// <param name="source">The source stream; successful result execution admission transfers disposal responsibility.</param>
    /// <param name="typeInfo">Static JSON metadata; nullable registration arguments resolve from UseJson contexts at route freeze, without reflection fallback.</param>
    /// <typeparam name="T">The statically known result type; JSON paths require explicit or registered static metadata.</typeparam>
    /// <returns>The typed result described in the summary, retaining the ownership policy of its declaring type.</returns>
    public static ServerResult JsonStream<T>(IAsyncEnumerable<T> source, JsonTypeInfo<T> typeInfo)
    { ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(typeInfo); return new JsonStreamResult<T>(source, typeInfo); }
    /// <summary>Streams bounded SSE through native Axum formatting and acknowledges each event before advancing.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Results" path="/member/remarks" />
    /// <param name="source">The source stream; successful result execution admission transfers disposal responsibility.</param>
    /// <returns>The typed result described in the summary, retaining the ownership policy of its declaring type.</returns>
    public static ServerResult ServerSentEvents(IAsyncEnumerable<ServerEvent> source)
    { ArgumentNullException.ThrowIfNull(source); return new SseResult(source); }
    private static void ValidateLocation(string value)
    { ArgumentException.ThrowIfNullOrWhiteSpace(value); HeaderCollection.ValidateValue(value); if (Encoding.UTF8.GetByteCount(value) > 4096 || !Uri.TryCreate(value, UriKind.RelativeOrAbsolute, out _)) throw new ArgumentException("Invalid Location.", nameof(value)); }

    private static void Admit(RequestContext context, CancellationToken token)
    { ArgumentNullException.ThrowIfNull(context); context.Http(); token.ThrowIfCancellationRequested(); if (context.Response.Started || context.Response.IsComplete) throw new InvalidOperationException("A terminal result cannot replace an admitted response."); }
    private abstract class SingleResult : ServerResult
    {
        private int _used;
        protected void Claim(RequestContext context, CancellationToken token)
        { Admit(context, token); if (Interlocked.CompareExchange(ref _used, 1, 0) != 0) throw new InvalidOperationException("This result can execute only once."); }
    }
    private sealed class EmptyResult(int status) : ServerResult
    {
        public override Task ExecuteAsync(RequestContext context, CancellationToken cancellationToken = default)
        { Admit(context, cancellationToken); context.Response.StatusCode = status; return context.Response.CompleteAsync(cancellationToken); }
    }
    private sealed class RedirectResult(string location, int status) : ServerResult
    {
        public override Task ExecuteAsync(RequestContext context, CancellationToken cancellationToken = default)
        { Admit(context, cancellationToken); context.Response.StatusCode = status; context.Response.Headers.Set("Location", location); return context.Response.CompleteAsync(cancellationToken); }
    }
    private sealed class TextResult(string text, string contentType, int status) : ServerResult
    {
        public override Task ExecuteAsync(RequestContext context, CancellationToken cancellationToken = default)
        {
            Admit(context, cancellationToken); context.Response.StatusCode = status; context.Response.ContentType = contentType;
            context.Response.ContentLength = Encoding.UTF8.GetByteCount(text);
            return ExecuteCoreAsync(context, cancellationToken);
        }
        private async Task ExecuteCoreAsync(RequestContext context, CancellationToken token)
        {
            if (!context.Response.BodySuppressed)
            {
                int capacity = Math.Max(4, context.Native.Body.MaxChunkBytes);
                using var credit = context.Native.Body.AcquireManagedCredit(capacity);
                byte[] bytes = new byte[capacity]; var encoder = Encoding.UTF8.GetEncoder(); int offset = 0;
                while (offset < text.Length)
                {
                    encoder.Convert(text.AsSpan(offset), bytes, true, out int chars, out int count, out _);
                    await context.Response.WriteAsync(bytes.AsMemory(0, count), token).ConfigureAwait(false); offset += chars;
                }
            }
            await context.Response.CompleteAsync(token).ConfigureAwait(false);
        }
    }
    private sealed class BytesResult(byte[] bytes, string contentType) : ServerResult
    {
        public override Task ExecuteAsync(RequestContext context, CancellationToken cancellationToken = default)
        { Admit(context, cancellationToken); context.Response.ContentType = contentType; context.Response.ContentLength = bytes.Length; return ExecuteCoreAsync(context, cancellationToken); }
        private async Task ExecuteCoreAsync(RequestContext context, CancellationToken token)
        { if (!context.Response.BodySuppressed) await context.Response.WriteAsync(bytes, token).ConfigureAwait(false); await context.Response.CompleteAsync(token).ConfigureAwait(false); }
    }
    private sealed class JsonResult<T>(T value, JsonTypeInfo<T> info, int status, string? location) : SingleResult
    {
        public override Task ExecuteAsync(RequestContext context, CancellationToken cancellationToken = default)
        { Claim(context, cancellationToken); context.Response.StatusCode = status; if (location is not null) context.Response.Headers.Set("Location", location); return ExecuteCoreAsync(context, cancellationToken); }
        private async Task ExecuteCoreAsync(RequestContext context, CancellationToken token)
        { await context.Response.WriteJsonAsync(value, info, token).ConfigureAwait(false); await context.Response.CompleteAsync(token).ConfigureAwait(false); }
    }
    private sealed class ProblemResult(ProblemDocument value) : ServerResult
    {
        public override Task ExecuteAsync(RequestContext context, CancellationToken cancellationToken = default)
        { Admit(context, cancellationToken); context.Response.StatusCode = value.Status; context.Response.ContentType = "application/problem+json"; return ExecuteCoreAsync(context, cancellationToken); }
        private async Task ExecuteCoreAsync(RequestContext context, CancellationToken token)
        { await context.Response.WriteJsonAsync(value, FrameworkJsonContext.Default.ProblemDocument, token).ConfigureAwait(false); await context.Response.CompleteAsync(token).ConfigureAwait(false); }
    }
    private sealed class ProducerResult(Func<Stream, CancellationToken, Task> producer, string contentType) : SingleResult
    {
        public override Task ExecuteAsync(RequestContext context, CancellationToken cancellationToken = default)
        { Claim(context, cancellationToken); context.Response.ContentType = contentType; return ExecuteCoreAsync(context, cancellationToken); }
        private async Task ExecuteCoreAsync(RequestContext context, CancellationToken token)
        { if (!context.Response.BodySuppressed) await producer(context.Response.Body, token).ConfigureAwait(false); await context.Response.CompleteAsync(token).ConfigureAwait(false); }
    }
    private sealed class StreamResult(Stream source, string contentType, bool leaveOpen) : SingleResult
    {
        public override Task ExecuteAsync(RequestContext context, CancellationToken cancellationToken = default)
        { Claim(context, cancellationToken); return ExecuteCoreAsync(context, cancellationToken); }
        private async Task ExecuteCoreAsync(RequestContext context, CancellationToken token)
        {
            try
            {
                context.Response.ContentType = contentType;
                if (!context.Response.BodySuppressed)
                {
                    int capacity = context.Native.Body.MaxChunkBytes;
                    using var credit = context.Native.Body.AcquireManagedCredit(capacity);
                    byte[] bytes = new byte[capacity]; int count;
                    while ((count = await source.ReadAsync(bytes, token).ConfigureAwait(false)) != 0)
                        await context.Response.WriteAsync(bytes.AsMemory(0, count), token).ConfigureAwait(false);
                }
                await context.Response.CompleteAsync(token).ConfigureAwait(false);
            }
            finally { if (!leaveOpen) await source.DisposeAsync().ConfigureAwait(false); }
        }
    }
    private sealed class JsonStreamResult<T>(IAsyncEnumerable<T> source, JsonTypeInfo<T> info) : SingleResult
    {
        public override Task ExecuteAsync(RequestContext context, CancellationToken cancellationToken = default)
        { Claim(context, cancellationToken); context.Response.ContentType = "application/json"; return ExecuteCoreAsync(context, cancellationToken); }
        private async Task ExecuteCoreAsync(RequestContext context, CancellationToken token)
        {
            if (!context.Response.BodySuppressed)
            {
                await context.Response.WriteAsync("["u8.ToArray(), token).ConfigureAwait(false); bool first = true;
                await foreach (var value in source.WithCancellation(token).ConfigureAwait(false))
                {
                    int maximum = context.Route.EffectiveMaxJsonBytes;
                    using var credit = context.Native.Body.AcquireManagedCredit(checked(maximum * 3));
                    using var buffer = new BoundedJsonBuffer(maximum);
                    using (var writer = new Utf8JsonWriter(buffer)) JsonSerializer.Serialize(writer, value, info);
                    if (!first) await context.Response.WriteAsync(","u8.ToArray(), token).ConfigureAwait(false);
                    first = false; await context.Response.WriteAsync(buffer.WrittenMemory, token).ConfigureAwait(false);
                }
                await context.Response.WriteAsync("]"u8.ToArray(), token).ConfigureAwait(false);
            }
            await context.Response.CompleteAsync(token).ConfigureAwait(false);
        }
    }
    private sealed class SseResult(IAsyncEnumerable<ServerEvent> source) : SingleResult
    {
        public override Task ExecuteAsync(RequestContext context, CancellationToken cancellationToken = default)
        { Claim(context, cancellationToken); context.Response.ContentType = "text/event-stream"; return ExecuteCoreAsync(context, cancellationToken); }
        private async Task ExecuteCoreAsync(RequestContext context, CancellationToken token)
        {
            await context.Response.StartAsync(token).ConfigureAwait(false);
            if (!context.Response.BodySuppressed)
                await foreach (var value in source.WithCancellation(token).ConfigureAwait(false))
                {
                    ArgumentNullException.ThrowIfNull(value); ArgumentNullException.ThrowIfNull(value.Data);
                    if (value.Event?.IndexOfAny(['\r', '\n']) >= 0 || value.Id?.IndexOfAny(['\r', '\n', '\0']) >= 0 ||
                        value.Retry is { } retry && (retry < TimeSpan.Zero || retry > TimeSpan.FromDays(1) || retry.Ticks % TimeSpan.TicksPerMillisecond != 0))
                        throw new ArgumentException("Invalid SSE metadata.", nameof(value));
                    int peak = checked(6 * Encoding.UTF8.GetByteCount(value.Data) + Encoding.UTF8.GetByteCount(value.Event ?? "") + Encoding.UTF8.GetByteCount(value.Id ?? "") + 128);
                    if (peak > context.Native.Body.MaxChunkBytes) throw new ServerException("resource_limit", "The SSE event exceeds its bounded native chunk credit.");
                    await context.Native.WriteServerEventAsync(value, token).ConfigureAwait(false);
                }
            await context.Response.CompleteAsync(token).ConfigureAwait(false);
        }
    }
}

internal sealed record ProblemDocument(
    [property: JsonPropertyName("status")] int Status,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("detail")] string? Detail,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("instance")] string? Instance,
    [property: JsonPropertyName("errors")] Dictionary<string, string[]>? Errors);
[JsonSerializable(typeof(ProblemDocument))]
internal partial class FrameworkJsonContext : JsonSerializerContext;
