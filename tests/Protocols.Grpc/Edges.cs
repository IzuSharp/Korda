using System.Net;
using System.Threading.Channels;
using Google.Protobuf;
using NativeWeave.Grpc.Tests.Messages;
using NativeWeave.Server;
using NativeWeave.Server.Grpc;
using G = Grpc.Core;

internal static partial class Acceptance
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class EdgeState
    {
        internal readonly TaskCompletionSource ReadWaiting = Signal(), ReadCanceled = Signal(), CleanupEntered = Signal(), CleanupRelease = Signal(), Disposed = Signal();
        internal int CleanupOrder;
    }
    private static async Task StartupContracts()
    {
        foreach (string scenario in new[] { "endpoint-filter", "inherited-filter", "body-override", "json-override", "http1-only", "native-floor" })
        {
            var builder = NativeServer.CreateBuilder();
            builder.Listen(IPAddress.Loopback, 0, o => o.Protocols = scenario == "http1-only" ? ServerProtocols.Http1 : ServerProtocols.Http2);
            if (scenario == "native-floor")
            {
                builder.Options.MaxJsonBytes = 1024;
                builder.Options.Budget.RetainedBytesPerRequest = 100_000;
                builder.Options.Budget.NativePayloadBytes = 100_000;
            }
            await using var app = builder.Build();
            ServerRoutes routes = app;
            if (scenario == "inherited-filter") routes = app.MapGroup("/nativeweave.Test").AddFilter(static (context, next, token) => next(context, token));
            var endpoint = routes.MapGrpcUnary(Method("Startup"), static (v, _) => Task.FromResult(v), Limits());
            if (scenario == "endpoint-filter") endpoint.AddFilter(static (context, next, token) => next(context, token));
            if (scenario == "body-override") endpoint.WithMaxRequestBodyBytes(1000);
            if (scenario == "json-override") endpoint.WithMaxJsonBytes(1000);
            await Throws<InvalidOperationException>(() => app.StartAsync());
            Check(app.BoundAddresses.Count == 0, "Startup rejects before listening: " + scenario);
        }
        var metadata = new GrpcMetadata();
        for (int i = 0; i < 200; i++) metadata.AddBinary("empty-bin", []);
        await Throws<ArgumentException>(() => { metadata.AddBinary("empty-bin", []); return Task.CompletedTask; });
    }
    private static EdgeState MapEdges(NativeServerApplication app)
    {
        var state = new EdgeState();
        app.MapGrpcUnary(Method("HeaderCount"), static (_, c) => Task.FromResult(new EchoMessage
        { Number = c.RequestContext.Request.Headers.ToDictionary().Sum(x => x.Value.Count), Text = c.RequestHeaders.GetBinaryValues("many-bin").Count.ToString(System.Globalization.CultureInfo.InvariantCulture) }), Limits());
        app.MapGrpcUnary(Method("HeaderRetry"), HeaderRetry, Limits());
        app.MapGrpcClientStreaming(Method("DisposeRace", GrpcMethodKind.ClientStreaming), DisposeRace, Limits());
        app.MapGrpcClientStreaming(new GrpcMethod<EchoMessage, CountMessage>("/nativeweave.Test/InputQuota", GrpcMethodKind.ClientStreaming, Echo, Count), Sum, Limits(stream: 16));
        app.MapGrpcServerStreaming(Method("OutputQuota", GrpcMethodKind.ServerStreaming), QuotaOutput, Limits(stream: 16));
        app.MapGrpcClientStreaming(Method("CancelRead", GrpcMethodKind.ClientStreaming), async (values, context) =>
        {
            using var registration = context.CancellationToken.Register(() => state.ReadCanceled.TrySetResult());
            await using var iterator = values.GetAsyncEnumerator(context.CancellationToken);
            Check(await iterator.MoveNextAsync(), "Cancellation fixture receives its first message");
            state.ReadWaiting.TrySetResult();
            try
            {
                Check(!await iterator.MoveNextAsync(), "Reset may settle Tonic request input as EOF");
                await state.ReadCanceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
                context.CancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) { state.ReadCanceled.TrySetResult(); throw; }
            throw new InvalidOperationException("The pending input read unexpectedly completed.");
        }, Limits());
        app.MapGrpcUnary(Method("Cleanup"), (value, context) =>
        {
            context.RequestContext.Response.OnCompleted(async () =>
            {
                Check(Interlocked.CompareExchange(ref state.CleanupOrder, 1, 0) == 0, "Protocol completion callback runs before registered disposal");
                state.CleanupEntered.TrySetResult(); await state.CleanupRelease.Task;
            });
            context.RequestContext.Response.RegisterForDisposeAsync(new CleanupResource(state));
            return Task.FromResult(value);
        }, Limits());
        return state;
    }
    private static async Task<EchoMessage> HeaderRetry(EchoMessage value, GrpcCallContext call)
    {
        var native = (NativeRequest)call.RequestContext.Native;
        var initial = new GrpcMetadata(); initial.Add("x-retried", "yes");
        long remaining = native.Engine.Configuration.Options.Budget.RetainedBytesPerRequest;
        // The current inbound message copy has already left framework ownership.
        // Exhaust the independent managed request ledger without changing native state.
        using (var held = native.AcquireProtocolManagedCredit(checked((int)(remaining - 16_384))))
        {
            var tooLarge = new GrpcMetadata(); tooLarge.Add("x-large", new string('x', 20_000));
            await Throws<ServerException>(() => call.WriteResponseHeadersAsync(tooLarge));
        }
        await call.WriteResponseHeadersAsync(initial);
        return value;
    }
    private static async Task<EchoMessage> DisposeRace(IAsyncEnumerable<EchoMessage> values, GrpcCallContext context)
    {
        var entered = Signal(); using var release = new ManualResetEventSlim();
        var stream = (GrpcMessageStream<EchoMessage>)values;
        stream.TestBeforePublish = () => { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("Publish fixture release"); };
        var iterator = stream.GetAsyncEnumerator(context.CancellationToken);
        await Throws<InvalidOperationException>(() => { stream.GetAsyncEnumerator(); return Task.CompletedTask; });
        Task<bool> moving = iterator.MoveNextAsync().AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            await Throws<InvalidOperationException>(() => iterator.MoveNextAsync().AsTask());
            Task first = iterator.DisposeAsync().AsTask(), second = iterator.DisposeAsync().AsTask();
            Check(ReferenceEquals(first, second) && !first.IsCompleted, "Repeated dispose joins the same paused MoveNext continuation");
            release.Set(); await first;
            Check(!await moving, "Disposed inbound stream does not republish a delayed Current");
            await Throws<InvalidOperationException>(() => { _ = iterator.Current; return Task.CompletedTask; });
        }
        finally { release.Set(); }
        return new EchoMessage { Text = "joined" };
    }
    private static async IAsyncEnumerable<EchoMessage> QuotaOutput(EchoMessage value, GrpcCallContext context)
    { yield return new() { Text = "12345678" }; await Task.CompletedTask; yield return new() { Text = "12345678" }; }
    private sealed class CleanupResource(EdgeState state) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            Check(Interlocked.CompareExchange(ref state.CleanupOrder, 2, 1) == 1, "Registered disposal follows completed callback");
            state.Disposed.TrySetResult(); return ValueTask.CompletedTask;
        }
    }
    private static async Task RunEdges(NativeServerApplication app, HttpClient client, G.CallInvoker invoker, EdgeState state, byte[] wire)
    {
        Check((await Raw(client, "HeaderRetry", wire)).Headers.Contains("x-retried"), "Rejected synchronous header admission permits one valid retry");
        Check((await Raw(client, "DisposeRace", wire)).Status == 0, "Successful native read and concurrent disposal safely join");
        var ordinary = await Raw(client, "HeaderCount", wire);
        int ordinaryCount = EchoMessage.Parser.ParseFrom(ordinary.Payload.AsSpan(5)).Number;
        int binaryCount = 200 - ordinaryCount;
        var exact = await Raw(client, "HeaderCount", wire, [("many-bin", new string(',', binaryCount - 1))]);
        Check(exact.Status == 0 && EchoMessage.Parser.ParseFrom(exact.Payload.AsSpan(5)).Text == binaryCount.ToString(System.Globalization.CultureInfo.InvariantCulture), "Exactly 200 projected entries including empty binary segments");
        Check((await Raw(client, "HeaderCount", wire, [("many-bin", new string(',', binaryCount))])).Status == 8, "Split entry201 rejects before handler");
        // A long repeated key is cheap on the wire, but consumes the decoded block
        // once per segment. It must fail the decoded-block cap before decoding.
        string longName = new string('k', 220) + "-bin";
        Check((await Raw(client, "Unary", wire, [(longName, new string(',', 159))])).Status == 8, "Decoded metadata byte cap includes every repeated key");
        Check((await Raw(client, "Unary", wire, [("grpc-timeout", "0n")])).Status == 4, "Already expired timeout is DeadlineExceeded");
        Check((await Raw(client, "Unary", wire, [("grpc-timeout", "99999999H")])).Status == 0, "Large valid timeout is capped by finite Host deadline");
        var small = Frame(new EchoMessage { Text = "12345678" }.ToByteArray());
        Check((await Raw(client, "InputQuota", [.. small, .. small])).Status == 8, "Actual inbound streaming total quota");
        Check((await Raw(client, "OutputQuota", Frame([]))).Status == 8, "Actual outbound streaming total quota after first message");
        using (var cancel = new CancellationTokenSource())
        using (var call = invoker.AsyncClientStreamingCall(ClientMethod("CancelRead", G.MethodType.ClientStreaming), null, new G.CallOptions(cancellationToken: cancel.Token)))
        {
            await call.RequestStream.WriteAsync(new EchoMessage { Number = 1 });
            await state.ReadWaiting.Task.WaitAsync(TimeSpan.FromSeconds(2)); cancel.Cancel();
            try { await call.ResponseAsync; throw new InvalidOperationException("Expected canceled client call"); }
            catch (G.RpcException error) { Check(error.StatusCode == G.StatusCode.Cancelled, "Independent client observes pending-read cancellation"); }
            await state.ReadCanceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        Task<WireResult> response = Raw(client, "Cleanup", wire);
        await state.CleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            Check((await response.WaitAsync(TimeSpan.FromSeconds(2))).Status == 0, "Typed terminal status reaches client before lifecycle cleanup finishes");
            var pending = await app.CloseAsync(TimeSpan.FromMilliseconds(10));
            Check(pending.Status == ServerCloseStatus.Pending && pending.PendingRequests > 0, "Unfinished gRPC cleanup retains request ownership");
        }
        finally { state.CleanupRelease.TrySetResult(); }
        await state.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }
    private static async Task PressureContracts()
    {
        var builder = NativeServer.CreateBuilder(); builder.Listen(IPAddress.Loopback, 0, o => o.Protocols = ServerProtocols.Http2);
        builder.Options.MaxHeaderBytes = 1024; builder.Options.MaxJsonBytes = 1024;
        builder.Options.Budget.MaxChunkBytes = 1024; builder.Options.Budget.NativePayloadBytes = 60_000;
        builder.Options.Budget.RetainedBytesPerRequest = 60_000;
        var entered = Channel.CreateBounded<int>(8); var release = Signal(); int calls = 0;
        await using var app = builder.Build();
        app.Use(async (context, next, token) => { ((NativeRequest)context.Native).Engine.TestRetirement = Observe; await next(context, token); });
        app.MapGrpcUnary(Method("Pressure"), async (value, _) =>
        { entered.Writer.TryWrite(Interlocked.Increment(ref calls)); await release.Task; return value; },
            new() { MaxEncodedMessageBytes = 1024, MaxDecodedMessageBytes = 1024, MaxStreamBytes = 8192, Timeout = TimeSpan.FromSeconds(3) });
        await app.StartAsync();
        using var channel = global::Grpc.Net.Client.GrpcChannel.ForAddress(app.BoundAddresses.Single()); var invoker = channel.CreateCallInvoker();
        var held = new List<G.AsyncUnaryCall<EchoMessage>>();
        try
        {
            bool exhausted = false;
            for (int i = 0; i < 8; i++)
            {
                var call = invoker.AsyncUnaryCall(ClientMethod("Pressure", G.MethodType.Unary), null, new(), new EchoMessage()); held.Add(call);
                Task<int> admission = entered.Reader.ReadAsync().AsTask();
                Task winner = await Task.WhenAny(admission, call.ResponseAsync).WaitAsync(TimeSpan.FromSeconds(2));
                if (winner == admission) { await admission; continue; }
                try { await call.ResponseAsync; throw new InvalidOperationException("Pressure call completed before release"); }
                catch (G.RpcException error) { Check(error.StatusCode == G.StatusCode.ResourceExhausted, "Nonwaiting native protocol budget exhaustion"); }
                exhausted = true; break;
            }
            Check(exhausted && calls > 0 && calls < held.Count, "Payload exhaustion admits no additional business callback");
        }
        finally { release.TrySetResult(); foreach (var call in held) { try { await call.ResponseAsync; } catch (G.RpcException) { } call.Dispose(); } }
        var close = await app.CloseAsync(TimeSpan.FromSeconds(3));
        Check(close.Status == ServerCloseStatus.Completed && close.RetainedNativeBytes == 0 && close.RetainedManagedBytes == 0, "Pressure grants and partial admission retire to zero");
    }
}
