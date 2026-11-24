using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Korda.Data;
using Korda.Query.Internal;

if (args.Length != 3 || RuntimeFeature.IsDynamicCodeSupported)
    throw new ArgumentException("Native AOT only: <result.json> <encoder-source-file> <source-base-commit>");
const int Warmup = 1024, Operations = 4096;
var cases = new List<Case>();
foreach (var count in new[] { 1, 4, 8, 32, 128 })
{
    var fields = new ResultField[count];
    for (var i = 0; i < count; i++)
    {
        var name = i % 2 == 0 ? "field_" + i : "订单_" + i + "_🦀";
        fields[i] = (i % 4) switch
        {
            0 => ResultField.Create(name, Codecs.Int64),
            1 => ResultField.Create(name, Codecs.NullableReference(Codecs.String)),
            2 => ResultField.Create(name, Codecs.Decimal(18, 2)),
            _ => ResultField.Create(name, Codecs.Boolean),
        };
    }
    var projection = Projection<long>.Create(fields, static _ => 0L);
    cases.Add(new("fields-" + count + "-metadata", projection, []));
    var expressions = new ulong[count];
    for (var i = 0; i < count; i++) expressions[i] = 0x100000001UL + (uint)i;
    cases.Add(new("fields-" + count + "-expressions", projection, expressions));
}
Func<CodecDescriptor, uint> bind = static descriptor => descriptor.Id;
var readings = new Reading[cases.Count];
var encoded = new byte[cases.Count][];
var fixtureBefore = new string[cases.Count];
var fixtureAfter = new string[cases.Count];
var bindingOrders = new uint[cases.Count][];
using var process = Process.GetCurrentProcess();
for (var index = 0; index < cases.Count; index++)
{
    var item = cases[index];
    fixtureBefore[index] = Fingerprint(item);
    encoded[index] = OperandEncoding.Projection(item.Projection, bind, item.Expressions);
    // Binder observations and golden creation are outside every allocation window.
    var order = new List<uint>();
    var ordered = OperandEncoding.Projection(item.Projection, descriptor =>
    {
        order.Add(descriptor.Id);
        return descriptor.Id;
    }, item.Expressions);
    bindingOrders[index] = order.ToArray();
    if (!ordered.AsSpan().SequenceEqual(encoded[index]) || order.Count != item.Projection.Fields.Length)
        throw new InvalidOperationException("Golden bytes or binder count changed.");
    for (var i = 0; i < order.Count; i++)
        if (order[i] != item.Projection.Fields[i].CodecId)
            throw new InvalidOperationException("Binder visitation order differs from the ordered fields.");
    var expected = Consume(encoded[index], encoded[index]);
    long warmChecksum = 0;
    for (var i = 0; i < Warmup; i++) warmChecksum += EncodeAndConsume(item, bind, encoded[index]);
    if (warmChecksum != expected * Warmup) throw new InvalidOperationException("Warmup bytes changed.");
    _ = Stopwatch.GetTimestamp(); _ = GC.GetTotalAllocatedBytes(true);
    process.Refresh();
    var cpuUserBefore = process.UserProcessorTime.TotalSeconds;
    var cpuSystemBefore = process.PrivilegedProcessorTime.TotalSeconds;
    var gc0 = GC.CollectionCount(0); var gc1 = GC.CollectionCount(1); var gc2 = GC.CollectionCount(2);
    var before = GC.GetTotalAllocatedBytes(true); var started = Stopwatch.GetTimestamp();
    long checksum = 0;
    for (var i = 0; i < Operations; i++) checksum += EncodeAndConsume(item, bind, encoded[index]);
    var ended = Stopwatch.GetTimestamp(); var after = GC.GetTotalAllocatedBytes(true);
    var after0 = GC.CollectionCount(0); var after1 = GC.CollectionCount(1); var after2 = GC.CollectionCount(2);
    process.Refresh();
    var cpuUserAfter = process.UserProcessorTime.TotalSeconds;
    var cpuSystemAfter = process.PrivilegedProcessorTime.TotalSeconds;
    readings[index] = new(before, after, gc0, gc1, gc2, after0, after1, after2, started, ended, checksum,
        cpuUserBefore, cpuSystemBefore, cpuUserAfter, cpuSystemAfter);
    if (checksum != expected * Operations) throw new InvalidOperationException("Measured bytes changed.");
    fixtureAfter[index] = Fingerprint(item);
    if (fixtureAfter[index] != fixtureBefore[index]) throw new InvalidOperationException("Projection or expression input was mutated.");
}
// Error and binding-order observations are outside all allocation windows.
var invalid = new List<(string Name, string Type, string Message, uint[] BindingOrder)>();
void Observe(string name, Action action, List<uint>? order = null)
{
    Exception? observed = null;
    try { action(); }
    catch (Exception error) { observed = error; }
    if (observed is null) throw new InvalidOperationException("Invalid fixture unexpectedly succeeded: " + name);
    invalid.Add((name, observed.GetType().FullName!, observed.Message, order?.ToArray() ?? []));
}
Observe("null-projection", () => _ = OperandEncoding.Projection<long>(null!, bind));
Observe("null-binder", () => _ = OperandEncoding.Projection(cases[0].Projection, null!));
Observe("expression-length", () => _ = OperandEncoding.Projection(cases[2].Projection, bind, new ulong[1]));
var bindingOrder = new List<uint>();
Observe("binding-failure-third-field", () => _ = OperandEncoding.Projection(cases[2].Projection, descriptor =>
{
    bindingOrder.Add(descriptor.Id);
    if (bindingOrder.Count == 3) throw new ApplicationException("fixture bind failure");
    return descriptor.Id;
}), bindingOrder);

