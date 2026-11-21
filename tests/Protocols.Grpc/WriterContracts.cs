using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using NativeWeave.Grpc.Tests.Messages;
using NativeWeave.Interop.Abi;
using NativeWeave.Server;
using NativeWeave.Server.Grpc;
using NativeWeave.Server.Native;
using G = Grpc.Core;

internal static partial class Acceptance
{
    // Wire/credit/lifetime regressions using the existing internal test access.
    // These must pass the eager writer
    // as well as a future weak scratch cache; they never require a reuse count.
    private sealed class WriterState(int maximum) : IDisposable
    {
        internal readonly int Maximum = maximum;
        internal readonly TaskCompletionSource Entered = Signal();
        internal readonly ManualResetEventSlim Release = new();
        internal readonly List<ServerHandle> UnstartedCreates = new(40);
        internal WeakReference<byte[]>? LastBacking;
        internal Exception? Failure;
        internal int Encodes;
        internal bool FailEncode, BlockEncode, DirtyUnusedTail, FailNativeCreate;
        internal void ReleaseCreates()
        {
            foreach (var operation in UnstartedCreates) operation.Dispose();
            UnstartedCreates.Clear();
        }
        public void Dispose() { ReleaseCreates(); Release.Dispose(); }
    }

    private sealed class WriterFixtureException : Exception { }

    private static async Task WriterContracts(string certificatePath, string keyPath)
    {
        await PerMessageWriterBuffers(certificatePath, keyPath);
        foreach (int maximum in new[] { 5_000, 10_000, 65_539 })
        {
            await WriterContractHost(maximum, certificatePath, keyPath);
        }
    }

