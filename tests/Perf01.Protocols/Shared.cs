using System.Text.Json.Serialization;

namespace NativeWeave.Perf01;

internal static class Inputs
{
    internal const string Bearer = "Bearer Perf01-synthetic-local-test-token";
    internal static byte[] Bytes(int size)
    {
        byte[] bytes = new byte[size];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)((i * 31L + (i >> 8) * 17L + 20261119) & 255);
        return bytes;
    }
    internal static string Argument(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        if (index < 0 || index + 1 >= args.Length) throw new ArgumentException("Missing " + name);
        return args[index + 1];
    }
}

internal sealed record TransferReply(long Bytes, string Sha256);
internal sealed record SmallReply(string Message, int Value);
internal sealed record Ready(string[] Addresses, bool DynamicCodeSupported, Dictionary<string, string> LoadedNativeSha256);
internal sealed record Closed(string Status, int PendingRequests, long RetainedManagedBytes, long RetainedNativeBytes, string State);
internal sealed record ResourceSnapshot(long Timestamp, long TimestampFrequency, string Utc, long ManagedAllocatedBytes, long ManagedHeapBytes,
    long ManagedCommittedBytes, int Gen0, int Gen1, int Gen2, Dictionary<string, long> NativeGauges, string Phase);
internal sealed class ServerConfig
{
    public string Certificate { get; set; } = "";
    public string PrivateKey { get; set; } = "";
    public int ChunkBytes { get; set; } = 32768;
    public int MaxConnections { get; set; } = 512;
    public int MaxHttp2StreamsPerConnection { get; set; } = 32;
    public int MaxActiveRequests { get; set; } = 256;
    public int MaxQueuedRequests { get; set; } = 128;
    public long ManagedPayloadBytes { get; set; } = 67108864;
    public long NativePayloadBytes { get; set; } = 67108864;
    public long RetainedBytesPerRequest { get; set; } = 4194304;
    public int MaxMessageBytes { get; set; } = 1048576;
    public long MaxStreamBytes { get; set; } = 67108864;
    public int MaxJsonBytes { get; set; } = 1048576;
    public int RequestTimeoutSeconds { get; set; } = 30;
    public int ShutdownTimeoutSeconds { get; set; } = 30;
}
internal sealed class ClientConfig
{
    public string Http1 { get; set; } = "";
    public string Http2 { get; set; } = "";
    public string Certificate { get; set; } = "";
    public int Concurrency { get; set; } = 16;
    public int Http2Connections { get; set; } = 4;
    public int TimeoutSeconds { get; set; } = 20;
    public int QueueCapacity { get; set; } = 1024;
    public Dictionary<int, string> ExpectedHashes { get; set; } = [];
}
internal sealed record ClientHello(string Kind, bool DynamicCodeSupported, int Pid, long TimestampFrequency);
internal sealed record Admission(string Kind, long Id);
internal sealed record Completion(string Kind, long Id, string Status, string Value);
internal sealed record OperationRecord(long Id, string Scenario, string Protocol, string LogicalStatus, string WireStatus,
    long StartedTimestamp, long CompletedTimestamp, long RequestMessages, long ResponseMessages, long RequestPayloadBytes,
    long ResponsePayloadBytes, long RequestEncodedBytes, long ResponseEncodedBytes, bool IntentionalCancellation,
    bool IntentionalDeadline, bool CorrectnessFailure, string? Failure);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(TransferReply))]
[JsonSerializable(typeof(SmallReply))]
[JsonSerializable(typeof(ServerConfig))]
[JsonSerializable(typeof(ClientConfig))]
[JsonSerializable(typeof(Ready))]
[JsonSerializable(typeof(Closed))]
[JsonSerializable(typeof(ResourceSnapshot))]
[JsonSerializable(typeof(ClientHello))]
[JsonSerializable(typeof(Admission))]
[JsonSerializable(typeof(Completion))]
[JsonSerializable(typeof(OperationRecord))]
internal partial class WireJson : JsonSerializerContext { }