using var output = File.Create(args[0]);
using var json = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
json.WriteStartObject();
#if PROJECTION_SCRATCH
json.WriteString("callerEncodingMode", "bounded-stack-scratch-4096");
#else
json.WriteString("callerEncodingMode", "owned-array-baseline");
#endif
json.WriteString("task", "DB-Perf-01"); json.WriteString("kind", "isolated-projection-scratch-callsite");
json.WriteString("scope", "Actual internal projection encoder with prebuilt immutable projection, expression IDs and codec binder. Includes every encoded wire array and temporary object in whole-process managed allocation. No SQL, DTO/list/string business result, package-consumer or formal throughput acceptance.");
json.WriteString("sourceBaseCommit", args[2]); json.WriteString("sourcePath", Path.GetFullPath(args[1]));
json.WriteString("sourceSha256", Hash(args[1])); json.WriteString("executableSha256", Hash(Environment.ProcessPath!));
json.WriteString("runtime", RuntimeInformation.FrameworkDescription); json.WriteString("architecture", RuntimeInformation.ProcessArchitecture.ToString());
json.WriteNumber("processId", Environment.ProcessId); json.WriteBoolean("nativeAot", true);
json.WriteString("processStartedUtc", process.StartTime.ToUniversalTime().ToString("O"));
json.WriteNumber("warmupPerCase", Warmup); json.WriteNumber("operationsPerCase", Operations);
json.WriteNumber("stopwatchFrequency", Stopwatch.Frequency); json.WriteNumber("businessResultAllocatedBytes", 0);
json.WriteString("gcScope", "Whole shared process heap; GC counts are not assigned to business/framework categories.");
json.WriteString("cpuScope", "Raw process user/system endpoints bracket the allocation and GC counters; duration covers the encode, full byte comparison and consumption loop. This shared-host microprobe does not establish stable CPU or latency performance.");
json.WriteString("binderScope", "Prebuilt static delegate returns each logical descriptor Id, including configured decimal; this isolates encoding and does not register source-bound native codec IDs.");
json.WriteStartArray("cases");
for (var i = 0; i < cases.Count; i++)
{
    var item = cases[i]; var value = readings[i];
    json.WriteStartObject(); json.WriteString("name", item.Name); json.WriteNumber("fieldCount", item.Projection.Fields.Length);
    json.WriteNumber("expressionCount", item.Expressions.Length); json.WriteNumber("encodedBytes", encoded[i].Length);
    json.WriteString("encodedHex", Convert.ToHexStringLower(encoded[i]));
    json.WriteString("shapeHash", item.Projection.ShapeHash);
    json.WriteString("canonicalMetadataHex", Convert.ToHexStringLower(item.Projection.CanonicalBytes));
    json.WriteString("fixtureSha256Before", fixtureBefore[i]); json.WriteString("fixtureSha256After", fixtureAfter[i]);
    json.WriteBoolean("inputUnchanged", fixtureBefore[i] == fixtureAfter[i]);
    json.WriteStartArray("bindingOrder"); foreach (var id in bindingOrders[i]) json.WriteNumberValue(id); json.WriteEndArray();
    json.WriteNumber("allocatedBefore", value.Before); json.WriteNumber("allocatedAfter", value.After);
    json.WriteNumber("allocatedBytes", value.After - value.Before);
    json.WriteNumber("allocatedBytesPerOperation", (double)(value.After - value.Before) / Operations);
    json.WriteNumber("gc0Before", value.Gc0Before); json.WriteNumber("gc1Before", value.Gc1Before); json.WriteNumber("gc2Before", value.Gc2Before);
    json.WriteNumber("gc0After", value.Gc0After); json.WriteNumber("gc1After", value.Gc1After); json.WriteNumber("gc2After", value.Gc2After);
    json.WriteNumber("startedTicks", value.Started); json.WriteNumber("endedTicks", value.Ended);
    json.WriteNumber("userCpuSecondsBefore", value.UserBefore); json.WriteNumber("systemCpuSecondsBefore", value.SystemBefore);
    json.WriteNumber("userCpuSecondsAfter", value.UserAfter); json.WriteNumber("systemCpuSecondsAfter", value.SystemAfter);
    json.WriteNumber("checksum", value.Checksum); json.WriteEndObject();
}
json.WriteEndArray(); json.WriteStartArray("exceptions");
foreach (var value in invalid)
{
    json.WriteStartObject(); json.WriteString("name", value.Name); json.WriteString("type", value.Type);
    json.WriteString("message", value.Message); json.WriteStartArray("bindingOrder");
    foreach (var id in value.BindingOrder) json.WriteNumberValue(id);
    json.WriteEndArray(); json.WriteEndObject();
}
json.WriteEndArray(); json.WriteEndObject();

