using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Korda.Data;
using Korda.Database.Internal;

// One isolated regression boundary: batch acquisition and its first native ->
// managed copy have already occurred. Both lanes parse, consume every numeric
// row, and dispose. Each raw total includes validation/lease/framework work.
// The copy-only control identifies the removed byte[]; it is never subtracted
// to declare the owned path allocation-free or used as public-provider evidence.
internal static class OwnedBatchProbe
{
    private const int Rows = 128, Operations = 64, Rounds = 3, Warmup = 1024;
    // The measured normal numeric path is 256 B/batch after ordered-range
    // validation. A 512 B ceiling rejects the former 2,448 B range-list path;
    // it is a bounded-allocation regression, never a zero-byte assertion.
    private const long OwnedAllocatedBytesLimitPerBatch = 512;
    private static long s_sink;
    private static int s_assertions;
    private static readonly Projection<long> Shape = Projection<long>.Create(
        [ResultField.Create("id", Codecs.Int64)], static row => row.Read(0, Codecs.Int64));
    private static readonly WireExpectation Expected = new(PayloadKind.Rows, 2,
        Convert.FromHexString(Shape.ShapeHash), 0, 71, [new(0, Codecs.Int64.Descriptor, 3)]);
    private static readonly StreamLimits Limits = new(Rows, 8192, Rows * 2, 16 * 1024);

    private readonly record struct Counter(long Bytes, long CurrentThreadBytes, int Gen0, int Gen1, int Gen2);
    private readonly record struct Window(string Lane, int Round, Counter Before, Counter After,
        long Operations, long Rows, long Checksum);

    internal static void Run(string output)
    {
        var payload = Encode(Rows);
        Correctness(payload);
        _ = Consume(Prepare(payload, Warmup, owned: false), 0, "warmup-borrowed");
        _ = Consume(Prepare(payload, Warmup, owned: true), 0, "warmup-owned");
        _ = CopyOnly(payload, Warmup, 0);
        _ = Capture();
        var windows = new Window[Rounds * 3];
        bool passed = true;
        for (int round = 0; round < Rounds; round++)
        {
            // Every owner/input array is ready before either measured boundary.
            // Ownership transfer is single-use; no measurement reuses a batch.
            var borrowed = Prepare(payload, Operations, owned: false);
            var owned = Prepare(payload, Operations, owned: true);
            Window baseline, after;
            if ((round & 1) == 0)
            {
                baseline = Consume(borrowed, round, "borrowed-defensive-copy");
                after = Consume(owned, round, "exclusive-owned-handoff");
            }
            else
            {
                after = Consume(owned, round, "exclusive-owned-handoff");
                baseline = Consume(borrowed, round, "borrowed-defensive-copy");
            }
            var copy = CopyOnly(payload, Operations, round);
            windows[round * 3] = baseline; windows[round * 3 + 1] = after; windows[round * 3 + 2] = copy;
            AssertReleased(borrowed, takes: 0); AssertReleased(owned, takes: 1);
            long removed = baseline.After.Bytes - baseline.Before.Bytes - (after.After.Bytes - after.Before.Bytes);
            long copyBytes = copy.After.Bytes - copy.Before.Bytes;
            long threadRemoved = baseline.After.CurrentThreadBytes - baseline.Before.CurrentThreadBytes
                - (after.After.CurrentThreadBytes - after.Before.CurrentThreadBytes);
            long threadCopyBytes = copy.After.CurrentThreadBytes - copy.Before.CurrentThreadBytes;
            // A process-wide counter may contain additional runtime work.
            // Preserve it; do not demand or manufacture an exact equality.
            // The ownership assertions establish array identity, the local
            // counter isolates the synchronous lane, and the global raw totals
            // must still show at least one copy saved with a bounded owned lane.
            passed &= removed >= copyBytes && threadRemoved >= threadCopyBytes
                && copyBytes >= payload.LongLength * Operations && threadCopyBytes >= payload.LongLength * Operations
                && after.After.Bytes - after.Before.Bytes <= OwnedAllocatedBytesLimitPerBatch * Operations
                && baseline.Rows == Operations * Rows && after.Rows == baseline.Rows
                && baseline.Checksum == after.Checksum
                && baseline.Checksum == Operations * (long)Rows * (Rows + 1) / 2;
        }

        var path = Path.Combine(output, "owned-batch-probe.json");
        using (var file = File.Create(path))
        using (var json = new Utf8JsonWriter(file, new() { Indented = true }))
        {
            json.WriteStartObject(); json.WriteNumber("schema_version", 1);
            json.WriteString("task", "DB-Perf-01"); json.WriteString("kind", "isolated-owned-batch-regression");
            json.WriteBoolean("passed", passed); json.WriteBoolean("public_provider_acceptance", false);
            json.WriteBoolean("native_aot", !RuntimeFeature.IsDynamicCodeSupported);
            json.WriteString("runtime", RuntimeInformation.FrameworkDescription); json.WriteNumber("process_id", Environment.ProcessId);
            json.WriteString("executable", Environment.ProcessPath);
            if (Environment.ProcessPath is { } executable)
            {
                using var binary = File.OpenRead(executable);
                json.WriteString("executable_sha256", Convert.ToHexStringLower(SHA256.HashData(binary)));
            }
            json.WriteNumber("warmup_operations_per_lane", Warmup); json.WriteNumber("rounds", Rounds);
            json.WriteNumber("operations_per_window", Operations); json.WriteNumber("rows_per_batch", Rows);
            json.WriteNumber("encoded_batch_bytes", payload.Length);
            json.WriteNumber("owned_raw_bytes_limit_per_batch", OwnedAllocatedBytesLimitPerBatch);
            json.WriteString("batch_sha256", Convert.ToHexStringLower(SHA256.HashData(payload)));
            json.WriteNumber("correctness_assertions", s_assertions);
            json.WriteString("boundary", "Precreated exclusive or borrowed managed input and batch owner; RowBatchLease construction, all 128 numeric row reads and owner disposal are measured. Native fetch and its first managed copy are excluded. All process threads count via GC.GetTotalAllocatedBytes(true).");
            json.WriteString("attribution", "Raw lane totals remain nonzero. Identity assertions establish the removed payload array. The gate requires at least one equal-sized copy saved in both whole-process and auxiliary current-thread totals, and a fixed owned-lane budget. Excess difference is unattributed; no control is subtracted to claim zero or attribute public query totals.");
            json.WriteString("ownership", "No array pool or retained payload cache. Each prepared owner is used once; all release counters equal one after the window. Production native owner/byte-permit retention is verified separately by Data.NativeHost actual-provider tests.");
            json.WriteStartArray("windows");
            foreach (var window in windows)
            {
                json.WriteStartObject(); json.WriteString("lane", window.Lane); json.WriteNumber("round", window.Round);
                WriteCounter(json, "before", window.Before); WriteCounter(json, "after", window.After);
                long bytes = window.After.Bytes - window.Before.Bytes;
                json.WriteNumber("allocated_bytes_total", bytes); json.WriteNumber("operations", window.Operations);
                json.WriteNumber("rows", window.Rows); json.WriteNumber("checksum", window.Checksum);
                json.WriteNumber("bytes_per_operation", bytes / (double)window.Operations);
                if (window.Rows > 0) json.WriteNumber("bytes_per_row", bytes / (double)window.Rows); else json.WriteNull("bytes_per_row");
                json.WriteEndObject();
            }
            json.WriteEndArray(); json.WriteEndObject();
        }
        if (!passed) throw new InvalidOperationException("Owned-batch lane exceeded its fixed raw budget or saved less than one same-sized managed copy; inspect every raw window.");
        Console.WriteLine("PASS owned batch: " + s_assertions + " ownership/validation assertions; " + Rounds
            + " raw allocation windows per lane; no full-query zero-allocation claim; " + path);
    }

