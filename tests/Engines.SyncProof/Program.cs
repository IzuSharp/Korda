using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NativeWeave.Engines.SyncProof;
using NativeWeave.Engines.SyncProof.Generated;
using NativeWeave.Interop;

if (args.Contains("--require-aot") && RuntimeFeature.IsDynamicCodeSupported) throw new Exception("Actual Native AOT execution required.");
string? directCorpus = args.FirstOrDefault(static arg => arg.StartsWith("--direct-corpus=", StringComparison.Ordinal))?[16..];
int assertions = 0;
void Check(bool condition, string label) { if (!condition) throw new Exception(label); assertions++; }
void Throws<T>(Action body, string label) where T : Exception
{
    try { body(); throw new Exception("Did not throw: " + label); }
    catch (T) { assertions++; }
}
void Code(Action body, int code)
{
    try { body(); throw new Exception("Missing native domain failure " + code); }
    catch (NativeEngineException e) { Check(e.StatusCode == 13 && e.ErrorDomain == 0x53594e43 && e.NativeCode == code, "native status/code retained"); }
}
byte[] RandomBytes(int length)
{
    uint seed = 0x9e3779b9;
    byte[] bytes = new byte[length];
    for (int i = 0; i < length; i++) { seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5; bytes[i] = (byte)seed; }
    return bytes;
}
byte[] ManagedCompress(byte[] input)
{
    using var output = new MemoryStream();
    using (var stream = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true)) stream.Write(input);
    return output.ToArray();
}
byte[] ManagedDecode(byte[] input)
{
    using var source = new MemoryStream(input, writable: false);
    using var stream = new ZLibStream(source, CompressionMode.Decompress);
    using var output = new MemoryStream();
    stream.CopyTo(output); return output.ToArray();
}
unsafe (ulong Live, ulong Released, ulong Capacity, ulong Active) Counters()
{
    ulong live = 0, released = 0, capacity = 0, active = 0;
    Check(NativeMethods.nw_syncproof_counters_v1(&live, &released, &capacity, &active) == 0, "native counters");
    return (live, released, capacity, active);
}

var codec = new ZlibCodec();
EngineInfo engine = ZlibCodec.Descriptor.Inspect();
Check(engine.ThreadingModel == EngineThreadingModel.Synchronous, "synchronous capability");
Check(engine.Capabilities.Count == 1 && engine.Supports(new("codec.zlib.v1")), "no callback/executor capability");
Check(Counters().Live == 0, "initial owner count");
foreach (int size in new[] { 0, 1, 7, 31, 256, 4096, 65536, codec.RawInputLimit })
{
    byte[] input = RandomBytes(size);
    byte[] compressed = codec.Compress(input);
    if (directCorpus is not null) Check(compressed.SequenceEqual(File.ReadAllBytes(Path.Combine(directCorpus, size + ".zlib"))), "direct Rust versus C# encoded identity");
    Check(codec.Decompress(compressed).SequenceEqual(input), "native round-trip");
    Check(ManagedDecode(compressed).SequenceEqual(input), "native to ZLibStream");
    Check(codec.Decompress(ManagedCompress(input)).SequenceEqual(input), "ZLibStream to native");
    using NativeBuffer owned = codec.CompressOwned(input);
    Check(owned.Read(static bytes => bytes.Length) == compressed.Length, "scoped bytes");
    byte[] copy = new byte[owned.Length]; owned.CopyTo(copy);
    Check(copy.SequenceEqual(compressed), "owned bytes copy");
}
Check(Counters().Live == 0 && Counters().Capacity == 0, "round-trip retire");
foreach (int cap in new[] { 0, 1, 7, 8, 31, 256, 4096, 65536, codec.RawInputLimit, ZlibCodec.MaximumProfileBytes })
{
    var bounded = new ZlibCodec(cap, cap, cap);
    byte[] input = RandomBytes(cap);
    try
    {
        using NativeBuffer output = bounded.CompressOwned(input);
        Check(bounded.Decompress(output.ToArray()).SequenceEqual(input), "same-profile success round-trips");
    }
    catch (NativeEngineException e) { Check(e.StatusCode == 13 && e.NativeCode == 5, "expansion fails before publication"); }
    Check(Counters().Live == 0 && Counters().Capacity == 0, "expansion owner retirement");
}
Code(() => codec.Decompress(ManagedCompress(new byte[codec.DecodedOutputLimit + 1])), 4);
Code(() => codec.Decompress(RandomBytes(codec.CompressedInputLimit)), 1);
Throws<ArgumentOutOfRangeException>(() => codec.Decompress(new byte[codec.CompressedInputLimit + 1]), "default encoded input cap");
var maximum = new ZlibCodec(ZlibCodec.MaximumProfileBytes, ZlibCodec.MaximumProfileBytes, ZlibCodec.MaximumProfileBytes);
byte[] maximumDecoded = new byte[ZlibCodec.MaximumProfileBytes];
Check(maximum.Decompress(ManagedCompress(maximumDecoded)).SequenceEqual(maximumDecoded), "exact maximum decoded output");
Code(() => maximum.Decompress(ManagedCompress(new byte[ZlibCodec.MaximumProfileBytes + 1])), 4);
Code(() => maximum.Decompress(RandomBytes(ZlibCodec.MaximumProfileBytes)), 1);
Throws<ArgumentOutOfRangeException>(() => maximum.Compress(new byte[ZlibCodec.MaximumProfileBytes + 1]), "absolute raw input cap");
Throws<ArgumentOutOfRangeException>(() => maximum.Decompress(new byte[ZlibCodec.MaximumProfileBytes + 1]), "absolute encoded input cap");
var small = new ZlibCodec(16, 1024, 16);
Code(() => small.Decompress(ManagedCompress(new byte[17])), 4);
Throws<ArgumentOutOfRangeException>(() => small.Compress(new byte[17]), "raw input cap");
Throws<ArgumentOutOfRangeException>(() => small.Decompress(new byte[1025]), "encoded input cap");
Throws<ArgumentOutOfRangeException>(() => new ZlibCodec(17, 32, 16), "raw cannot exceed decoded profile");
Throws<ArgumentOutOfRangeException>(() => new ZlibCodec(-1), "negative limit");
Throws<ArgumentOutOfRangeException>(() => new ZlibCodec(ZlibCodec.MaximumProfileBytes + 1), "absolute limit");
byte[] valid = small.Compress("payload"u8);
for (int length = 0; length < valid.Length; length++) { int take = length; Code(() => small.Decompress(valid.AsSpan(0, take)), 1); }
byte[] corrupt = (byte[])valid.Clone(); corrupt[^1] ^= 1; Code(() => small.Decompress(corrupt), 1);
Code(() => small.Decompress([.. valid, 0]), 1);
Check(Counters().Live == 0 && Counters().Capacity == 0, "malformed partial output dropped");

