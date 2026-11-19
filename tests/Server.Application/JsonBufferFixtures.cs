using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using NativeWeave.Server;

// These are contract regressions, not allocation or growth-policy assertions.
// The fake transport makes credit and await boundaries deterministic; real
// native ownership, sockets and resource admission remain separate acceptance.
internal sealed class JsonBufferAcceptance
{
    private static readonly TimeSpan JoinLimit = TimeSpan.FromSeconds(10);
    private int _checks;

    internal static async Task<int> RunAsync()
    {
        var tests = new JsonBufferAcceptance();
        tests.BufferWriterBoundaries();
        tests.ConfigurationAdmission();
        await tests.RouteAdmissionAsync();
        await tests.ExactJsonAsync();
        await tests.PrecommitFailuresAsync();
        await tests.PendingWriteAsync();
        await tests.FailedWriteAsync(cancel: false);
        await tests.FailedWriteAsync(cancel: true);
        await tests.StreamItemsAsync(overLimit: false);
        await tests.StreamItemsAsync(overLimit: true);
        return tests._checks;
    }

    private void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("JSON buffer regression: " + message);
        _checks++;
    }

    private void BodyLimit(Action action)
    {
        try { action(); }
        catch (ServerException error) when (error.Code == "body_limit_exceeded") { _checks++; return; }
        throw new InvalidOperationException("Expected body_limit_exceeded.");
    }

    private void BufferWriterBoundaries()
    {
        int[] fragments = [1, 7, 251, 509, 1021, 2053, 4093, 8191, 16381];
        foreach (int maximum in new[] { 256, 5000, 10000, 65539 })
        {
            using var buffer = new BoundedJsonBuffer(maximum);
            byte[] expected = new byte[maximum];
            for (int i = 0; i < expected.Length; i++) expected[i] = (byte)((17 + 131 * i) & 255);
            BodyLimit(() => buffer.GetMemory(-1));
            BodyLimit(() => buffer.GetSpan(maximum + 1));
            BodyLimit(() => buffer.Advance(-1));
            Check(buffer.WrittenMemory.IsEmpty, "rejected calls preserve the empty prefix");
            int offset = 0, fragment = 0;
            while (offset < maximum)
            {
                int count = Math.Min(fragments[fragment % fragments.Length], maximum - offset);
                if ((fragment & 1) == 0)
                {
                    Memory<byte> memory = buffer.GetMemory(count);
                    Check(memory.Length >= count, "GetMemory honors its requested minimum");
                    expected.AsMemory(offset, count).CopyTo(memory);
                }
                else
                {
                    Span<byte> span = buffer.GetSpan(count);
                    Check(span.Length >= count, "GetSpan honors its requested minimum");
                    expected.AsSpan(offset, count).CopyTo(span);
                }
                buffer.Advance(count);
                offset += count; fragment++;
                Check(buffer.WrittenMemory.Length == offset, "Advance commits only the written prefix");
            }
            buffer.Advance(0);
            Check(buffer.WrittenMemory.Span.SequenceEqual(expected), "exact maximum preserves all fragmented bytes");
            BodyLimit(() => buffer.GetMemory());
            BodyLimit(() => buffer.GetSpan(1));
            BodyLimit(() => buffer.Advance(1));
            Check(buffer.WrittenMemory.Span.SequenceEqual(expected), "over-limit calls preserve the committed prefix");
        }
    }

    private void ConfigurationAdmission()
    {
        const int maximum = 65539;
        ServerOptions Options()
        {
            var options = new ServerOptions { MaxJsonBytes = maximum };
            long required = 3L * maximum + options.Budget.MaxChunkBytes;
            options.Budget.ManagedPayloadBytes = required;
            options.Budget.RetainedBytesPerRequest = required;
            return options;
        }
        _ = ServerConfiguration.Snapshot(Options()); _checks++;
        foreach (bool global in new[] { true, false })
        {
            var options = Options();
            if (global) options.Budget.ManagedPayloadBytes--;
            else options.Budget.RetainedBytesPerRequest--;
            bool rejected = false;
            try { _ = ServerConfiguration.Snapshot(options); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "global/request admission retains the JSON peak plus chunk floor");
        }
    }

    private async Task RouteAdmissionAsync()
    {
        const int maximum = 65539;
        foreach (int deficit in new[] { 0, 1 })
        {
            var options = new ServerOptions { MaxJsonBytes = 256 };
            options.Budget.ManagedPayloadBytes = 3L * maximum - deficit;
            options.Budget.RetainedBytesPerRequest = 3L * maximum - deficit;
            var config = new ServerConfiguration(ServerConfiguration.Snapshot(options), [], [JsonBoundaryContext.Default], null, null);
            await using var app = new NativeServerApplication(config, new FakeHost(), new ServiceCollection().BuildServiceProvider(), true);
            app.MapRaw("/", ["GET"], static (_, _) => Task.CompletedTask).WithMaxJsonBytes(maximum);
            bool rejected = false;
            try { await app.StartAsync(); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected == (deficit != 0), "route freeze retains its existing materialization peak floor");
        }
    }

    private async Task ExactJsonAsync()
    {
        foreach (int maximum in new[] { 5000, 10000, 65539 })
            foreach (bool flush in new[] { false, true })
            {
                var value = new JsonBoundaryValue(maximum, flush);
                var request = await RunRequestAsync(maximum, (context, _, token) =>
                    context.Response.WriteJsonAsync(value, JsonBoundaryContext.Default.JsonBoundaryValue, token));
                CheckBody(request, value);
                Check(value.SerializeCalls == 1 && (value.Flushes > 0) == flush, "explicit static converter and requested Flush path execute");
                Check(request.Status == 200 && request.Heads == 1 && request.Finishes == 1 && request.Completions == 1,
                    "exact-limit JSON completes once");
                CheckCredits(request.BodyImpl, maximum, 1);
            }

        // A small result retains the same request admission even when its route
        // permits a much larger document.
        var tiny = new JsonBoundaryValue(16);
        var small = await RunRequestAsync(65539, (context, _, token) =>
            Results.Json(tiny, JsonBoundaryContext.Default.JsonBoundaryValue).ExecuteAsync(context, token));
        CheckBody(small, tiny);
        CheckCredits(small.BodyImpl, 65539, 1);
    }

    private async Task PrecommitFailuresAsync()
    {
        const int maximum = 65539;
        foreach (bool converterFault in new[] { false, true })
        {
            var rejected = new JsonBoundaryValue(converterFault ? maximum : maximum + 1, flush: true,
                throwAfterItem: converterFault ? 6000 : -1);
            var fallback = new JsonBoundaryValue(5000, flush: true);
            bool caught = false;
            var request = await RunRequestAsync(maximum, async (context, native, token) =>
            {
                try { await context.Response.WriteJsonAsync(rejected, JsonBoundaryContext.Default.JsonBoundaryValue, token); }
                catch (Exception error) when (converterFault ? error is JsonException : error is ServerException { Code: "body_limit_exceeded" })
                { caught = true; }
                Check(caught && !context.Response.HasStarted && native.Output.Length == 0, "serialization failure publishes no header or partial body");
                Check(native.BodyImpl.CurrentManaged == 0 && native.BodyImpl.CreditsReleased == 1, "serialization failure returns its credit before retry");
                await context.Response.WriteJsonAsync(fallback, JsonBoundaryContext.Default.JsonBoundaryValue, token);
            });
            Check(caught && (!converterFault || rejected.ItemsWritten == 6000), "expected late converter/encoded-limit failure was observed");
            CheckBody(request, fallback);
            Check(request.Heads == 1 && request.Completions == 1, "precommit failure leaves the same response writable");
            CheckCredits(request.BodyImpl, maximum, 2);
        }

        var value = new JsonBoundaryValue(16);
        bool refused = false;
        var quota = await RunRequestAsync(maximum, async (context, native, token) =>
        {
            try { await context.Response.WriteJsonAsync(value, JsonBoundaryContext.Default.JsonBoundaryValue, token); }
            catch (InvalidOperationException error) when (error.Message == "credit exhausted") { refused = true; }
            Check(refused && value.SerializeCalls == 0 && !context.Response.HasStarted && native.Output.Length == 0,
                "insufficient existing credit refuses before serialization or headers");
            Check(native.BodyImpl.CurrentManaged == 0 && native.BodyImpl.CreditsAcquired == 0, "failed credit acquisition retains no grant");
            native.BodyImpl.ManagedLimit = 3 * maximum;
            await context.Response.WriteJsonAsync(value, JsonBoundaryContext.Default.JsonBoundaryValue, token);
        }, native => native.BodyImpl.ManagedLimit = 3 * maximum - 1);
        CheckBody(quota, value);
        CheckCredits(quota.BodyImpl, maximum, 1, requests: 2);
    }

    private async Task PendingWriteAsync()
    {
        const int maximum = 65539;
        var value = new JsonBoundaryValue(maximum, flush: true);
        byte[] expected = value.ExpectedBytes();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeRequest? observed = null, finished = null;
        ReadOnlyMemory<byte> borrowed = default;
        int writes = 0, completed = 0, disposed = 0;
        Task<FakeRequest> pending = RunRequestAsync(maximum, async (context, _, token) =>
        {
            context.Response.OnCompleted(() => { completed++; return Task.CompletedTask; });
            context.Response.RegisterForDisposeAsync(new DisposeProbe(() => disposed++));
            await context.Response.WriteJsonAsync(value, JsonBoundaryContext.Default.JsonBoundaryValue, token);
        }, native =>
        {
            observed = native; native.BodyImpl.ManagedLimit = 3 * maximum;
            native.BodyImpl.BeforeWrite = (data, token) =>
            {
                if (++writes != 1) return Task.CompletedTask;
                borrowed = data; entered.TrySetResult(); return release.Task.WaitAsync(token);
            };
        });
        try
        {
            await entered.Task.WaitAsync(JoinLimit);
            Check(!pending.IsCompleted && observed!.Completions == 0 && completed == 0 && disposed == 0,
                "pending transport write retains dispatch and response cleanup");
            Check(observed!.BodyImpl.CurrentManaged == 3 * maximum && observed.BodyImpl.CreditsReleased == 0,
                "pending transport write retains the full existing JSON grant");
            Check(!borrowed.IsEmpty && borrowed.Span.SequenceEqual(expected.AsSpan(0, borrowed.Length)),
                "borrowed encoded bytes remain readable while transport completion is delayed");
        }
        finally
        {
            release.TrySetResult();
            finished = await pending.WaitAsync(JoinLimit);
        }
        CheckBody(finished!, value);
        Check(writes > 1 && completed == 1 && disposed == 1 && finished!.Completions == 1,
            "chunked awaited write releases lifecycle resources exactly once");
        CheckCredits(finished!.BodyImpl, maximum, 1);
    }

    private async Task FailedWriteAsync(bool cancel)
    {
        const int maximum = 65539;
        var value = new JsonBoundaryValue(maximum, flush: true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var fault = new IOException("synthetic JSON transport failure");
        FakeRequest? observed = null, finished = null;
        int completed = 0, disposed = 0;
        Task<FakeRequest> pending = RunRequestAsync(maximum, async (context, _, _) =>
        {
            context.Response.OnCompleted(() => { completed++; return Task.CompletedTask; });
            context.Response.RegisterForDisposeAsync(new DisposeProbe(() => disposed++));
            await context.Response.WriteJsonAsync(value, JsonBoundaryContext.Default.JsonBoundaryValue, cancellation.Token);
        }, native =>
        {
            observed = native; native.BodyImpl.ManagedLimit = 3 * maximum;
            native.BodyImpl.BeforeWrite = async (_, token) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                token.ThrowIfCancellationRequested();
                throw fault;
            };
        }, error => cancel ? error is OperationCanceledException canceled && canceled.CancellationToken == cancellation.Token : ReferenceEquals(error, fault));
        try
        {
            await entered.Task.WaitAsync(JoinLimit);
            Check(observed!.BodyImpl.CurrentManaged == 3 * maximum && observed.Completions == 0,
                "failed/canceled write holds credit until its awaited transport resolves");
            if (cancel) cancellation.Cancel();
            else release.TrySetResult();
        }
        finally
        {
            if (cancel) cancellation.Cancel();
            release.TrySetResult();
            finished = await pending.WaitAsync(JoinLimit);
        }
        Check(finished!.Heads == 1 && finished.Output.Length == 0 && finished.Aborts > 0 && finished.Completions == 1,
            "post-header write failure aborts without a second response");
        Check(completed == 1 && disposed == 1, "failed/canceled write still performs exactly one lifecycle cleanup");
        CheckCredits(finished.BodyImpl, maximum, 1);
    }

    private async Task StreamItemsAsync(bool overLimit)
    {
        const int maximum = 5000;
        var first = new JsonBoundaryValue(maximum, flush: true);
        var second = new JsonBoundaryValue(overLimit ? maximum + 1 : maximum, flush: true);
        var source = new TrackedJsonValues([first, second]);
        var request = await RunRequestAsync(maximum, (context, _, token) =>
            Results.JsonStream(source, JsonBoundaryContext.Default.JsonBoundaryValue).ExecuteAsync(context, token),
            native =>
            {
                native.BodyImpl.ManagedLimit = 3 * maximum;
                source.BeforeMoveNext = () => Check(native.BodyImpl.CurrentManaged == 0, "JSON stream returns the preceding item grant before advancing");
            },
            overLimit ? error => error is ServerException { Code: "body_limit_exceeded" } : null);
        string firstJson = Encoding.UTF8.GetString(first.ExpectedBytes());
        string expected = overLimit ? "[" + firstJson : "[" + firstJson + "," + Encoding.UTF8.GetString(second.ExpectedBytes()) + "]";
        Check(request.Output.ToArray().AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(expected)),
            "JSON stream preserves exact per-item bytes and publishes no failed item's prefix");
        Check(source.Disposals == 1 && source.Moves == (overLimit ? 2 : 3), "JSON stream disposes its enumerator on success and rejection");
        Check(request.Heads == 1 && request.Completions == 1 && (overLimit ? request.Aborts > 0 : request.Finishes == 1),
            "JSON stream completes or aborts one admitted response");
        CheckCredits(request.BodyImpl, maximum, 2);
    }

    private void CheckBody(FakeRequest request, JsonBoundaryValue value)
    {
        byte[] actual = request.Output.ToArray();
        Check(actual.Length == value.TotalBytes && actual.AsSpan().SequenceEqual(value.ExpectedBytes()), "public static JSON result matches every expected UTF-8 byte");
        using var parsed = JsonDocument.Parse(actual);
        Check(parsed.RootElement.ValueKind == JsonValueKind.Array && parsed.RootElement.GetArrayLength() == value.ItemCount,
            "fragmented encoded output is one complete valid JSON array");
    }

    private void CheckCredits(FakeBody body, int maximum, int acquired, int requests = -1)
    {
        Check(body.CreditRequests.Count == (requests < 0 ? acquired : requests) && body.CreditRequests.All(value => value == 3 * maximum),
            "public JSON paths retain the unchanged maximum-times-three credit request");
        Check(body.CurrentManaged == 0 && body.PeakManaged == 3 * maximum && body.CreditsAcquired == acquired && body.CreditsReleased == acquired,
            "all admitted JSON credits are released exactly once without exceeding the grant");
    }

    private async Task<FakeRequest> RunRequestAsync(int maximum, Func<RequestContext, FakeRequest, CancellationToken, Task> handler,
        Action<FakeRequest>? configure = null, Func<Exception, bool>? expectedFailure = null)
    {
        var options = new ServerOptions { MaxJsonBytes = maximum };
        var config = new ServerConfiguration(ServerConfiguration.Snapshot(options), [], [JsonBoundaryContext.Default], null, null);
        await using var app = new NativeServerApplication(config, new FakeHost(), new ServiceCollection().BuildServiceProvider(), true);
        var request = new FakeRequest(1, "GET", []);
        configure?.Invoke(request);
        Exception? handlerFailure = null;
        app.MapRaw("/", ["GET"], async (context, token) =>
        {
            try { await handler(context, request, token); }
            catch (Exception error) { handlerFailure = error; throw; }
        }).WithMaxJsonBytes(maximum);
        await app.StartAsync();
        await app.DispatchAsync(request).WaitAsync(JoinLimit);
        if (handlerFailure is not null && (expectedFailure is null || !expectedFailure(handlerFailure)))
            ExceptionDispatchInfo.Capture(handlerFailure).Throw();
        if (expectedFailure is not null) Check(handlerFailure is not null && expectedFailure(handlerFailure), "expected handler failure reached dispatch cleanup");
        return request;
    }
}

