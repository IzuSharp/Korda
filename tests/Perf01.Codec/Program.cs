using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using NativeWeave.Engines.SyncProof;
using NativeWeave.Engines.SyncProof.Generated;
using NativeWeave.Interop;

// Test-only process: the outside controller owns arrival clocks, offered load,
// deadlines, sample persistence and resource observation. No application API changes.
internal static class Program
{
    private const int RawLimit = 4 * 1024 * 1024;
    private const int EncodedLimit = RawLimit + 65536;
    private static readonly object OutputGate = new();
    private static readonly ZlibCodec Codec = new(RawLimit, EncodedLimit, RawLimit);
    private static readonly ZLibCompressionOptions ManagedOptions = new() { CompressionLevel = 6, CompressionStrategy = ZLibCompressionStrategy.Default };
    private static bool Managed;
    private static Corpus[] Cases = [];
    private static long Errors;

    private sealed class Corpus
    {
        internal readonly byte[] Raw;
        internal readonly byte[] Encoded;
        internal readonly NativeBufferReader<bool> MatchRaw;
        internal readonly NativeBufferReader<bool> MatchEncoded;
        internal Corpus(byte[] raw, byte[] encoded)
        {
            Raw = raw; Encoded = encoded;
            MatchRaw = bytes => bytes.SequenceEqual(Raw);
            MatchEncoded = bytes => bytes.SequenceEqual(Encoded);
        }
    }
    private readonly record struct Work(long Id, int Case, bool Encode, bool Owned);