[MethodImpl(MethodImplOptions.NoInlining)]
static long EncodeAndConsume(Case item, Func<CodecDescriptor, uint> bind, byte[] expected)
{
#if PROJECTION_SCRATCH
    Span<byte> scratch = stackalloc byte[4096];
    return Consume(OperandEncoding.ProjectionInto(item.Projection, bind, scratch, item.Expressions), expected);
#else
    return Consume(OperandEncoding.Projection(item.Projection, bind, item.Expressions), expected);
#endif
}

static string Hash(string path) { using var input = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(input)); }
static string Fingerprint(Case item)
{
    using var bytes = new MemoryStream();
    using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, leaveOpen: true))
    {
        writer.Write(item.Projection.Fields.Length);
        foreach (var field in item.Projection.Fields)
        {
            writer.Write(field.Name); writer.Write(field.Descriptor.Id); writer.Write(field.Descriptor.Name);
            writer.Write(field.Descriptor.Nullable); writer.Write(field.Descriptor.Configuration is not null);
            if (field.Descriptor.Configuration is { } configuration) writer.Write(configuration);
        }
        writer.Write(item.Projection.ShapeHash); writer.Write(item.Projection.CanonicalBytes.Length);
        writer.Write(item.Projection.CanonicalBytes); writer.Write(item.Expressions.Length);
        foreach (var expression in item.Expressions) writer.Write(expression);
    }
    return Convert.ToHexStringLower(SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length))));
}
[MethodImpl(MethodImplOptions.NoInlining)]
static long Consume(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> expected)
{
    if (!bytes.SequenceEqual(expected)) throw new InvalidOperationException("Complete wire bytes changed.");
    long sum = 0; foreach (var value in bytes) sum += value; return sum;
}
internal sealed record Case(string Name, Projection<long> Projection, ulong[] Expressions);
internal readonly record struct Reading(long Before, long After, int Gc0Before, int Gc1Before, int Gc2Before,
    int Gc0After, int Gc1After, int Gc2After, long Started, long Ended, long Checksum,
    double UserBefore, double SystemBefore, double UserAfter, double SystemAfter);