[JsonConverter(typeof(JsonBoundaryConverter))]
internal sealed class JsonBoundaryValue
{
    internal int TotalBytes { get; }
    internal int ItemCount { get; }
    internal int FalseCount { get; }
    internal bool Flush { get; }
    internal int ThrowAfterItem { get; }
    internal int SerializeCalls, Flushes, ItemsWritten;

    internal JsonBoundaryValue(int totalBytes, bool flush = false, int throwAfterItem = -1)
    {
        TotalBytes = totalBytes; Flush = flush; ThrowAfterItem = throwAfterItem;
        ItemCount = (totalBytes - 1) / 5; FalseCount = (totalBytes - 1) % 5;
        if (ItemCount < 1 || FalseCount > ItemCount) throw new ArgumentOutOfRangeException(nameof(totalBytes));
    }

    internal byte[] ExpectedBytes()
    {
        var json = new StringBuilder(TotalBytes);
        json.Append('[');
        for (int i = 0; i < ItemCount; i++) { if (i != 0) json.Append(','); json.Append(i < FalseCount ? "false" : "null"); }
        json.Append(']');
        return Encoding.UTF8.GetBytes(json.ToString());
    }
}

internal sealed class JsonBoundaryConverter : JsonConverter<JsonBoundaryValue>
{
    public override JsonBoundaryValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("The boundary fixture only serializes.");