// The failure point is after real native success with a non-null owner and before
// SafeHandle/NativeBuffer construction commits. Each JIT/AOT run observes release.
var beforeFailure = Counters();
CodecConstructionProbe.BeforeManagedAdoption = static () => throw new InvalidOperationException("deterministic wrapper construction failure");
try { Throws<InvalidOperationException>(() => codec.CompressOwned("construct"u8), "wrapper exception"); }
finally { CodecConstructionProbe.BeforeManagedAdoption = null; }
var afterFailure = Counters();
Check(afterFailure.Live == 0 && afterFailure.Capacity == 0 && afterFailure.Released == beforeFailure.Released + 1, "raw owner released exactly once on wrapper failure");
var beforeSuccess = Counters();
NativeBuffer success = codec.CompressOwned("successful"u8);
Check(Counters().Live == 1, "successful owner remains live");
success.Dispose(); success.Dispose();
Check(Counters().Released == beforeSuccess.Released + 1 && Counters().Live == 0, "single SafeHandle release on repeated dispose");
Throws<ObjectDisposedException>(() => success.ToArray(), "closed buffer rejects reads");
NativeBuffer leased = codec.DecompressOwned(codec.Compress("lease"u8));
leased.Read(bytes => { leased.Dispose(); Check(bytes.SequenceEqual("lease"u8), "admitted read outlives disposal"); Check(Counters().Live == 1, "physical owner held by read lease"); });
Check(Counters().Live == 0, "lease retirement releases owner");

Parallel.For(0, 32, i =>
{
    byte[] bytes = RandomBytes(i * 97);
    using NativeBuffer output = codec.CompressOwned(bytes);
    if (!codec.Decompress(output.ToArray()).SequenceEqual(bytes)) throw new Exception("Concurrent codec mismatch.");
});
var final = Counters();
Check(final.Live == 0 && final.Capacity == 0 && final.Active == 0, "all native owners and admissions retired");
Console.WriteLine($"PASS A17 Linux {(RuntimeFeature.IsDynamicCodeSupported ? "JIT" : "NativeAOT")} {assertions} assertions; releases={final.Released}; live={final.Live}; capacity={final.Capacity}; active={final.Active}");