    private static byte[] Encode(int rows)
    {
        var cells = new ReadOnlyMemory<byte>?[rows];
        for (int i = 0; i < rows; i++) cells[i] = Codecs.Int64.Encode(i + 1L);
        return DbWire.Encode(Expected, checked((uint)rows), cells);
    }

    private static ProbeBatch[] Prepare(byte[] payload, int count, bool owned)
    {
        var batches = new ProbeBatch[count];
        for (int i = 0; i < count; i++)
        {
            var input = (byte[])payload.Clone();
            batches[i] = owned ? new OwnedProbeBatch(input) : new ProbeBatch(input);
        }
        return batches;
    }

    private static Window Consume(ProbeBatch[] batches, int round, string lane)
    {
        long rows = 0, checksum = 0;
        var before = Capture();
        foreach (var owner in batches)
        {
            using var lease = new RowBatchLease(owner, Expected, Shape.Fields, Limits);
            for (uint row = 0; row < lease.Rows; row++) { checksum += lease.Materialize(row, Shape); rows++; }
        }
        Volatile.Write(ref s_sink, checksum);
        var after = Capture();
        return new(lane, round, before, after, batches.Length, rows, checksum);
    }

    private static Window CopyOnly(byte[] payload, int count, int round)
    {
        long checksum = 0;
        var before = Capture();
        for (int i = 0; i < count; i++)
        {
            var copy = payload.AsSpan().ToArray();
            checksum += copy.Length;
            GC.KeepAlive(copy);
        }
        Volatile.Write(ref s_sink, checksum);
        var after = Capture();
        return new("same-sized-byte-array-copy-control", round, before, after, count, 0, checksum);
    }

