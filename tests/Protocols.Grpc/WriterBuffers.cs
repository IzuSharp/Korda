using System.Buffers;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using NativeWeave.Grpc.Tests.Messages;
using NativeWeave.Server;
using NativeWeave.Server.Grpc;
using G = Grpc.Core;

internal static partial class Acceptance
{
    private const int WriterBufferInitial = 16 * 1024;
    private const int WriterBufferMaximum = 65_539;
    private enum WriterBufferMode { Small, Grow, Fallback, ThrowSmall, ThrowGrow, NativeRejectGrow }
    private sealed class WriterBufferState : IDisposable
    {
        internal readonly WriterState Pressure = new(WriterBufferMaximum);
        internal NativeRequest? Native;
        internal ulong Baseline;
        internal int Encodes;
        internal WriterBufferMode Mode;
        internal IBufferWriter<byte>? LastWriter;
        internal WeakReference<byte[]>? FirstBacking, GrownBacking;
        internal Exception? Failure;
        public void Dispose() => Pressure.Dispose();
    }

    private static async Task PerMessageWriterBuffers(string certificatePath, string keyPath)
    {
        using var state = new WriterBufferState();
        var builder = NativeServer.CreateBuilder();
        builder.Listen(IPAddress.Loopback, 0, options =>
        { options.Protocols = ServerProtocols.Http2; options.Tls = ServerTlsOptions.FromPemFiles(certificatePath, keyPath); });
        builder.Options.RequestTimeout = TimeSpan.FromSeconds(15);
        builder.Options.Budget.MaxActiveRequests = 1;
        await using var app = builder.Build();
        app.Use(async (context, next, token) =>
        { ((NativeRequest)context.Native).Engine.TestRetirement = Observe; await next(context, token); });
        app.MapGrpcServerStreaming(Method("WriterBuffers", GrpcMethodKind.ServerStreaming),
            (_, call) => WriterBufferSequence(state, call), new()
            {
                MaxEncodedMessageBytes = WriterBufferMaximum, MaxDecodedMessageBytes = 8_192,
                MaxStreamBytes = 10L * WriterBufferMaximum, Timeout = TimeSpan.FromSeconds(15)
            });
        await app.StartAsync();
        using var certificate = X509CertificateLoader.LoadCertificateFromFile(certificatePath);
        string fingerprint = certificate.GetCertHashString();
        using var handler = new SocketsHttpHandler
        { SslOptions = { RemoteCertificateValidationCallback = (_, presented, _, _) => presented?.GetCertHashString() == fingerprint } };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        using var channel = global::Grpc.Net.Client.GrpcChannel.ForAddress(app.BoundAddresses.Single(),
            new global::Grpc.Net.Client.GrpcChannelOptions { HttpClient = client });
        var expected = new[]
        {
            new EchoMessage { Number = 501 }, ExactWriterMessage(WriterBufferMaximum, 0xa5),
            new EchoMessage { Number = 502 }, new EchoMessage { Number = 503 }, new EchoMessage()
        };
        Exception? failure = null;
        try
        {
            using var call = channel.CreateCallInvoker().AsyncServerStreamingCall(
                ClientMethod("WriterBuffers", G.MethodType.ServerStreaming), null, new(), new EchoMessage());
            int seen = 0;
            while (await call.ResponseStream.MoveNext(default))
            {
                Check(seen < expected.Length && call.ResponseStream.Current.Equals(expected[seen]),
                    "Per-message buffer wire bytes and ordering " + seen);
                seen++;
            }
            Check(seen == expected.Length && call.GetStatus().StatusCode == G.StatusCode.OK,
                "Per-message buffer failures emit no partial message and preserve terminal status");
        }
        catch (Exception error) { failure = error; }
        var close = await app.CloseAsync(TimeSpan.FromSeconds(5));
        Check(close.Status == ServerCloseStatus.Completed && close.PendingRequests == 0 &&
            close.RetainedManagedBytes == 0 && close.RetainedNativeBytes == 0,
            "Per-message buffers drain all actual native and managed owners");
        if (state.Failure is { } serverFailure) ExceptionDispatchInfo.Capture(serverFailure).Throw();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static async IAsyncEnumerable<EchoMessage> WriterBufferSequence(WriterBufferState state, GrpcCallContext call)
    {
        try
        {
            var native = (NativeRequest)call.RequestContext.Native;
            state.Native = native; state.Baseline = WriterManagedBytes(native);
            var codec = new GrpcMarshaller<EchoMessage>((value, writer) => EncodeWriterBuffer(state, value, writer),
                static bytes => EchoMessage.Parser.ParseFrom(bytes));
            async Task Send(WriterBufferMode mode, EchoMessage value)
            {
                state.Mode = mode; state.LastWriter = null; state.FirstBacking = null; state.GrownBacking = null;
                int encodes = state.Encodes;
                try { await call.Transport.WriteAsync(value, codec, call.CancellationToken); }
                finally
                {
                    Check(state.Encodes == encodes + 1, "Per-message encoder is never retried");
                    Check(WriterManagedBytes(native) == state.Baseline, "Per-message complete and overlap grants return exactly");
                    if (state.LastWriter is { } writer)
                    {
                        DisposedWriter(writer);
                        ((IDisposable)writer).Dispose();
                        Check(WriterManagedBytes(native) == state.Baseline, "Repeated writer disposal does not release another grant");
                    }
                    if (state.FirstBacking is { } first) CollectWriterBacking(first);
                    if (state.GrownBacking is { } grown) CollectWriterBacking(grown);
                }
            }
            await Send(WriterBufferMode.Small, new EchoMessage { Number = 501 });
            await Send(WriterBufferMode.Grow, ExactWriterMessage(WriterBufferMaximum, 0xa5));
            ulong baseline = state.Baseline;
            ulong callbackFailures = WriterBufferCallbackFailures(native);
            int heldBytes = checked((int)(native.Engine.Configuration.Options.Budget.RetainedBytesPerRequest -
                checked((long)baseline) - WriterBufferMaximum));
            using (native.AcquireProtocolManagedCredit(heldBytes))
            {
                state.Baseline = checked(baseline + (ulong)heldBytes);
                try { await Send(WriterBufferMode.Fallback, new EchoMessage { Number = 502 }); }
                finally { state.Baseline = baseline; }
            }
            Check(WriterManagedBytes(native) == baseline, "Optional-overlap denial preserves the original maximum-only success path");
            Check(WriterBufferHostHealthy(native, callbackFailures), "Optional-overlap denial is not a native callback or host failure");
            await Throws<WriterFixtureException>(() => Send(WriterBufferMode.ThrowSmall, new EchoMessage { Number = 599 }));
            await Throws<WriterFixtureException>(() => Send(WriterBufferMode.ThrowGrow, ExactWriterMessage(WriterBufferMaximum, 0x5a)));
            try
            {
                try
                {
                    await Send(WriterBufferMode.NativeRejectGrow, ExactWriterMessage(WriterBufferMaximum, 0x3c));
                    throw new InvalidOperationException("Expected actual native-create capacity denial after growth.");
                }
                catch (GrpcException error) when (error.StatusCode == GrpcStatusCode.ResourceExhausted)
                { Check(state.Pressure.UnstartedCreates.Count == 40, "Growth completed before exact native-create rejection"); }
            }
            finally { state.Pressure.ReleaseCreates(); }
            await Send(WriterBufferMode.Small, new EchoMessage { Number = 503 });
            // An encoder may emit an empty protobuf without requesting a buffer.
            int emptyEncodes = 0;
            IBufferWriter<byte>? emptyWriter = null;
            var empty = new GrpcMarshaller<EchoMessage>((_, writer) =>
            {
                emptyEncodes++; emptyWriter = writer;
                Check(WriterManagedBytes(native) == baseline + WriterBufferMaximum, "Empty encode keeps original full pre-admission");
                writer.Advance(0);
            }, static bytes => EchoMessage.Parser.ParseFrom(bytes));
            await call.Transport.WriteAsync(new EchoMessage(), empty, call.CancellationToken);
            Check(emptyEncodes == 1 && WriterManagedBytes(native) == baseline, "Empty encode completes once with exact grant cleanup");
            DisposedWriter(emptyWriter!);
        }
        catch (Exception error) { state.Failure = error; throw; }
        finally { state.Pressure.ReleaseCreates(); }
        yield break;
    }

    private static unsafe ulong WriterBufferCallbackFailures(NativeRequest request) => request.Engine.Snapshot().callback_failures;
    private static unsafe bool WriterBufferHostHealthy(NativeRequest request, ulong callbackFailures)
    {
        var snapshot = request.Engine.Snapshot();
        return snapshot.state == 2 && snapshot.callback_failures == callbackFailures;
    }

    private static void EncodeWriterBuffer(WriterBufferState state, EchoMessage value, IBufferWriter<byte> writer)
    {
        state.Encodes++; state.LastWriter = writer;
        NativeRequest native = state.Native!;
        Check(WriterManagedBytes(native) == state.Baseline + WriterBufferMaximum,
            "First buffer selection follows unchanged full-maximum pre-admission");
        Memory<byte> first = writer.GetMemory();
        bool fallback = state.Mode == WriterBufferMode.Fallback;
        int capacity = fallback ? WriterBufferMaximum : WriterBufferInitial;
        Check(first.Length == capacity && IsZero(first.Span), "Fresh per-message storage is zeroed at the actual chosen capacity");
        Check(MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)first, out var initial) &&
            initial.Array is not null && initial.Offset == 0 && initial.Array.Length == capacity,
            "Per-message first allocation has exact ordinary-array capacity");
        state.FirstBacking = new(initial.Array!);
        ulong expected = state.Baseline + WriterBufferMaximum + (fallback ? 0UL : WriterBufferInitial);
        Check(WriterManagedBytes(native) == expected, "Optional overlap is granted before small storage is exposed");
        if (state.Mode == WriterBufferMode.ThrowSmall) { first.Span.Fill(0xd7); throw new WriterFixtureException(); }
        if (state.Mode is WriterBufferMode.Grow or WriterBufferMode.ThrowGrow or WriterBufferMode.NativeRejectGrow)
        {
            byte[] payload = value.ToByteArray(); // Application-owned test codec storage.
            Check(payload.Length == WriterBufferMaximum, "Growth fixture occupies the exact non-power-of-two encoded limit");
            int prefix = WriterBufferInitial / 2;
            payload.AsSpan(0, prefix).CopyTo(first.Span); writer.Advance(prefix);
            Memory<byte> grown = writer.GetMemory(payload.Length - prefix);
            Check(MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)grown, out var current) &&
                current.Array is not null && current.Offset == prefix && current.Array.Length == WriterBufferMaximum &&
                !ReferenceEquals(initial.Array, current.Array), "Growth preserves the committed prefix in one new maximum backing");
            state.GrownBacking = new(current.Array!);
            Check(WriterManagedBytes(native) == expected, "Old and new backings remain covered by the pregranted overlap");
            if (state.Mode == WriterBufferMode.ThrowGrow) { grown.Span.Fill(0x6b); throw new WriterFixtureException(); }
            payload.AsSpan(prefix).CopyTo(grown.Span); writer.Advance(payload.Length - prefix);
            if (state.Mode == WriterBufferMode.NativeRejectGrow) HoldWriterOperationSlots(state.Pressure, native);
        }
        else value.WriteTo(writer);
    }
}
