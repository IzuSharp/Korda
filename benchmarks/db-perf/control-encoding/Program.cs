using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Korda.Data;

if (args.Length != 3 || RuntimeFeature.IsDynamicCodeSupported)
    throw new ArgumentException("Native AOT only: <result.json> <source-file> <source-base-commit>");
const int Warmup = 2048, Operations = 8192;
var cases = new (string Name, ControlField[] Fields)[]
{
    ("empty", []), ("ascending-1", Create(1)), ("ascending-4", Create(4)),
    ("ascending-5", Create(5)), ("ascending-8", Create(8)), ("ascending-32", Create(32)),
    ("reverse-5", Create(5).Reverse().ToArray()),
    ("mixed-5", Permute(Create(5))),
};
var measured = new Reading[cases.Length];
var golden = new byte[cases.Length][];
var originalOrder = cases.Select(c => c.Fields.ToArray()).ToArray();
for (int index = 0; index < cases.Length; ++index)
{
    var fields = cases[index].Fields;
    golden[index] = ControlRecord.Encode(13, fields);
    long warmChecksum = 0;
    for (int i = 0; i < Warmup; ++i) warmChecksum += Consume(ControlRecord.Encode(13, fields));
    if (warmChecksum != Consume(golden[index]) * Warmup) throw new InvalidOperationException("Warmup bytes differ.");
    int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
    long before = GC.GetTotalAllocatedBytes(true), started = Stopwatch.GetTimestamp(), checksum = 0;
    for (int i = 0; i < Operations; ++i) checksum += Consume(ControlRecord.Encode(13, fields));
    long ended = Stopwatch.GetTimestamp(), after = GC.GetTotalAllocatedBytes(true);
    measured[index] = new(before, after, gc0, gc1, gc2, GC.CollectionCount(0), GC.CollectionCount(1),
        GC.CollectionCount(2), started, ended, checksum);
    if (checksum != Consume(golden[index]) * Operations) throw new InvalidOperationException("Measured bytes differ.");
    for (int i = 0; i < fields.Length; ++i)
        if (!ReferenceEquals(fields[i], originalOrder[index][i])) throw new InvalidOperationException("Caller array order changed.");
}
// Invalid/cold paths are observations outside the steady allocation intervals.
var invalid = new (string Name, ControlField[]? Fields)[]
{
    ("null-array", null), ("null-field", [null!]), ("null-payload", [new(1, 1, null!)]),
    ("zero-id", [new(0, 1, [1])]), ("duplicate-id", [new(1, 1, [1]), new(1, 2, [2])]),
    ("duplicate-before-null", [new(1, 1, [1]), new(1, 2, [2]), null!]),
    ("zero-before-null", [new(0, 1, [1]), null!]),
};
var exceptions = new (string Type, string Message)[invalid.Length];
for (int i = 0; i < invalid.Length; ++i)
{
    try { _ = ControlRecord.Encode(13, invalid[i].Fields!); throw new InvalidOperationException("Invalid input unexpectedly accepted."); }
    catch (Exception failure) { exceptions[i] = (failure.GetType().FullName!, failure.Message); }
}
using var output = File.Create(args[0]);
using var json = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
json.WriteStartObject();
json.WriteString("task", "DB-Perf-01"); json.WriteString("kind", "isolated-control-record-encoding");
json.WriteString("scope", "Actual internal encoder, precreated fields; includes owned encoded wire byte array and all process allocation; no DTO/list/string business result or SQL operation. This is not a package or end-to-end acceptance measurement.");
json.WriteString("sourceBaseCommit", args[2]); json.WriteString("sourcePath", Path.GetFullPath(args[1]));
json.WriteString("sourceSha256", Hash(args[1])); json.WriteString("executableSha256", Hash(Environment.ProcessPath!));
json.WriteString("runtime", RuntimeInformation.FrameworkDescription); json.WriteString("architecture", RuntimeInformation.ProcessArchitecture.ToString());
json.WriteBoolean("nativeAot", true); json.WriteNumber("processId", Environment.ProcessId);
json.WriteNumber("stopwatchFrequency", Stopwatch.Frequency); json.WriteNumber("warmupPerCase", Warmup);
json.WriteNumber("operationsPerCase", Operations); json.WriteNumber("businessResultAllocationBytes", 0);
json.WriteString("timingScope", "Local isolated encoding diagnosis; no database throughput or formal host qualification");
json.WriteStartArray("cases");
for (int i = 0; i < cases.Length; ++i)
{
    var value = measured[i]; json.WriteStartObject(); json.WriteString("name", cases[i].Name);
    json.WriteNumber("fieldCount", cases[i].Fields.Length); json.WriteNumber("encodedBytes", golden[i].Length);
    json.WriteString("encodedHex", Convert.ToHexStringLower(golden[i]));
    json.WriteNumber("allocatedBefore", value.Before); json.WriteNumber("allocatedAfter", value.After);
    json.WriteNumber("allocatedBytes", value.After - value.Before);
    json.WriteNumber("allocatedBytesPerOperation", (double)(value.After - value.Before) / Operations);
    json.WriteNumber("gc0Before", value.Gc0Before); json.WriteNumber("gc1Before", value.Gc1Before); json.WriteNumber("gc2Before", value.Gc2Before);
    json.WriteNumber("gc0After", value.Gc0After); json.WriteNumber("gc1After", value.Gc1After); json.WriteNumber("gc2After", value.Gc2After);
    json.WriteNumber("startedTicks", value.Started); json.WriteNumber("endedTicks", value.Ended);
    json.WriteNumber("checksum", value.Checksum); json.WriteBoolean("callerArrayUnchanged", true); json.WriteEndObject();
}
json.WriteEndArray(); json.WriteStartArray("exceptions");
for (int i = 0; i < invalid.Length; ++i)
{
    json.WriteStartObject(); json.WriteString("name", invalid[i].Name); json.WriteString("type", exceptions[i].Type);
    json.WriteString("message", exceptions[i].Message); json.WriteEndObject();
}
json.WriteEndArray(); json.WriteEndObject();

static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
static ControlField[] Create(int count)
{
    var fields = new ControlField[count];
    for (int i = 0; i < count; ++i)
    {
        var bytes = new byte[1 + i % 37];
        for (int j = 0; j < bytes.Length; ++j) bytes[j] = (byte)(17 * i + j);
        fields[i] = new((ushort)(i + 1), (ushort)(1 + i % 8), bytes);
    }
    return fields;
}
static ControlField[] Permute(ControlField[] fields) => [fields[2], fields[0], fields[4], fields[1], fields[3]];
[MethodImpl(MethodImplOptions.NoInlining)]
static long Consume(byte[] bytes) { long value = 0; for (int i = 0; i < bytes.Length; ++i) value += bytes[i]; return value; }
internal readonly record struct Reading(long Before, long After, int Gc0Before, int Gc1Before, int Gc2Before,
    int Gc0After, int Gc1After, int Gc2After, long Started, long Ended, long Checksum);