    private static Counter Capture()
    {
        long threadBytes = GC.GetAllocatedBytesForCurrentThread();
        long bytes = GC.GetTotalAllocatedBytes(true);
        return new(bytes, threadBytes, GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
    }

    private static void WriteCounter(Utf8JsonWriter json, string name, Counter counter)
    {
        json.WriteStartObject(name); json.WriteNumber("allocated_bytes", counter.Bytes);
        json.WriteNumber("current_thread_allocated_bytes", counter.CurrentThreadBytes);
        json.WriteNumber("gc0", counter.Gen0); json.WriteNumber("gc1", counter.Gen1);
        json.WriteNumber("gc2", counter.Gen2); json.WriteEndObject();
    }

    private static void Correctness(byte[] payload)
    {
        var input = (byte[])payload.Clone();
        var borrowed = new ProbeBatch(input);
        using (var lease = new RowBatchLease(borrowed, Expected, Shape.Fields, Limits))
        {
            Array.Clear(input);
            Check(lease.Materialize(0, Shape) == 1 && lease.Materialize(Rows - 1, Shape) == Rows,
                "Borrowed mutable input no longer has defensive snapshot isolation.");
        }
        Check(borrowed.Releases == 1, "Borrowed owner release count differs.");

        input = (byte[])payload.Clone();
        var owned = new OwnedProbeBatch(input);
        var retained = new RowBatchLease(owned, Expected, Shape.Fields, Limits);
        Check(owned.Takes == 1 && owned.Releases == 0, "Transfer released the original owner before disposal.");
        Reject<ObjectDisposedException>(() => { _ = owned.Bytes; });
        Reject<ObjectDisposedException>(() => { _ = owned.TakeBytes(); });
        Check(retained.Materialize(0, Shape) == 1, "Owned row decode changed.");
        Check(MemoryMarshal.TryGetArray(retained.Value(0)!.Value, out var segment)
            && ReferenceEquals(segment.Array, input), "Row lease copied the transferred managed buffer.");
        retained.Dispose(); retained.Dispose();
        Check(owned.Releases == 1, "Idempotent lease disposal released its owner twice.");
        Reject<ObjectDisposedException>(() => retained.AssertAlive());
        Reject<ObjectDisposedException>(() => retained.Materialize(0, Shape));
        Check(BinaryPrimitives.ReadInt64LittleEndian(input.AsSpan((int)DbWire.R64(input, 120))) == 1,
            "Retiring an owner invalidated managed storage.");

        input = (byte[])payload.Clone(); input[0] = 0;
        var malformed = new OwnedProbeBatch(input);
        Reject<DatabaseException>(() => { using var lease = new RowBatchLease(malformed, Expected, Shape.Fields, Limits); });
        Check(malformed.Takes == 1 && malformed.Releases == 1, "Parse failure after transfer leaked or double-released its owner.");
        var oversized = new OwnedProbeBatch((byte[])payload.Clone());
        Reject<DatabaseException>(() => { using var lease = new RowBatchLease(oversized, Expected, Shape.Fields, Limits with { MaxBatchBytes = payload.Length - 1 }); });
        Check(oversized.Takes == 1 && oversized.Releases == 1, "Post-transfer byte-limit failure did not retire its owner.");
        var tooManyRows = new OwnedProbeBatch((byte[])payload.Clone());
        Reject<DatabaseException>(() => { using var lease = new RowBatchLease(tooManyRows, Expected, Shape.Fields, Limits with { MaxBatchRows = Rows - 1 }); });
        Check(tooManyRows.Takes == 1 && tooManyRows.Releases == 1, "Post-parse row-limit failure did not retire its owner.");
        var missingEnd = new OwnedProbeBatch(Encode(0));
        Reject<DatabaseException>(() => { using var lease = new RowBatchLease(missingEnd, Expected, Shape.Fields, Limits); });
        Check(missingEnd.Takes == 1 && missingEnd.Releases == 1, "Invalid empty-batch end marker did not retire its owner.");
        var end = new OwnedProbeBatch(Encode(0), end: true);
        using (var lease = new RowBatchLease(end, Expected, Shape.Fields, Limits))
            Check(lease.End && lease.Rows == 0, "Canonical owned end batch changed.");
        Check(end.Releases == 1, "End batch owner did not retire.");
    }

    private static void AssertReleased(ProbeBatch[] batches, int takes)
    {
        foreach (var batch in batches)
            Check(batch.Releases == 1 && batch.Takes == takes, "Measured owner release/transfer count differs.");
    }
    private static void Check(bool condition, string message)
    {
        s_assertions++;
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { s_assertions++; return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private class ProbeBatch(byte[] bytes, bool end = false) : IStreamBatch
    {
        protected byte[]? Payload = bytes;
        private int _active = 1;
        internal int Takes, Releases;
        public ReadOnlyMemory<byte> Bytes => Payload ?? throw new ObjectDisposedException(nameof(ProbeBatch));
        public bool End { get; } = end;
        public void Dispose()
        {
            Interlocked.Exchange(ref Payload, null);
            if (Interlocked.Exchange(ref _active, 0) != 0) Releases++;
        }
    }
    private sealed class OwnedProbeBatch(byte[] bytes, bool end = false) : ProbeBatch(bytes, end), IOwnedStreamBatch
    {
        public byte[] TakeBytes()
        {
            var result = Interlocked.Exchange(ref Payload, null) ?? throw new ObjectDisposedException(nameof(OwnedProbeBatch));
            Takes++; return result;
        }
    }
}
