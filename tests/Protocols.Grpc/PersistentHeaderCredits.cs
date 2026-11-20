using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using NativeWeave.Grpc.Tests.Messages;
using NativeWeave.Server;
using NativeWeave.Server.Grpc;
using NativeWeave.Server.Native;
using G = Grpc.Core;

internal static partial class Acceptance
{
    private static async Task PersistentHeaderCredits(string certificatePath, string keyPath)
    {
        var requests = new ConcurrentDictionary<int, NativeRequest>();
        var builder = NativeServer.CreateBuilder();
        builder.Listen(IPAddress.Loopback, 0, options =>
        { options.Protocols = ServerProtocols.Http2; options.Tls = ServerTlsOptions.FromPemFiles(certificatePath, keyPath); });
        builder.Options.RequestTimeout = TimeSpan.FromSeconds(15);
        builder.Options.Budget.MaxActiveRequests = 1;
        await using var app = builder.Build();
        async Task<EchoMessage> Respond(EchoMessage value, GrpcCallContext call)
        {
            Check(requests.TryAdd(value.Number, (NativeRequest)call.RequestContext.Native), "Persistent H2 call enters exactly once");
            string number = value.Number.ToString(CultureInfo.InvariantCulture);
            var headers = new GrpcMetadata();
            headers.Add("x-credit-initial", number);
            headers.AddBinary("credit-bin", [(byte)value.Number, 0, 255]);
            call.ResponseTrailers.Add("x-credit-final", number);
            call.ResponseTrailers.AddBinary("credit-end-bin", [255, 0, (byte)value.Number]);
            await call.WriteResponseHeadersAsync(headers);
            return value;
        }
        static GrpcMethodOptions CreditLimits() => new()
        { MaxEncodedMessageBytes = 5_000, MaxDecodedMessageBytes = 5_000, MaxStreamBytes = 40_000, Timeout = TimeSpan.FromSeconds(15) };
        app.MapGrpcUnary(Method("CreditUnary"), Respond, CreditLimits());
        app.MapGrpcClientStreaming(Method("CreditClient", GrpcMethodKind.ClientStreaming),
            async (values, call) => await Respond(await AcrossLast(values, call), call), CreditLimits());
        await app.StartAsync();
        using var certificate = X509CertificateLoader.LoadCertificateFromFile(certificatePath);
        string fingerprint = certificate.GetCertHashString();
        HttpClient Client() => new(new SocketsHttpHandler
        { SslOptions = { RemoteCertificateValidationCallback = (_, presented, _, _) => presented?.GetCertHashString() == fingerprint } })
        { Timeout = TimeSpan.FromSeconds(20) };
        using var firstClient = Client();
        using var secondClient = Client();
        using var firstChannel = global::Grpc.Net.Client.GrpcChannel.ForAddress(app.BoundAddresses.Single(),
            new global::Grpc.Net.Client.GrpcChannelOptions { HttpClient = firstClient });
        using var secondChannel = global::Grpc.Net.Client.GrpcChannel.ForAddress(app.BoundAddresses.Single(),
            new global::Grpc.Net.Client.GrpcChannelOptions { HttpClient = secondClient });

        async Task<NativeRequest> Send(G.CallInvoker invoker, int number, bool clientStreaming = false)
        {
            var value = new EchoMessage { Number = number, Text = "persistent", Payload = ByteString.CopyFrom([(byte)number, 0, 255]) };
            void Verify(EchoMessage response, G.Metadata headers, G.Status status, G.Metadata trailers)
            {
                string text = number.ToString(CultureInfo.InvariantCulture);
                Check(response.Equals(value) && status.StatusCode == G.StatusCode.OK, "Persistent H2 preserves exact protobuf and OK status");
                Check(headers.GetValue("x-credit-initial") == text && headers.GetValueBytes("credit-bin")!.SequenceEqual(new byte[] { (byte)number, 0, 255 }),
                    "Persistent H2 preserves initial ASCII and binary metadata");
                Check(trailers.GetValue("x-credit-final") == text && trailers.GetValueBytes("credit-end-bin")!.SequenceEqual(new byte[] { 255, 0, (byte)number }),
                    "Persistent H2 preserves trailing ASCII and binary metadata");
            }
            if (clientStreaming)
            {
                using var call = invoker.AsyncClientStreamingCall(ClientMethod("CreditClient", G.MethodType.ClientStreaming), null, new());
                await call.RequestStream.WriteAsync(value); await call.RequestStream.CompleteAsync();
                var response = await call.ResponseAsync.WaitAsync(TimeSpan.FromSeconds(3));
                Verify(response, await call.ResponseHeadersAsync, call.GetStatus(), call.GetTrailers());
            }
            else
            {
                using var call = invoker.AsyncUnaryCall(ClientMethod("CreditUnary", G.MethodType.Unary), null, new(), value);
                var response = await call.ResponseAsync.WaitAsync(TimeSpan.FromSeconds(3));
                Verify(response, await call.ResponseHeadersAsync, call.GetStatus(), call.GetTrailers());
            }
            NativeRequest request = requests[number];
            await request.Owner.Retirement.WaitAsync(TimeSpan.FromSeconds(3));
            await AcrossWaitIdle(request);
            Check(NoQueuedHeaderCredit(request), "Completed call releases all request slots and credits while TLS H2 clients remain connected");
            return request;
        }

        NativeRequest first = await Send(firstChannel.CreateCallInvoker(), 1);
        IPEndPoint firstPeer = first.RemoteEndPoint ?? throw new InvalidOperationException("Missing first TLS peer endpoint");
        NativeRequest same = await Send(firstChannel.CreateCallInvoker(), 2, clientStreaming: true);
        Check(firstPeer.Equals(same.RemoteEndPoint), "Unary and client streaming reuse the same real TLS H2 connection with one request slot");
        NativeRequest independent = await Send(secondChannel.CreateCallInvoker(), 3);
        Check(!firstPeer.Equals(independent.RemoteEndPoint), "An independent live TLS H2 connection uses the released admission slot");
        NativeRequest reused = await Send(firstChannel.CreateCallInvoker(), 4);
        Check(firstPeer.Equals(reused.RemoteEndPoint) && requests.Count == 4, "The first TLS H2 connection stays reusable after the independent call");
        var close = await app.CloseAsync(TimeSpan.FromSeconds(5));
        Check(close.Status == ServerCloseStatus.Completed && close.PendingRequests == 0 && close.RetainedManagedBytes == 0 && close.RetainedNativeBytes == 0,
            "Persistent H2 header-credit host fully drains");
    }

    private static unsafe bool NoQueuedHeaderCredit(NativeRequest request) => request.Engine.Snapshot().queued_requests == 0;
}