    public override void Write(Utf8JsonWriter writer, JsonBoundaryValue value, JsonSerializerOptions options)
    {
        value.SerializeCalls++;
        writer.WriteStartArray();
        for (int i = 0; i < value.ItemCount; i++)
        {
            if (i < value.FalseCount) writer.WriteBooleanValue(false); else writer.WriteNullValue();
            value.ItemsWritten++;
            if (value.ThrowAfterItem == i + 1) throw new JsonException("synthetic incremental converter failure");
            // Leave room for the locked writer's initial request after an
            // explicit Flush. The final exact-limit tail is still fragmented.
            if (value.Flush && (i + 1) % 257 == 0 && writer.BytesCommitted + writer.BytesPending < value.TotalBytes - 512)
            { writer.Flush(); value.Flushes++; }
        }
        writer.WriteEndArray();
    }
}

[JsonSerializable(typeof(JsonBoundaryValue))]
internal partial class JsonBoundaryContext : JsonSerializerContext;

internal sealed class TrackedJsonValues(IReadOnlyList<JsonBoundaryValue> values) : IAsyncEnumerable<JsonBoundaryValue>
{
    internal Action? BeforeMoveNext;
    internal int Moves, Disposals;
    public IAsyncEnumerator<JsonBoundaryValue> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
        new Enumerator(this, values, cancellationToken);

    private sealed class Enumerator(TrackedJsonValues owner, IReadOnlyList<JsonBoundaryValue> items, CancellationToken token) : IAsyncEnumerator<JsonBoundaryValue>
    {
        private int _index = -1;
        public JsonBoundaryValue Current => items[_index];
        public ValueTask<bool> MoveNextAsync()
        {
            token.ThrowIfCancellationRequested(); owner.BeforeMoveNext?.Invoke(); owner.Moves++;
            return ValueTask.FromResult(++_index < items.Count);
        }
        public ValueTask DisposeAsync() { owner.Disposals++; return ValueTask.CompletedTask; }
    }
}