    private static async Task WriterContractHost(int maximum, string certificatePath, string keyPath)
    {
        using var state = new WriterState(maximum);
        var builder = NativeServer.CreateBuilder();
        builder.Listen(IPAddress.Loopback, 0, options =>
        {
            options.Protocols = ServerProtocols.Http2;
            options.Tls = ServerTlsOptions.FromPemFiles(certificatePath, keyPath);
        });
        builder.Options.RequestTimeout = TimeSpan.FromSeconds(10);
        builder.Options.Budget.MaxActiveRequests = 1;
        await using var app = builder.Build();
        app.Use(async (context, next, token) =>
        {
            ((NativeRequest)context.Native).Engine.TestRetirement = Observe;
            await next(context, token);
        });
        app.MapGrpcServerStreaming(Method("WriterContract", GrpcMethodKind.ServerStreaming),
            (_, call) => WriterSequence(state, call), new()
            {
                MaxEncodedMessageBytes = maximum,
                MaxDecodedMessageBytes = 8_192,
                MaxStreamBytes = 10L * maximum,
                Timeout = TimeSpan.FromSeconds(10)
            });
        await app.StartAsync();
        using var certificate = X509CertificateLoader.LoadCertificateFromFile(certificatePath);
        string fingerprint = certificate.GetCertHashString();
        using var handler = new SocketsHttpHandler
        {
            SslOptions = { RemoteCertificateValidationCallback = (_, presented, _, _) => presented?.GetCertHashString() == fingerprint }
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        using var channel = global::Grpc.Net.Client.GrpcChannel.ForAddress(app.BoundAddresses.Single(),
            new global::Grpc.Net.Client.GrpcChannelOptions { HttpClient = client });
        var expected = new[]
        {
            ExactWriterMessage(maximum, 0xa5), new EchoMessage { Number = 11 },
            new EchoMessage { Number = 12 }, ExactWriterMessage(maximum, 0x5a),
            new EchoMessage { Number = 22 }, new EchoMessage { Number = 33 }
        };
        Exception? clientFailure = null;
        try
        {
            using var call = channel.CreateCallInvoker().AsyncServerStreamingCall(
                ClientMethod("WriterContract", G.MethodType.ServerStreaming), null, new(), new EchoMessage());
            int seen = 0;
            while (await call.ResponseStream.MoveNext(default))
            {
                Check(seen < expected.Length && call.ResponseStream.Current.Equals(expected[seen]),
                    "Writer contract exact protobuf response " + maximum + "/" + seen);
                seen++;
            }
            Check(seen == expected.Length && call.GetStatus().StatusCode == G.StatusCode.OK,
                "Writer contract message count and terminal status " + maximum);
        }
        catch (Exception error)
        {
            clientFailure = error;
        }
        finally
        {
            state.Release.Set();
        }
        var close = await app.CloseAsync(TimeSpan.FromSeconds(5));
        Check(close.Status == ServerCloseStatus.Completed && close.PendingRequests == 0 &&
            close.RetainedManagedBytes == 0 && close.RetainedNativeBytes == 0,
            "Writer contract grants, operations and native frames drain " + maximum);
        if (state.Failure is { } serverFailure) ExceptionDispatchInfo.Capture(serverFailure).Throw();
        if (clientFailure is not null) ExceptionDispatchInfo.Capture(clientFailure).Throw();
    }

    private static async IAsyncEnumerable<EchoMessage> WriterSequence(WriterState state, GrpcCallContext call)
    {
        try
        {
            var native = (NativeRequest)call.RequestContext.Native;
            ulong baseline = WriterManagedBytes(native);
            var codec = new GrpcMarshaller<EchoMessage>(
                (value, writer) => EncodeWriterFixture(state, native, baseline, value, writer),
                static bytes => EchoMessage.Parser.ParseFrom(bytes));
            async Task Send(EchoMessage value)
            {
                await call.Transport.WriteAsync(value, codec, call.CancellationToken);
                Check(WriterManagedBytes(native) == baseline, "Writer managed credit ends at synchronous copy boundary");
            }

            await Send(ExactWriterMessage(state.Maximum, 0xa5));
            state.DirtyUnusedTail = true;
            await Send(new EchoMessage { Number = 11 });
            state.DirtyUnusedTail = false;
            // This call checks the entire destination after the previous encoder
            // wrote beyond its committed length. Clearing Written alone is unsafe.
            await Send(new EchoMessage { Number = 12 });
            await Task.Yield();
            CollectWriterBacking(state.LastBacking!);
            await Send(ExactWriterMessage(state.Maximum, 0x5a));

            state.FailEncode = true;
            try
            {
                await Throws<WriterFixtureException>(() => Send(new EchoMessage { Number = 99 }));
            }
            finally
            {
                state.FailEncode = false;
            }
            Check(WriterManagedBytes(native) == baseline, "Encoder exception releases managed credit before retry");

            int beforeDenied = state.Encodes;
            int heldBytes = checked((int)(native.Engine.Configuration.Options.Budget.RetainedBytesPerRequest -
                checked((long)baseline) - state.Maximum + 1));
            using (native.AcquireProtocolManagedCredit(heldBytes))
            {
                try
                {
                    await Send(new EchoMessage { Number = 98 });
                    throw new InvalidOperationException("Expected the unchanged maximum-sized managed admission to fail.");
                }
                catch (GrpcException error) when (error.StatusCode == GrpcStatusCode.ResourceExhausted)
                {
                    Check(state.Encodes == beforeDenied && WriterManagedBytes(native) == baseline + checked((ulong)heldBytes),
                        "Insufficient maximum credit rejects before encoding without changing held ownership");
                }
            }

            int beforeNativeDenial = state.Encodes;
            state.FailNativeCreate = true;
            try
            {
                try
                {
                    await Send(new EchoMessage { Number = 21 });
                    throw new InvalidOperationException("Expected native create to reject before transferring its reservation.");
                }
                catch (GrpcException error) when (error.StatusCode == GrpcStatusCode.ResourceExhausted)
                {
                    Check(state.Encodes == beforeNativeDenial + 1 && state.UnstartedCreates.Count == 40 &&
                        WriterManagedBytes(native) == baseline,
                        "Real native create rejection unwinds completed encoding and its unchanged managed credit");
                }
            }
            finally
            {
                state.FailNativeCreate = false;
                state.ReleaseCreates();
            }
            await Send(new EchoMessage { Number = 22 });

            state.BlockEncode = true;
            Task first = Send(new EchoMessage { Number = 33 });
            try
            {
                await state.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Check(!first.IsCompleted && WriterManagedBytes(native) == baseline + checked((ulong)state.Maximum),
                    "Blocked encoder retains the full existing managed credit");
                await Throws<InvalidOperationException>(() => call.Transport.WriteAsync(new EchoMessage { Number = 34 }, codec, call.CancellationToken));
            }
            finally
            {
                state.Release.Set();
                await first;
            }
            await Task.Yield();
            CollectWriterBacking(state.LastBacking!);
        }
        catch (Exception error)
        {
            state.Failure = error;
            throw;
        }
        finally
        {
            state.Release.Set();
            state.ReleaseCreates();
        }
        yield break;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void EncodeWriterFixture(WriterState state, NativeRequest native, ulong baseline,
        EchoMessage value, IBufferWriter<byte> writer)
    {
        state.Encodes++;
        Check(WriterManagedBytes(native) == baseline + checked((ulong)state.Maximum), "Encoder begins under the complete original credit");
        byte[] payload = value.ToByteArray(); // Application codec allocation, outside the framework's byte-writer ledger.
        Memory<byte> memory = writer.GetMemory(state.Maximum);
        Check(memory.Length >= state.Maximum && IsZero(memory.Span[..state.Maximum]), "Entire maximum destination is zero before each encode");
        Check(MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)memory, out ArraySegment<byte> segment) &&
            segment.Array is not null && segment.Offset == 0 && segment.Count == state.Maximum && segment.Array.Length == state.Maximum,
            "Concrete bounded writer exposes its current exact array to the internal lifetime fixture");
        state.LastBacking = new WeakReference<byte[]>(segment.Array!);

        WriterLimit(() => writer.GetSpan(-1));
        WriterLimit(() => writer.GetMemory(-1));
        WriterLimit(() => writer.GetSpan(state.Maximum + 1));
        WriterLimit(() => writer.GetMemory(state.Maximum + 1));
        WriterLimit(() => writer.Advance(-1));
        WriterLimit(() => writer.Advance(state.Maximum + 1));
        writer.Advance(0);
        if (state.FailEncode)
        {
            writer.GetSpan(state.Maximum).Fill(0x6b);
            throw new WriterFixtureException();
        }
        if (state.DirtyUnusedTail) writer.GetSpan(state.Maximum).Fill(0xd7);
        if (state.BlockEncode)
        {
            state.BlockEncode = false;
            state.Entered.TrySetResult();
            if (!state.Release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Writer fixture release");
        }
        for (int position = 0; position < payload.Length;)
        {
            int count = Math.Min(127, payload.Length - position);
            payload.AsSpan(position, count).CopyTo(writer.GetSpan(count));
            writer.Advance(count);
            position += count;
        }
        WriterLimit(() => writer.GetSpan(state.Maximum - payload.Length + 1));
        WriterLimit(() => writer.Advance(state.Maximum - payload.Length + 1));
        if (payload.Length == state.Maximum)
        {
            WriterLimit(() => writer.GetSpan());
            WriterLimit(() => writer.GetMemory());
        }
        if (state.FailNativeCreate) HoldWriterOperationSlots(state, native);
    }

    private static unsafe void HoldWriterOperationSlots(WriterState state, NativeRequest request)
    {
        // Host operation capacity is 8*MaxActiveRequests+32. Hold every creator
        // ourselves: first exhaustion alone could race another operation's retire.
        int slots = checked(request.Engine.Configuration.Options.Budget.MaxActiveRequests * 8 + 32);
        Check(slots == 40 && state.UnstartedCreates.Count == 0, "Finite native-create failure fixture capacity");
        long started = Stopwatch.GetTimestamp();
        while (state.UnstartedCreates.Count < slots)
        {
            var operation = new ServerHandle(0, request.Engine.Native.nw_server_operation_release);
            bool retained = false;
            try
            {
                nint pointer = request.Engine.Create(request.Engine.Owner,
                    (nint owner, nint* output, NwErrorInfoV1* error) =>
                        request.Engine.Native.nw_server_close_create(owner, 0, output, error));
                operation.Adopt(pointer);
                state.UnstartedCreates.Add(operation);
                retained = true;
            }
            catch (ServerException error) when (error.Code == "resource_limit")
            {
                if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(2))
                    throw new TimeoutException("A preexisting native operation did not retire before the bounded fixture deadline.", error);
                Thread.Sleep(1);
            }
            finally
            {
                if (!retained) operation.Dispose();
            }
        }
        Check(request.Engine.Snapshot().state == 2, "Never-started close creators have not initiated shutdown");
        // The following real gRPC create must now fail its operation-slot check.
        // No creator is started, and every owner is released by the caller's finally.
    }

