using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading.Channels;
using Google.Protobuf;
using Grpc.Net.Client;
using NativeWeave.Perf01;
using NativeWeave.Perf01.Wire;
using G = Grpc.Core;

var config = JsonSerializer.Deserialize(File.ReadAllText(Inputs.Argument(args, "--config")), WireJson.Default.ClientConfig)!;
string output = Inputs.Argument(args, "--output");
Directory.CreateDirectory(output);
await using var driver = new Driver(config, Path.Combine(output, "operations.jsonl"));
await driver.RunAsync();

internal sealed class Driver : IAsyncDisposable
{
    private readonly ClientConfig _config;
    private readonly HttpClient _http1;
    private readonly HttpClient[] _http2;
    private readonly GrpcChannel[] _channels;
    private readonly G.CallInvoker[] _invokers;
    private readonly StreamWriter _records;
    private readonly object _outputGate = new();
    private readonly Dictionary<int, byte[]> _bytes = [];
    private readonly Dictionary<int, ByteString> _protobuf = [];
    private readonly Dictionary<int, string> _hashes = [];
    private readonly string _certificateHash;
    private static readonly G.Marshaller<Payload> Codec = G.Marshallers.Create<Payload>(static value => value.ToByteArray(), static bytes => Payload.Parser.ParseFrom(bytes));
    internal Driver(ClientConfig config, string records)
    {
        if (config.Concurrency is < 1 or > 512 || config.QueueCapacity is < 1 or > 4096 || config.Http2Connections is < 1 or > 32)
            throw new ArgumentOutOfRangeException(nameof(config));
        _config = config;
        using var certificate = X509CertificateLoader.LoadCertificateFromFile(config.Certificate);
        _certificateHash = certificate.GetCertHashString(HashAlgorithmName.SHA256);
        _http1 = CreateHttp(config.Http1, HttpVersion.Version11, config.Concurrency);
        _http2 = Enumerable.Range(0, config.Http2Connections).Select(_ => CreateHttp(config.Http2, HttpVersion.Version20, 1)).ToArray();
        _channels = _http2.Select(http => GrpcChannel.ForAddress(config.Http2, new() { HttpClient = http, DisposeHttpClient = false })).ToArray();
        _invokers = _channels.Select(channel => channel.CreateCallInvoker()).ToArray();
        _records = new StreamWriter(records) { AutoFlush = true };
        foreach (int size in new[] { 64, 4096, 65536, 1048576, 8388608 })
        {
            byte[] bytes = Inputs.Bytes(size);
            _bytes.Add(size, bytes); _protobuf.Add(size, ByteString.CopyFrom(bytes));
            _hashes.Add(size, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            if (!config.ExpectedHashes.TryGetValue(size, out var expected) || expected != _hashes[size])
                throw new IntegrityFailure("Payload differs from the independent frozen Python hash.");
        }
    }
    private HttpClient CreateHttp(string address, Version version, int connections)
    {
        var uri = new Uri(address);
        if (uri.Scheme != "https" || !IPAddress.TryParse(uri.Host, out var ip) || !IPAddress.IsLoopback(ip))
            throw new InvalidOperationException("Only the configured loopback TLS target is supported.");
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = connections, EnableMultipleHttp2Connections = false,
            AutomaticDecompression = DecompressionMethods.None, AllowAutoRedirect = false,
            UseCookies = false, UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(5)
        };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, presented, _, _) =>
            presented?.GetCertHashString(HashAlgorithmName.SHA256) == _certificateHash;
        return new HttpClient(handler) { BaseAddress = uri, DefaultRequestVersion = version,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact, Timeout = Timeout.InfiniteTimeSpan };
    }
    internal async Task RunAsync()
    {
        Console.WriteLine(JsonSerializer.Serialize(new ClientHello("hello", RuntimeFeature.IsDynamicCodeSupported, Environment.ProcessId, Stopwatch.Frequency), WireJson.Default.ClientHello));
        Console.Out.Flush();
        var queue = Channel.CreateBounded<(long Id, string Token)>(new BoundedChannelOptions(_config.QueueCapacity)
        { SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        Task[] workers = Enumerable.Range(0, _config.Concurrency).Select(_ => Consume()).ToArray();
        while (await Console.In.ReadLineAsync() is { } line)
        {
            string[] fields = line.Split('\t');
            if (fields.Length != 3 || fields[2] != "ok") throw new InvalidOperationException("Invalid A21 input line.");
            await queue.Writer.WriteAsync((long.Parse(fields[0], CultureInfo.InvariantCulture), fields[1]));
        }
        queue.Writer.Complete();
        await Task.WhenAll(workers);
        async Task Consume()
        {
            await foreach (var input in queue.Reader.ReadAllAsync()) await ExecuteAsync(input.Id, input.Token);
        }
    }
    private async Task ExecuteAsync(long id, string token)
    {
        string[] fields = token.Split('|');
        if (fields.Length != 4) throw new InvalidOperationException("Invalid frozen workload token.");
        string scenario = fields[0], protocol = fields[1];
        int size = int.Parse(fields[2], CultureInfo.InvariantCulture), messages = int.Parse(fields[3], CultureInfo.InvariantCulture);
        var counts = new Counts(); string status = "ok", wire = "OK"; string? failure = null; bool correctnessFailure = false;
        long started = Stopwatch.GetTimestamp();
        lock (_outputGate) { Console.WriteLine(JsonSerializer.Serialize(new Admission("admitted", id), WireJson.Default.Admission)); Console.Out.Flush(); }
        try
        {
            if (scenario.StartsWith("http-", StringComparison.Ordinal)) wire = await HttpAsync(id, scenario, protocol, size, counts);
            else
            {
                Require(protocol == "http2-tls", "gRPC requires exact HTTP/2 over TLS.");
                wire = await GrpcAsync(id, scenario, size, messages, counts);
            }
        }
        catch (G.RpcException error)
        {
            wire = error.StatusCode.ToString();
            status = error.StatusCode switch { G.StatusCode.ResourceExhausted or G.StatusCode.Unavailable => "rejected", G.StatusCode.DeadlineExceeded => "timeout", G.StatusCode.Cancelled => "cancelled", _ => "failed" };
            failure = error.GetType().Name;
        }
        catch (OperationCanceledException error) { status = "timeout"; wire = "Timeout"; failure = error.GetType().Name; }
        catch (HttpFailure error) { wire = error.Status.ToString(CultureInfo.InvariantCulture); status = error.Status is 429 or 503 ? "rejected" : "failed"; failure = error.GetType().Name; }
        catch (Exception error) { status = "failed"; wire = error.GetType().Name; correctnessFailure = error is IntegrityFailure; failure = correctnessFailure ? error.Message : error.GetType().Name; }
        long completed = Stopwatch.GetTimestamp();
        var record = new OperationRecord(id, scenario, protocol, status, wire, started, completed, counts.Inputs, counts.Outputs,
            counts.InputBytes, counts.OutputBytes, counts.EncodedInputs, counts.EncodedOutputs, counts.Canceled, counts.Deadline, correctnessFailure, failure);
        lock (_outputGate)
        {
            _records.WriteLine(JsonSerializer.Serialize(record, WireJson.Default.OperationRecord));
            Console.WriteLine(JsonSerializer.Serialize(new Completion("completed", id, status, token), WireJson.Default.Completion));
            Console.Out.Flush();
        }
    }
    private async Task<string> HttpAsync(long id, string scenario, string protocol, int size, Counts counts)
    {
        HttpClient client = protocol switch { "http1-tls" => _http1, "http2-tls" => _http2[id % _http2.Length], _ => throw new IntegrityFailure("Unexpected HTTP protocol token.") };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(_config.TimeoutSeconds));
        string path = scenario switch { "http-tiny" => "/tiny", "http-json" => "/json", "http-upload" or "http-cancel" => "/upload", "http-download" or "http-slow" => "/download/" + size.ToString(CultureInfo.InvariantCulture), _ => throw new IntegrityFailure("Unexpected HTTP scenario.") };
        using var request = new HttpRequestMessage(scenario is "http-upload" or "http-cancel" ? HttpMethod.Put : HttpMethod.Get, path)
        { Version = client.DefaultRequestVersion, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
        request.Headers.Authorization = AuthenticationHeaderValue.Parse(Inputs.Bearer);
        if (scenario is "http-upload" or "http-cancel")
            request.Content = new StreamingContent(_bytes[size], stop, counts, scenario == "http-cancel");
        counts.Inputs = 1;
        HttpResponseMessage response;
        try { response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stop.Token); }
        catch (OperationCanceledException) when (scenario == "http-cancel" && counts.Canceled) { return "ExpectedCanceledUpload"; }
        using (response)
        {
            Require(response.Version == client.DefaultRequestVersion, "Actual HTTP version differs.");
            if (scenario == "http-cancel") throw new IntegrityFailure("Canceled upload unexpectedly returned an HTTP response.");
            if (response.StatusCode != HttpStatusCode.OK) throw new HttpFailure((int)response.StatusCode);
            Require(response.Content.Headers.ContentEncoding.Count == 0, "HTTP response unexpectedly negotiated compression.");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var body = await response.Content.ReadAsStreamAsync(stop.Token);
            if (scenario is "http-download" or "http-slow")
            {
                Require(response.Content.Headers.ContentLength == size, "Download Content-Length differs.");
                byte[] chunk = new byte[32768]; long total = 0; int read;
                while ((read = await body.ReadAsync(chunk, stop.Token)) != 0)
                {
                    hash.AppendData(chunk, 0, read); total += read; counts.OutputBytes += read;
                    if (scenario == "http-slow") await Task.Delay(2, stop.Token);
                }
                Require(total == size && Convert.ToHexStringLower(hash.GetHashAndReset()) == _hashes[size], "Download size or content hash differs.");
                Require(response.Headers.GetValues("x-content-sha256").Single() == _hashes[size], "Download hash header differs.");
            }
            else
            {
                byte[] bounded = new byte[8193]; int length = 0, read;
                while (length < bounded.Length && (read = await body.ReadAsync(bounded.AsMemory(length), stop.Token)) != 0) length += read;
                Require(length <= 8192, "Small response exceeded independent client bound.");
                ReadOnlySpan<byte> result = bounded.AsSpan(0, length); counts.OutputBytes += length;
                if (scenario == "http-tiny") Require(result.SequenceEqual("0123456789abcdef"u8), "Tiny response content differs.");
                else if (scenario == "http-json")
                {
                    var value = JsonSerializer.Deserialize(result, WireJson.Default.SmallReply);
                    Require(value is { Message: "perf01", Value: 42 }, "Static JSON response differs.");
                }
                else
                {
                    var value = JsonSerializer.Deserialize(result, WireJson.Default.TransferReply);
                    Require(value is not null && value.Bytes == size && value.Sha256 == _hashes[size], "Upload hash or acknowledged length differs.");
                }
            }
            counts.Inputs = counts.Outputs = 1;
            return "HTTP200";
        }
    }
    private G.CallOptions Options(CancellationToken token, bool deadline = false)
        => new(headers: new G.Metadata { { "authorization", Inputs.Bearer } },
            deadline: DateTime.UtcNow.AddMilliseconds(deadline ? 150 : _config.TimeoutSeconds * 1000), cancellationToken: token);
    private static G.Method<Payload, Payload> Method(string name, G.MethodType kind)
        => new(kind, "nativeweave.perf01.Benchmark", name, Codec, Codec);
    private async Task<string> GrpcAsync(long id, string scenario, int size, int messages, Counts counts)
    {
        G.CallInvoker invoker = _invokers[id % _invokers.Length];
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(_config.TimeoutSeconds));
        var request = new Payload { Sequence = 0, Data = _protobuf[size], Count = messages };
        if (scenario == "grpc-unary")
        {
            using var call = invoker.AsyncUnaryCall(Method("Unary", G.MethodType.Unary), null, Options(stop.Token), request);
            Submitted(request, counts); var response = await call.ResponseAsync; Received(response, counts);
            Require(response.Equals(request), "Unary protobuf differs."); Trailers(call.GetStatus(), call.GetTrailers(), 1, 1);
        }
        else if (scenario is "grpc-server" or "grpc-deadline" or "grpc-cancel")
        {
            bool special = scenario != "grpc-server";
            using var call = invoker.AsyncServerStreamingCall(Method(special ? "Wait" : "Server", G.MethodType.ServerStreaming), null, Options(stop.Token, scenario == "grpc-deadline"), request);
            Submitted(request, counts);
            if (special) Require((await call.ResponseHeadersAsync).GetValue("perf-started") == "yes", "Wait headers were not committed.");
            int seen = 0;
            try
            {
                while (await call.ResponseStream.MoveNext(stop.Token))
                {
                    var value = call.ResponseStream.Current; Received(value, counts);
                    Require(value.Equals(new Payload { Sequence = seen, Data = request.Data, Count = messages }), "Server stream protobuf/order differs.");
                    seen++;
                    if (scenario == "grpc-cancel") { counts.Canceled = true; await stop.CancelAsync(); }
                }
                if (special) throw new IntegrityFailure("Wait stream ended without its expected deadline/cancellation.");
            }
            catch (G.RpcException error) when (scenario == "grpc-deadline" && error.StatusCode == G.StatusCode.DeadlineExceeded && seen == 1)
            { counts.Deadline = true; return "ExpectedDeadlineExceeded"; }
            catch (G.RpcException error) when (scenario == "grpc-cancel" && error.StatusCode == G.StatusCode.Cancelled && seen == 1 && counts.Canceled)
            { return "ExpectedCanceledStream"; }
            catch (OperationCanceledException) when (scenario == "grpc-cancel" && seen == 1 && counts.Canceled)
            { return "ExpectedCanceledStream"; }
            Require(seen == messages, "Server stream message count differs."); Trailers(call.GetStatus(), call.GetTrailers(), 1, messages);
        }
        else if (scenario == "grpc-client")
        {
            using var call = invoker.AsyncClientStreamingCall(Method("Client", G.MethodType.ClientStreaming), null, Options(stop.Token));
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (int i = 0; i < messages; i++)
            { request.Sequence = i; await call.RequestStream.WriteAsync(request, stop.Token); Submitted(request, counts); hash.AppendData(request.Data.Span); }
            await call.RequestStream.CompleteAsync();
            var value = await call.ResponseAsync; Received(value, counts);
            Require(value.Equals(new Payload { Count = messages, TotalBytes = (long)size * messages, Digest = Convert.ToHexStringLower(hash.GetHashAndReset()) }), "Client stream aggregate protobuf/hash differs.");
            Trailers(call.GetStatus(), call.GetTrailers(), messages, 1);
        }
        else if (scenario == "grpc-duplex")
        {
            using var call = invoker.AsyncDuplexStreamingCall(Method("Duplex", G.MethodType.DuplexStreaming), null, Options(stop.Token));
            // Both directions are actually live: response reads start before sending/half-close.
            Task read = ReceiveAsync();
            for (int i = 0; i < messages; i++)
            { request.Sequence = i; await call.RequestStream.WriteAsync(request, stop.Token); Submitted(request, counts); }
            await call.RequestStream.CompleteAsync(); await read;
            Trailers(call.GetStatus(), call.GetTrailers(), messages, messages);
            async Task ReceiveAsync()
            {
                int seen = 0;
                while (await call.ResponseStream.MoveNext(stop.Token))
                {
                    var value = call.ResponseStream.Current; Received(value, counts);
                    Require(value.Equals(new Payload { Sequence = seen, Count = messages, Data = _protobuf[size] }), "Duplex protobuf/order differs."); seen++;
                }
                Require(seen == messages, "Duplex message count differs.");
            }
        }
        else throw new IntegrityFailure("Unexpected gRPC scenario.");
        return "GrpcOK";
    }
    private static void Submitted(Payload value, Counts count)
    { count.Inputs++; count.InputBytes += value.Data.Length; count.EncodedInputs += value.CalculateSize(); }
    private static void Received(Payload value, Counts count)
    { count.Outputs++; count.OutputBytes += value.Data.Length; count.EncodedOutputs += value.CalculateSize(); }
    private static void Trailers(G.Status status, G.Metadata trailers, long inputs, long outputs)
    {
        Require(status.StatusCode == G.StatusCode.OK, "gRPC terminal status differs.");
        Require(trailers.GetValue("perf-completed") == "yes" && trailers.GetValue("perf-inputs") == inputs.ToString(CultureInfo.InvariantCulture)
            && trailers.GetValue("perf-outputs") == outputs.ToString(CultureInfo.InvariantCulture), "gRPC terminal trailers differ.");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new IntegrityFailure(message); }
    public ValueTask DisposeAsync()
    {
        foreach (var channel in _channels) channel.Dispose();
        foreach (var client in _http2) client.Dispose();
        _http1.Dispose(); _records.Dispose(); return ValueTask.CompletedTask;
    }
    private sealed class Counts
    {
        internal long Inputs, Outputs, InputBytes, OutputBytes, EncodedInputs, EncodedOutputs;
        internal bool Canceled, Deadline;
    }
    private sealed class StreamingContent(byte[] bytes, CancellationTokenSource stop, Counts counts, bool cancel) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = bytes.Length; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => WriteAsync(stream, stop.Token);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token) => WriteAsync(stream, token);
        private async Task WriteAsync(Stream stream, CancellationToken token)
        {
            int chunks = 0;
            for (int offset = 0; offset < bytes.Length;)
            {
                int length = Math.Min(32768, bytes.Length - offset);
                await stream.WriteAsync(bytes.AsMemory(offset, length), token);
                counts.InputBytes += length; offset += length; chunks++;
                if (cancel && chunks == 4) { counts.Canceled = true; await stop.CancelAsync(); stop.Token.ThrowIfCancellationRequested(); }
                if (cancel) await Task.Delay(2, token);
            }
        }
    }
    private sealed class IntegrityFailure(string message) : Exception(message);
    private sealed class HttpFailure(int status) : Exception("Unexpected HTTP status") { internal int Status => status; }
}
