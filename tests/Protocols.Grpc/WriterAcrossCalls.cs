using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using NativeWeave.Grpc.Tests.Messages;
using NativeWeave.Server;
using NativeWeave.Server.Grpc;
using NativeWeave.Server.Native;
using G = Grpc.Core;

internal static partial class Acceptance
{
    // These real-call checks also apply to the uncached implementation. They
    // require isolation and lifetime guarantees, never a GC-dependent cache hit.
    private sealed class AcrossCallsState(int maximum) : IDisposable
    {
        internal readonly int Maximum = maximum;
        internal readonly TaskCompletionSource Entered = Signal();
        internal readonly ManualResetEventSlim Release = new();
        internal readonly TaskCompletionSource CancelEntered = Signal();
        internal readonly ManualResetEventSlim CancelRelease = new();
        private readonly object _gate = new();
        private readonly Dictionary<int, IBufferWriter<byte>> _writers = [];
        private readonly HashSet<int> _completed = [];
        private WeakReference<byte[]>? _held;
        internal WeakReference<byte[]>? LastBacking;
        internal NativeRequest? Native;
        internal Exception? Failure;

        internal void Encode(EchoMessage value, IBufferWriter<byte> writer)
        {
            try
            {
                Memory<byte> memory = writer.GetMemory(Maximum);
                Check(memory.Length == Maximum && IsZero(memory.Span), "Cross-call destination begins fully zeroed at its exact cap");
                Check(MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)memory, out var segment) &&
                    segment.Array is not null && segment.Offset == 0 && segment.Count == Maximum && segment.Array.Length == Maximum,
                    "Cross-call backing has the exact non-power-of-two capacity");
                lock (_gate)
                {
                    foreach (int completed in _completed) DisposedWriter(_writers[completed]);
                    if (_held is not null && _held.TryGetTarget(out byte[]? active))
                        Check(!ReferenceEquals(active, segment.Array), "Concurrent calls never encode into the same backing");
                    _writers.Add(value.Number, writer);
                    LastBacking = new(segment.Array!);
                    if (value.Number is 101 or 103) _held = LastBacking;
                }
                // Every successful call dirties its uncommitted tail as well.
                // Failure cleanup must clear those bytes before any later loan.
                memory.Span.Fill(0xd7);
                if (value.Number == 30) throw new WriterFixtureException();
                if (value.Number == 32) { writer.GetMemory(Maximum + 1); throw new InvalidOperationException("Oversize hint was accepted"); }
                if (value.Number == 34) { writer.Advance(Maximum + 1); throw new InvalidOperationException("Oversize advancement was accepted"); }
                value.WriteTo(writer);
                if (value.Number == 101)
                {
                    Entered.TrySetResult();
                    if (!Release.Wait(TimeSpan.FromSeconds(8))) throw new TimeoutException("Concurrent encoder release");
                }
                if (value.Number == 103)
                {
                    CancelEntered.TrySetResult();
                    if (!CancelRelease.Wait(TimeSpan.FromSeconds(8))) throw new TimeoutException("Canceled encoder release");
                }
            }
            catch (WriterFixtureException) when (value.Number == 30) { throw; }
            catch (GrpcException error) when ((value.Number is 32 or 34) && error.StatusCode == GrpcStatusCode.ResourceExhausted) { throw; }
            catch (Exception error) { lock (_gate) Failure ??= error; throw; }
            finally { if (value.Number is 101 or 103) lock (_gate) _held = null; }
        }
        internal void Completed(int number)
        {
            lock (_gate)
            {
                Check(_writers.TryGetValue(number, out var writer), "Expected response encoder ran before terminal status");
                DisposedWriter(writer!);
                _completed.Add(number);
            }
        }
        public void Dispose() { Release.Dispose(); CancelRelease.Dispose(); }
    }

    private static void DisposedWriter(IBufferWriter<byte> writer)
    {
        foreach (Action action in new Action[] { () => writer.GetMemory(), () => writer.GetSpan(), () => writer.Advance(1) })
        {
            try { action(); }
            catch (ObjectDisposedException) { continue; }
            throw new InvalidOperationException("A completed scoped writer became usable again.");
        }
    }

    private static async Task WriterAcrossCalls(string certificatePath, string keyPath)
    {
        using var unary = new AcrossCallsState(5_000);
        using var clientStream = new AcrossCallsState(10_000);
        using var serverStream = new AcrossCallsState(65_539);
        using var duplex = new AcrossCallsState(5_001);
        var builder = NativeServer.CreateBuilder();
        builder.Listen(IPAddress.Loopback, 0, options =>
        { options.Protocols = ServerProtocols.Http2; options.Tls = ServerTlsOptions.FromPemFiles(certificatePath, keyPath); });
        builder.Options.RequestTimeout = TimeSpan.FromSeconds(15);
        builder.Options.Budget.MaxActiveRequests = 4;
        await using var app = builder.Build();
        app.Use(async (context, next, token) =>
        { ((NativeRequest)context.Native).Engine.TestRetirement = Observe; await next(context, token); });
        static GrpcMethod<EchoMessage, EchoMessage> CrossMethod(string name, GrpcMethodKind kind, AcrossCallsState state) =>
            new("/nativeweave.Test/" + name, kind, Echo, new(state.Encode, static bytes => EchoMessage.Parser.ParseFrom(bytes)));
        static GrpcMethodOptions CrossLimits(AcrossCallsState state) => new()
        { MaxEncodedMessageBytes = state.Maximum, MaxDecodedMessageBytes = state.Maximum, MaxStreamBytes = 8L * state.Maximum, Timeout = TimeSpan.FromSeconds(15) };
        app.MapGrpcUnary(CrossMethod("AcrossUnary", GrpcMethodKind.Unary, unary), (value, call) =>
        { unary.Native = (NativeRequest)call.RequestContext.Native; return Task.FromResult(value); }, CrossLimits(unary));
        app.MapGrpcClientStreaming(CrossMethod("AcrossClient", GrpcMethodKind.ClientStreaming, clientStream), async (values, call) =>
        { clientStream.Native = (NativeRequest)call.RequestContext.Native; return await AcrossLast(values, call); }, CrossLimits(clientStream));
        app.MapGrpcServerStreaming(CrossMethod("AcrossServer", GrpcMethodKind.ServerStreaming, serverStream), Stream, CrossLimits(serverStream));
        app.MapGrpcDuplex(CrossMethod("AcrossDuplex", GrpcMethodKind.DuplexStreaming, duplex), Duplex, CrossLimits(duplex));
        await app.StartAsync();
        using var certificate = X509CertificateLoader.LoadCertificateFromFile(certificatePath);
        string fingerprint = certificate.GetCertHashString();
        using var handler = new SocketsHttpHandler
        { SslOptions = { RemoteCertificateValidationCallback = (_, presented, _, _) => presented?.GetCertHashString() == fingerprint } };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        using var channel = global::Grpc.Net.Client.GrpcChannel.ForAddress(app.BoundAddresses.Single(),
            new global::Grpc.Net.Client.GrpcChannelOptions { HttpClient = client });
        var invoker = channel.CreateCallInvoker();
        Exception? failure = null;
        try
        {
            foreach (var entry in new[] { (unary, "AcrossUnary", G.MethodType.Unary), (clientStream, "AcrossClient", G.MethodType.ClientStreaming) })
            {
                var (state, name, kind) = entry;
                Task Send(int number, G.StatusCode expected = G.StatusCode.OK) => AcrossSingle(invoker, name, kind, state,
                    new EchoMessage { Number = number, Payload = ByteString.CopyFrom(new byte[] { (byte)number, 0, 255 }) }, expected);
                await Send(5);
                Task held = Send(101);
                try
                {
                    await state.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                    Check(!held.IsCompleted, "First encoder stays active while a different call completes");
                    var probes = new List<Task> { Send(102) };
                    if (kind == G.MethodType.Unary)
                    {
                        probes.Add(AcrossServer(invoker, serverStream));
                        probes.Add(AcrossDuplex(invoker, duplex));
                    }
                    await Task.WhenAll(probes);
                }
                finally { state.Release.Set(); await held; }
                await Send(15);
                using (var cancel = new CancellationTokenSource())
                {
                    Task canceled = AcrossSingle(invoker, name, kind, state, new EchoMessage { Number = 103 }, G.StatusCode.Cancelled, cancel.Token, complete: false);
                    try
                    {
                        await state.CancelEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                        await cancel.CancelAsync();
                        await canceled.WaitAsync(TimeSpan.FromSeconds(3));
                    }
                    finally { state.CancelRelease.Set(); await cancel.CancelAsync(); await canceled; }
                    await AcrossWaitIdle(state.Native!);
                    state.Completed(103);
                }
                await Send(16);
                await Send(30, G.StatusCode.Internal);
                await Send(31);
                await Send(32, G.StatusCode.ResourceExhausted);
                await Send(33);
                await Send(34, G.StatusCode.ResourceExhausted);
                await Send(35);
                await AcrossSingle(invoker, name, kind, state, ExactWriterMessage(state.Maximum, 0xa5), G.StatusCode.OK);
                await Task.Yield();
                CollectWriterBacking(state.LastBacking!);
            }
        }
        catch (Exception error) { failure = error; }
        finally { unary.Release.Set(); clientStream.Release.Set(); unary.CancelRelease.Set(); clientStream.CancelRelease.Set(); }
        var close = await app.CloseAsync(TimeSpan.FromSeconds(5));
        Check(close.Status == ServerCloseStatus.Completed && close.PendingRequests == 0 && close.RetainedManagedBytes == 0 && close.RetainedNativeBytes == 0,
            "Cross-call writers, native copies and queues fully drain");
        foreach (var state in new[] { unary, clientStream, serverStream, duplex })
            if (state.Failure is { } serverFailure) ExceptionDispatchInfo.Capture(serverFailure).Throw();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        await WriterAcrossCallNativeFailure(certificatePath, keyPath);
    }

    private static async Task AcrossSingle(G.CallInvoker invoker, string name, G.MethodType kind, AcrossCallsState state, EchoMessage value, G.StatusCode expected,
        CancellationToken cancellationToken = default, bool complete = true)
    {
        try
        {
            EchoMessage response;
            if (kind == G.MethodType.Unary)
            {
                using var call = invoker.AsyncUnaryCall(ClientMethod(name, kind), null, new(cancellationToken: cancellationToken), value);
                response = await call.ResponseAsync;
                Check(call.GetStatus().StatusCode == G.StatusCode.OK, "Cross-call unary terminal status");
            }
            else
            {
                using var call = invoker.AsyncClientStreamingCall(ClientMethod(name, kind), null, new(cancellationToken: cancellationToken));
                await call.RequestStream.WriteAsync(value); await call.RequestStream.CompleteAsync();
                response = await call.ResponseAsync;
                Check(call.GetStatus().StatusCode == G.StatusCode.OK, "Cross-call client-stream terminal status");
            }
            Check(expected == G.StatusCode.OK && response.Equals(value), "Cross-call independent client sees unchanged protobuf bytes and fields");
        }
        catch (G.RpcException error) when (expected != G.StatusCode.OK && error.StatusCode == expected)
        { Check(true, "Cross-call encoder failure preserves protocol classification"); }
        if (complete) state.Completed(value.Number);
    }

    private static async Task AcrossServer(G.CallInvoker invoker, AcrossCallsState state)
    {
        using var call = invoker.AsyncServerStreamingCall(ClientMethod("AcrossServer", G.MethodType.ServerStreaming), null, new(), new EchoMessage { Number = 201 });
        int number = 201;
        while (await call.ResponseStream.MoveNext(default))
        { Check(number <= 202 && call.ResponseStream.Current.Equals(new EchoMessage { Number = number }), "Concurrent server-stream content"); number++; }
        Check(number == 203 && call.GetStatus().StatusCode == G.StatusCode.OK, "Concurrent server-stream count and trailers");
        state.Completed(201); state.Completed(202);
    }

    private static async Task AcrossDuplex(G.CallInvoker invoker, AcrossCallsState state)
    {
        using var call = invoker.AsyncDuplexStreamingCall(ClientMethod("AcrossDuplex", G.MethodType.DuplexStreaming), null, new());
        foreach (int number in new[] { 301, 302 })
        {
            var value = new EchoMessage { Number = number, Payload = ByteString.CopyFrom(new byte[] { 8, 9, 10 }) };
            await call.RequestStream.WriteAsync(value);
            Check(await call.ResponseStream.MoveNext(default) && call.ResponseStream.Current.Equals(value), "Concurrent duplex content before half-close");
        }
        await call.RequestStream.CompleteAsync();
        Check(!await call.ResponseStream.MoveNext(default) && call.GetStatus().StatusCode == G.StatusCode.OK, "Concurrent duplex count and trailers");
        state.Completed(301); state.Completed(302);
    }

    private static async Task<EchoMessage> AcrossLast(IAsyncEnumerable<EchoMessage> values, GrpcCallContext call)
    {
        var result = new EchoMessage();
        await foreach (var value in values.WithCancellation(call.CancellationToken)) result = value;
        return result;
    }

    private static async Task WriterAcrossCallNativeFailure(string certificatePath, string keyPath)
    {
        using var state = new WriterState(10_000) { DirtyUnusedTail = true };
        NativeRequest? current = null;
        ulong baseline = 0;
        IDisposable? heldCredit = null;
        int heldBytes = 0, nativeDenials = 0;
        var builder = NativeServer.CreateBuilder();
        builder.Listen(IPAddress.Loopback, 0, options =>
        { options.Protocols = ServerProtocols.Http2; options.Tls = ServerTlsOptions.FromPemFiles(certificatePath, keyPath); });
        builder.Options.RequestTimeout = TimeSpan.FromSeconds(10);
        builder.Options.Budget.MaxActiveRequests = 1;
        await using var app = builder.Build();
        app.Use(async (context, next, token) =>
        {
            ((NativeRequest)context.Native).Engine.TestRetirement = Observe;
            try { await next(context, token); }
            finally
            {
                try
                {
                    if (state.FailNativeCreate)
                    {
                        Check(state.UnstartedCreates.Count == 40 && WriterManagedBytes(current!) == baseline,
                            "Cross-call native create rejection returns exact managed credit before status");
                        nativeDenials++;
                    }
                    if (heldCredit is not null)
                        Check(WriterManagedBytes(current!) == baseline + checked((ulong)heldBytes),
                            "Cross-call managed admission rejection preserves the existing held credit");
                }
                catch (Exception error) { state.Failure ??= error; throw; }
                finally
                { state.ReleaseCreates(); heldCredit?.Dispose(); heldCredit = null; }
            }
        });
        EchoMessage Prepare(EchoMessage value, GrpcCallContext call)
        {
            current = (NativeRequest)call.RequestContext.Native;
            baseline = WriterManagedBytes(current);
            if (value.Number == 98)
            {
                heldBytes = checked((int)(current.Engine.Configuration.Options.Budget.RetainedBytesPerRequest - checked((long)baseline) - state.Maximum + 1));
                heldCredit = current.AcquireProtocolManagedCredit(heldBytes);
            }
            return value;
        }
        var responseCodec = new GrpcMarshaller<EchoMessage>((value, writer) => EncodeWriterFixture(state, current!, baseline, value, writer), static bytes => EchoMessage.Parser.ParseFrom(bytes));
        GrpcMethod<EchoMessage, EchoMessage> FailureMethod(string name, GrpcMethodKind kind) => new("/nativeweave.Test/" + name, kind, Echo, responseCodec);
        GrpcMethodOptions options = new() { MaxEncodedMessageBytes = state.Maximum, MaxDecodedMessageBytes = state.Maximum, MaxStreamBytes = 80_000, Timeout = TimeSpan.FromSeconds(10) };
        app.MapGrpcUnary(FailureMethod("AcrossFailureUnary", GrpcMethodKind.Unary), (value, call) => Task.FromResult(Prepare(value, call)), options);
        app.MapGrpcClientStreaming(FailureMethod("AcrossFailureClient", GrpcMethodKind.ClientStreaming),
            async (values, call) => Prepare(await AcrossLast(values, call), call), options);
        await app.StartAsync();
        using var certificate = X509CertificateLoader.LoadCertificateFromFile(certificatePath);
        string fingerprint = certificate.GetCertHashString();
        using var handler = new SocketsHttpHandler
        { SslOptions = { RemoteCertificateValidationCallback = (_, presented, _, _) => presented?.GetCertHashString() == fingerprint } };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        using var channel = global::Grpc.Net.Client.GrpcChannel.ForAddress(app.BoundAddresses.Single(),
            new global::Grpc.Net.Client.GrpcChannelOptions { HttpClient = client });
        var invoker = channel.CreateCallInvoker();
        Exception? failure = null;
        try
        {
            foreach (var (name, kind) in new[] { ("AcrossFailureUnary", G.MethodType.Unary), ("AcrossFailureClient", G.MethodType.ClientStreaming) })
            {
                async Task Send(int number, G.StatusCode expected = G.StatusCode.OK)
                {
                    var value = new EchoMessage { Number = number };
                    try
                    {
                        EchoMessage response;
                        if (kind == G.MethodType.Unary)
                        { using var call = invoker.AsyncUnaryCall(ClientMethod(name, kind), null, new(), value); response = await call.ResponseAsync; }
                        else
                        {
                            using var call = invoker.AsyncClientStreamingCall(ClientMethod(name, kind), null, new());
                            await call.RequestStream.WriteAsync(value); await call.RequestStream.CompleteAsync(); response = await call.ResponseAsync;
                        }
                        Check(expected == G.StatusCode.OK && response.Equals(value), "Cross-call failure recovery returns the expected protobuf response");
                    }
                    catch (G.RpcException error) when (expected != G.StatusCode.OK && error.StatusCode == expected)
                    { Check(true, "Cross-call real admission failure preserves ResourceExhausted"); }
                    if (current is not null) await AcrossWaitIdle(current);
                }
                await Send(11);
                int beforeNative = state.Encodes;
                state.FailNativeCreate = true;
                try { await Send(21, G.StatusCode.ResourceExhausted); }
                finally { state.FailNativeCreate = false; state.ReleaseCreates(); }
                Check(state.Encodes == beforeNative + 1, "Native create rejection follows exactly one encoder invocation");
                await Send(22);
                int beforeManaged = state.Encodes;
                await Send(98, G.StatusCode.ResourceExhausted);
                Check(state.Encodes == beforeManaged, "Insufficient E credit rejects before promoting or invoking a new writer");
                await Send(23);
            }
            Check(nativeDenials == 2, "Both single-response method kinds exercised real native create rejection");
            await Task.Yield();
            CollectWriterBacking(state.LastBacking!);
        }
        catch (Exception error) { failure = error; }
        finally { state.ReleaseCreates(); heldCredit?.Dispose(); heldCredit = null; }
        var close = await app.CloseAsync(TimeSpan.FromSeconds(5));
        Check(close.Status == ServerCloseStatus.Completed && close.PendingRequests == 0 && close.RetainedManagedBytes == 0 && close.RetainedNativeBytes == 0,
            "Cross-call failure creators and all byte grants drain");
        if (state.Failure is { } serverFailure) ExceptionDispatchInfo.Capture(serverFailure).Throw();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static unsafe bool AcrossRequestIdle(NativeRequest request)
    {
        var snapshot = request.Engine.Snapshot();
        return snapshot.active_requests == 0 && snapshot.retiring_requests == 0 &&
            snapshot.retained_managed_bytes == 0 && snapshot.retained_native_bytes == 0;
    }

    private static async Task AcrossWaitIdle(NativeRequest request)
    {
        long start = Stopwatch.GetTimestamp();
        while (!AcrossRequestIdle(request))
        {
            if (Stopwatch.GetElapsedTime(start) >= TimeSpan.FromSeconds(3)) throw new TimeoutException("Cross-call retirement");
            await Task.Delay(5);
        }
    }
}