    private static void WriterLimit(Action action)
    {
        try
        {
            action();
        }
        catch (GrpcException error) when (error.StatusCode == GrpcStatusCode.ResourceExhausted)
        {
            Check(true, "Writer size hint and Advance retain ResourceExhausted classification");
            return;
        }
        throw new InvalidOperationException("Expected bounded writer rejection.");
    }

    private static bool IsZero(ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            if (value != 0) return false;
        }
        return true;
    }

    private static EchoMessage ExactWriterMessage(int maximum, byte fill)
    {
        static int VarintLength(int value)
        {
            int bytes = 1;
            while ((value >>= 7) != 0) bytes++;
            return bytes;
        }
        int length = maximum - 1;
        while (length + 1 + VarintLength(length) > maximum) length--;
        byte[] payload = new byte[length];
        Array.Fill(payload, fill);
        var value = new EchoMessage { Payload = ByteString.CopyFrom(payload) };
        Check(value.CalculateSize() == maximum, "Fixture protobuf occupies the exact non-power-of-two maximum");
        return value;
    }

    private static unsafe ulong WriterManagedBytes(NativeRequest request) => request.Engine.Snapshot().retained_managed_bytes;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CollectWriterBacking(WeakReference<byte[]> backing)
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        Check(!WriterBackingAlive(backing), "Completed encoding leaves no strongly owned idle scratch buffer");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool WriterBackingAlive(WeakReference<byte[]> backing) => backing.TryGetTarget(out _);
}