    private static int Main(string[] args)
    {
        try
        {
            string Value(string key) => args[Array.IndexOf(args, key) + 1];
            if (args.Contains("--require-aot") && RuntimeFeature.IsDynamicCodeSupported)
                throw new InvalidOperationException("Actual Native AOT execution required.");
            Managed = Value("--route") == "managed-zlib";
            int workers = int.Parse(Value("--workers"), CultureInfo.InvariantCulture);
            int capacity = int.Parse(Value("--queue"), CultureInfo.InvariantCulture);
            if (workers is < 1 or > 256 || capacity < 1) throw new ArgumentOutOfRangeException(nameof(args));
            string directory = Value("--corpus");
            Cases = Enumerable.Range(0, 8).Select(i => new Corpus(
                File.ReadAllBytes(Path.Combine(directory, i + ".raw")),
                File.ReadAllBytes(Path.Combine(directory, i + ".zlib")))).ToArray();
            Preflight();
            if (args.Contains("--check-only")) { Emit("{\"kind\":\"functional_check_passed\"}"); return 0; }
            using var queue = new BlockingCollection<Work>(capacity);
            Thread[] threads = Enumerable.Range(0, workers).Select(i => new Thread(() => Worker(queue)) { Name = "perf01-codec-" + i }).ToArray();
            foreach (Thread thread in threads) thread.Start();
            using var telemetry = new Timer(_ => Snapshot("telemetry"), null, 1000, 1000);
            Emit($"{{\"kind\":\"ready\",\"dynamic_code_supported\":{RuntimeFeature.IsDynamicCodeSupported.ToString().ToLowerInvariant()},\"workers\":{workers},\"queue_capacity\":{capacity},\"strict_single_stream_comparable\":{(!Managed).ToString().ToLowerInvariant()}}}");
            string? line;
            while ((line = Console.ReadLine()) is not null)
            {
                if (line == "snapshot") { Snapshot("snapshot"); continue; }
                if (line == "drain")
                {
                    queue.CompleteAdding();
                    foreach (Thread thread in threads) thread.Join();
                    Snapshot("drained");
                    // Keep the target resident for the controller's separate idle observation.
                    while ((line = Console.ReadLine()) is not null)
                        if (line == "snapshot") Snapshot("idle");
                    return Errors == 0 ? 0 : 1;
                }
                string[] fields = line.Split('\t');
                if (fields.Length != 4) throw new InvalidDataException("Invalid work frame.");
                Work work = new(long.Parse(fields[0], CultureInfo.InvariantCulture), int.Parse(fields[1], CultureInfo.InvariantCulture), fields[2] == "c", fields[3] == "o");
                if (work.Case is < 0 or >= 8 || fields[2] is not ("c" or "d") || fields[3] is not ("a" or "o"))
                    throw new InvalidDataException("Unknown corpus/action/interface.");
                if (!queue.TryAdd(work)) Emit($"{{\"kind\":\"completed\",\"id\":{work.Id},\"status\":\"rejected\"}}");
            }
            queue.CompleteAdding();
            foreach (Thread thread in threads) thread.Join();
            Snapshot("drained");
            return Errors == 0 ? 0 : 1;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.GetType().Name + ": " + error.Message);
            return 1;
        }
    }

    private static void Preflight()
    {
        foreach (Corpus item in Cases)
        {
            if (item.Raw.Length > RawLimit || item.Encoded.Length > EncodedLimit) throw new InvalidDataException("Corpus exceeds frozen limits.");
            if (Managed)
            {
                if (!ManagedDecode(item.Encoded, RawLimit).AsSpan().SequenceEqual(item.Raw) ||
                    !ManagedDecode(ManagedEncode(item.Raw), RawLimit).AsSpan().SequenceEqual(item.Raw))
                    throw new InvalidDataException("Managed round-trip failed.");
            }
            else
            {
                if (!Codec.Compress(item.Raw).AsSpan().SequenceEqual(item.Encoded) || !Codec.Decompress(item.Encoded).AsSpan().SequenceEqual(item.Raw))
                    throw new InvalidDataException("Native frozen corpus oracle failed.");
                using NativeBuffer owned = Codec.DecompressOwned(item.Encoded);
                if (!owned.Read(bytes => bytes.SequenceEqual(item.Raw))) throw new InvalidDataException("Native owned oracle failed.");
            }
        }
        byte[] valid = Managed ? ManagedEncode("limit"u8) : Codec.Compress("limit"u8);
        if (Managed)
        {
            MustFail(() => ManagedDecode(valid, 1));
            MustFail(() => ManagedEncode(new byte[RawLimit + 1]));
            MustFail(() => ManagedDecode(new byte[EncodedLimit + 1], RawLimit));
            byte[] corrupt = (byte[])valid.Clone(); corrupt[^1] ^= 1;
            MustFail(() => ManagedDecode(corrupt, RawLimit));
            // ZLibStream does not expose exact consumed-input position after buffered reads.
            // This route is explicitly not comparable for strict trailing/concatenation rules.
        }
        else
        {
            MustFail(() => new ZlibCodec(1, 1024, 1).Decompress(valid));
            MustFail(() => Codec.Compress(new byte[RawLimit + 1]));
            MustFail(() => Codec.Decompress(new byte[EncodedLimit + 1]));
            for (int size = 0; size < valid.Length; size++)
            {
                int end = size; MustFail(() => Codec.Decompress(valid.AsSpan(0, end)));
            }
            byte[] corrupt = (byte[])valid.Clone(); corrupt[^1] ^= 1;
            MustFail(() => Codec.Decompress(corrupt));
            MustFail(() => Codec.Decompress([.. valid, 0]));
            MustFail(() => Codec.Decompress([.. valid, .. valid]));
            NativeBuffer lease = Codec.CompressOwned("lease"u8);
            lease.Read(bytes => { lease.Dispose(); if (bytes.Length == 0) throw new InvalidDataException("Lost admitted lease."); });
            lease.Dispose();
            MustFail(() => lease.ToArray());
            var counters = NativeCounters();
            if (counters.Live != 0 || counters.Capacity != 0 || counters.Active != 0) throw new InvalidDataException("Preflight owners did not retire.");
        }
    }

    private static void MustFail(Action action)
    {
        bool failed = false;
        try { action(); }
        catch (Exception error) when (error is NativeEngineException or ArgumentOutOfRangeException or InvalidDataException or ObjectDisposedException) { failed = true; }
        if (!failed) throw new InvalidDataException("Negative codec oracle unexpectedly succeeded.");
    }

    private static void Worker(BlockingCollection<Work> queue)
    {
        foreach (Work work in queue.GetConsumingEnumerable())
        {
            Emit($"{{\"kind\":\"admitted\",\"id\":{work.Id}}}");
            Corpus item = Cases[work.Case];
            byte[] input = work.Encode ? item.Raw : item.Encoded;
            byte[] expected = work.Encode ? item.Encoded : item.Raw;
            long start = Stopwatch.GetTimestamp();
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            long call = start, consumed = start, end = start, copied = 0, callAllocation = 0;
            int length = 0;
            string status = "ok";
            try
            {
                if (Managed)
                {
                    byte[] result = work.Encode ? ManagedEncode(input) : ManagedDecode(input, RawLimit);
                    call = Stopwatch.GetTimestamp(); callAllocation = GC.GetAllocatedBytesForCurrentThread() - allocated; length = result.Length;
                    bool valid = work.Encode ? ManagedDecode(result, RawLimit).AsSpan().SequenceEqual(item.Raw) : result.AsSpan().SequenceEqual(item.Raw);
                    if (!valid) throw new InvalidDataException("Managed payload oracle failed.");
                    consumed = end = Stopwatch.GetTimestamp();
                }
                else if (work.Owned)
                {
                    NativeBuffer result = work.Encode ? Codec.CompressOwned(input) : Codec.DecompressOwned(input);
                    call = Stopwatch.GetTimestamp(); callAllocation = GC.GetAllocatedBytesForCurrentThread() - allocated; length = result.Length;
                    try
                    {
                        if (!result.Read(work.Encode ? item.MatchEncoded : item.MatchRaw)) throw new InvalidDataException("Owned payload oracle failed.");
                        consumed = Stopwatch.GetTimestamp();
                    }
                    finally { result.Dispose(); end = Stopwatch.GetTimestamp(); }
                }
                else
                {
                    byte[] result = work.Encode ? Codec.Compress(input) : Codec.Decompress(input);
                    call = Stopwatch.GetTimestamp(); callAllocation = GC.GetAllocatedBytesForCurrentThread() - allocated; length = result.Length; copied = length;
                    if (!result.AsSpan().SequenceEqual(expected)) throw new InvalidDataException("Array payload oracle failed.");
                    consumed = end = Stopwatch.GetTimestamp();
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                Interlocked.Increment(ref Errors); status = "failed"; end = Stopwatch.GetTimestamp();
            }
            long allocation = GC.GetAllocatedBytesForCurrentThread() - allocated;
            // Observation fields are generated after the allocation and codec timing interval.
            Emit($"{{\"kind\":\"completed\",\"id\":{work.Id},\"status\":\"{status}\",\"raw_bytes\":{item.Raw.Length},\"encoded_bytes\":{(work.Encode ? length : item.Encoded.Length)},\"call_ns\":{Ns(call-start)},\"consume_ns\":{Ns(consumed-call)},\"release_ns\":{Ns(end-consumed)},\"operation_ns\":{Ns(end-start)},\"managed_allocated_bytes\":{callAllocation},\"managed_allocated_bytes_including_validation\":{allocation},\"copied_bytes_derived\":{(Managed ? "null" : copied.ToString(CultureInfo.InvariantCulture))},\"ffi_calls_derived\":{(Managed ? "null" : "2")}}}");
        }
    }

    private static long Ns(long ticks) => (long)(ticks * (1_000_000_000.0 / Stopwatch.Frequency));
    private static void Emit(string text) { lock (OutputGate) Console.WriteLine(text); }

    private static unsafe (ulong Live, ulong Released, ulong Capacity, ulong Active) NativeCounters()
    {
        ulong live = 0, released = 0, capacity = 0, active = 0;
        if (NativeMethods.nw_syncproof_counters_v1(&live, &released, &capacity, &active) != 0) throw new InvalidDataException("Counter ABI failed.");
        return (live, released, capacity, active);
    }

    private static void Snapshot(string kind)
    {
        string native = "null";
        if (!Managed)
        {
            var value = NativeCounters();
            native = $"{{\"live\":{value.Live},\"released\":{value.Released},\"capacity\":{value.Capacity},\"active\":{value.Active}}}";
        }
        var info = GC.GetGCMemoryInfo();
        Emit($"{{\"kind\":\"{kind}\",\"native\":{native},\"managed_allocated_bytes_total\":{GC.GetTotalAllocatedBytes(false)},\"gc0\":{GC.CollectionCount(0)},\"gc1\":{GC.CollectionCount(1)},\"gc2\":{GC.CollectionCount(2)},\"gc_heap_bytes\":{info.HeapSizeBytes},\"gc_committed_bytes\":{info.TotalCommittedBytes},\"errors\":{Interlocked.Read(ref Errors)}}}");
    }

    private static byte[] ManagedEncode(ReadOnlySpan<byte> input)
    {
        if (input.Length > RawLimit) throw new ArgumentOutOfRangeException(nameof(input));
        using var output = new BoundedOutput(Math.Max(8, input.Length / 2), EncodedLimit);
        using (var stream = new ZLibStream(output, ManagedOptions, leaveOpen: true)) stream.Write(input);
        return output.ToArray();
    }

    private static byte[] ManagedDecode(byte[] input, int limit)
    {
        if (input.Length > EncodedLimit) throw new ArgumentOutOfRangeException(nameof(input));
        using var source = new MemoryStream(input, writable: false);
        using var stream = new ZLibStream(source, CompressionMode.Decompress);
        using var output = new BoundedOutput(Math.Min(input.Length * 2, limit), limit);
        byte[] chunk = new byte[65536];
        int count;
        while ((count = stream.Read(chunk)) != 0) output.Write(chunk.AsSpan(0, count));
        return output.ToArray();
    }

    private sealed class BoundedOutput(int initial, int limit) : Stream
    {
        private byte[] _buffer = new byte[Math.Min(initial, limit)];
        private int _length;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length > limit - _length) throw new InvalidDataException("Frozen output limit exceeded.");
            int required = _length + buffer.Length;
            if (required > _buffer.Length) Array.Resize(ref _buffer, Math.Min(limit, Math.Max(required, Math.Max(8, _buffer.Length * 2))));
            buffer.CopyTo(_buffer.AsSpan(_length)); _length = required;
        }
        internal byte[] ToArray() => _buffer.AsSpan(0, _length).ToArray();
    }
}
