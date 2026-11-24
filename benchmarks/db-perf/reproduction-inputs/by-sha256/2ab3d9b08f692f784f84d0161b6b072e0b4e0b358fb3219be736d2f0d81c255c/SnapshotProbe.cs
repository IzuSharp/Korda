using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Korda.Data;
using Korda.Database;
using Korda.Database.PostgreSql;
using Korda.Database.Sqlite;
using Korda.Interop;
using Korda.Interop.Abi;

internal static class SnapshotProbe
{
    private static TransactionSnapshot _snapshotSink;
    private static DatabaseCloseResult? _cleanupSink;
    private static readonly TransactionSnapshot Empty = new(TransactionState.Active, TransactionOutcome.InProgress, false,
        new(CleanupStatus.NotStarted, 0, null, false, ConnectionDisposition.Usable, false));
    private readonly record struct Field(ushort Id, ushort Type, byte[] Bytes);
    private readonly record struct Observation(TransactionSnapshot? Value, string? Type, string? Message,
        DatabaseErrorKind? Kind, string? ProviderCode);
    private readonly record struct Counter(long Bytes, int Gen0, int Gen1, int Gen2, long Ticks)
    {
        internal static Counter Read()
        {
            var a = GC.CollectionCount(0); var b = GC.CollectionCount(1); var c = GC.CollectionCount(2);
            return new(GC.GetTotalAllocatedBytes(true), a, b, c, Stopwatch.GetTimestamp());
        }
    }
    private readonly record struct Sample(string Lane, int Window, int Operations, Counter Before, Counter After);

    internal static async Task<bool> RunIfRequested(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("--snapshot-probe" or "--snapshot-public")) return false;
        if (RuntimeFeature.IsDynamicCodeSupported) throw new InvalidOperationException("A real Native AOT probe is required.");
        if (args[0] == "--snapshot-public")
        {
            if (args.Length != 6) throw new ArgumentException("--snapshot-public <sqlite|postgresql> <connection> <output> <iterations> <windows>");
            await Public(args[1], args[2], args[3], int.Parse(args[4]), int.Parse(args[5]));
            return true;
        }
        if (args.Length != 4) throw new ArgumentException("--snapshot-probe <output> <iterations> <windows>");
        var output = args[1]; var iterations = int.Parse(args[2]); var windows = int.Parse(args[3]);
        Bounds(iterations, windows); Directory.CreateDirectory(output);
        var oracleInputs = Verify();
        var normal = Frame(Standard()); var releasedFields = Standard();
        releasedFields[3] = new(4, 1, U32(2)); releasedFields[5] = new(6, 1, U32(1));
        var released = Frame(releasedFields); var provider = Frame(Standard("40001"));
        File.WriteAllBytes(Path.Combine(output, "normal-control.bin"), normal);
        File.WriteAllBytes(Path.Combine(output, "released-control.bin"), released);
        File.WriteAllBytes(Path.Combine(output, "provider-control.bin"), provider);
        for (var i = 0; i < 10000; i++)
        { _snapshotSink = HostEncoding.Snapshot(normal); _snapshotSink = HostEncoding.Snapshot(released); _snapshotSink = HostEncoding.Snapshot(provider); }
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var samples = new Sample[checked(windows * 4)];
        for (var window = 0; window < windows; window++)
        for (var lane = 0; lane < 4; lane++)
        {
            var before = Counter.Read();
            if (lane == 0) for (var i = 0; i < iterations; i++) _snapshotSink = Empty;
            else
            {
                var payload = lane == 1 ? normal : lane == 2 ? released : provider;
                for (var i = 0; i < iterations; i++) _snapshotSink = HostEncoding.Snapshot(payload);
            }
            var after = Counter.Read();
            samples[window * 4 + lane] = new(lane switch { 0 => "empty-driver", 1 => "normal-control", 2 => "released-control", _ => "provider-control" }, window, iterations, before, after);
        }
        if (_snapshotSink.Cleanup.ProviderCode != "40001") throw new InvalidOperationException("Snapshot result was not consumed.");
        Write(output, "parser-only", oracleInputs, samples, null, null, null, normal, released, provider);
        return true;
    }

    private static async Task Public(string provider, string connection, string output, int iterations, int windows)
    {
        Bounds(iterations, windows); Directory.CreateDirectory(output);
        var builder = new DataSourceBuilder().WithName("DB-Perf-01-snapshot-public")
            .ConfigureLimits(new DatabaseLimits { MaxConnections = 1, MaxWaiters = 4 });
        if (provider == "sqlite") builder.UseSqlite(connection);
        else if (provider == "postgresql") builder.UsePostgreSql(connection, options => options.TlsMode = PostgreSqlTlsMode.DisableForLocalDevelopment);
        else throw new ArgumentException("Unknown provider.");
        var source = builder.Build(); var session = await source.OpenSessionAsync();
        var engine = source.Provider == DatabaseProvider.Sqlite ? SqliteDataSource.Engine : PostgreSqlDataSource.Engine;
        var actualNativePayload = NativePayload(session);
        File.WriteAllBytes(Path.Combine(output, "actual-session-control.bin"), actualNativePayload);
        CheckEquivalent(actualNativePayload, "actual native session snapshot");
        for (var i = 0; i < 10000; i++) _cleanupSink = session.Cleanup;
        var beforeOwners = Counts(engine); var samples = new Sample[windows];
        for (var window = 0; window < windows; window++)
        {
            var before = Counter.Read();
            for (var i = 0; i < iterations; i++) _cleanupSink = session.Cleanup;
            var after = Counter.Read(); samples[window] = new("public-session-cleanup", window, iterations, before, after);
        }
        var afterOwners = Counts(engine);
        if (!beforeOwners.SequenceEqual(afterOwners) || _cleanupSink!.Status != CleanupStatus.NotStarted)
            throw new InvalidOperationException("Live snapshot changed ownership, SQL counters, or source state.");
        await session.DisposeAsync(); await source.DisposeAsync(); var drained = Counts(engine);
        if (drained.Take(6).Any(value => value != 0) || !session.Cleanup.LocalResourcesReleased || !source.Cleanup.LocalResourcesReleased)
            throw new InvalidOperationException("Public snapshot owners did not retire.");
        Write(output, provider + "/public-session-cleanup", 1, samples, beforeOwners, afterOwners, drained, actualNativePayload);
    }

    private static unsafe byte[] NativePayload(DataSession session)
    {
        using var lease = session.Owner.Borrow(session.Owner.Kind); nint result = 0;
        var error = new NwErrorInfoV1 { StructSize = 24 };
        HostFailure.Check(session.Source.Binding, session.Source.Binding.Calls.nw_db_snapshot_v1(lease.Pointer, default, &result, &error), error);
        using var owner = new Korda.Database.NativeBuffer(session.Source.Binding, result); return owner.Copy();
    }
    private static unsafe ulong[] Counts(EngineDescriptor engine)
    {
        var count = (delegate* unmanaged[Cdecl]<uint, ulong>)EngineModuleLoader.ResolveExport(engine, "nw_db_diagnostic_count_v1");
        var values = new ulong[8]; for (uint i = 0; i < values.Length; i++) values[i] = count(i); return values;
    }
    private static void Bounds(int iterations, int windows)
    { if (iterations <= 0 || windows is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(iterations)); }

    internal static void VerifyAllocationBudget()
    {
        if (RuntimeFeature.IsDynamicCodeSupported) throw new InvalidOperationException("A real Native AOT probe is required.");
        const int operations = 10000;
        var normal = Frame(Standard()); var provider = Frame(Standard("40001"));
        for (var i = 0; i < operations; i++)
        { _snapshotSink = HostEncoding.Snapshot(normal); _snapshotSink = HostEncoding.Snapshot(provider); }
        var before = Counter.Read();
        for (var i = 0; i < operations; i++) _snapshotSink = HostEncoding.Snapshot(normal);
        var normalAfter = Counter.Read();
        for (var i = 0; i < operations; i++) _snapshotSink = HostEncoding.Snapshot(provider);
        var providerAfter = Counter.Read();
        var normalBytes = normalAfter.Bytes - before.Bytes; var providerBytes = providerAfter.Bytes - normalAfter.Bytes;
        Console.WriteLine($"snapshot allocation operations={operations}; normal raw bytes={normalBytes}; provider raw bytes={providerBytes}");
        // The immutable public result and optional string remain allocated.
        // These positive budgets reject reintroduced per-field parsing objects;
        // they are deliberately not a zero-allocation acceptance claim.
        if (normalBytes > operations * 64L || providerBytes > operations * 96L)
            throw new InvalidOperationException("Snapshot parsing exceeded its bounded temporary-allocation regression budget.");
    }

    internal static int Verify()
    {
        var count = 0;
        void Test(byte[] bytes, string name) { CheckEquivalent(bytes, name); count++; }
        var fixtures = new[] { Frame(Standard()), Frame(Standard("40001")), Frame(Standard("")), Frame(Standard("\ufeff\0x")) };
        foreach (var fixture in fixtures)
        {
            Test(fixture, "valid");
            for (var length = 0; length < fixture.Length; length++) Test(fixture[..length], "truncated");
            for (var at = 0; at < fixture.Length; at++)
            foreach (var mask in new byte[] { 1, 128, 255 })
            { var copy = fixture.ToArray(); copy[at] ^= mask; Test(copy, "byte mutation"); }
        }
        for (var missing = 0; missing < 9; missing++)
        { var fields = Standard("x"); fields.RemoveAt(missing); Test(Frame(fields), "missing field"); }
        for (var ordinal = 0; ordinal < 9; ordinal++)
        for (ushort type = 0; type <= 8; type++)
        foreach (var length in new[] { 0, 1, 3, 4, 7, 8, 9, 16 })
        {
            var fields = Standard("x"); fields[ordinal] = fields[ordinal] with { Type = type, Bytes = new byte[length] };
            Test(Frame(fields), "type and width");
        }
        for (var ordinal = 0; ordinal < 7; ordinal++)
        foreach (var value in new uint[] { 0, 1, 2, 3, 4, 5, uint.MaxValue })
        { var fields = Standard(); fields[ordinal] = fields[ordinal] with { Bytes = U32(value) }; Test(Frame(fields), "enum or boolean bounds"); }
        foreach (var raw in new byte[][] { [0xc0, 0xaf], [0xed, 0xa0, 0x80], [0xf4, 0x90, 0x80, 0x80], [0xef, 0xbb, 0xbf], [0] })
        {
            var fields = Standard("x"); fields[8] = fields[8] with { Bytes = raw }; Test(Frame(fields), "provider Unicode");
            fields = Standard(); fields.Add(new(10, 4, raw)); Test(Frame(fields), "opaque unknown field");
        }
        for (var entries = 8; entries <= 65; entries++)
        {
            var fields = Standard(); for (var id = 10; fields.Count < entries; id++) fields.Add(new((ushort)id, 5, []));
            Test(Frame(fields), "field count limit");
        }
        // Error ordering: generic structure precedes semantic getters; every
        // required getter precedes enum validation and optional UTF-8 decoding.
        var conflict = Standard("x"); conflict[0] = conflict[0] with { Bytes = U32(99) }; conflict.RemoveAt(6);
        Test(Frame(conflict), "bad state and missing later required field");
        conflict.Add(new(10, 0, [])); Test(Frame(conflict), "bad unknown field before semantic error");
        conflict = Standard("x"); conflict.RemoveAt(0); conflict[^1] = conflict[^1] with { Bytes = [0xff] };
        Test(Frame(conflict), "missing state before invalid provider text");
        var trailing = Frame(Standard()).Concat(new byte[] { 0 }).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(trailing.AsSpan(4), (uint)trailing.Length); Test(trailing, "trailing bytes");
        var random = new Random(20261124);
        for (var i = 0; i < 2048; i++)
        { var bytes = new byte[random.Next(0, 512)]; random.NextBytes(bytes); Test(bytes, "deterministic random"); }
        return count;
    }
    private static void CheckEquivalent(byte[] bytes, string name)
    {
        var expected = Observe(bytes, true); var actual = Observe(bytes, false);
        if (expected != actual) throw new InvalidOperationException($"Snapshot oracle differs for {name}; input={Convert.ToHexStringLower(SHA256.HashData(bytes))}; old={expected}; new={actual}");
    }
    private static Observation Observe(byte[] bytes, bool legacy)
    {
        try { return new(legacy ? LegacySnapshot(bytes) : HostEncoding.Snapshot(bytes), null, null, null, null); }
        catch (Exception error) { return new(null, error.GetType().FullName, error.Message, (error as DatabaseException)?.Kind, (error as DatabaseException)?.ProviderCode); }
    }
    private static List<Field> Standard(string? provider = null)
    {
        var fields = new List<Field>(); for (ushort id = 1; id <= 7; id++) fields.Add(new(id, 1, U32(0)));
        var correlation = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(correlation, 123);
        fields.Add(new(8, 2, correlation)); if (provider is not null) fields.Add(new(9, 4, Encoding.UTF8.GetBytes(provider))); return fields;
    }
    private static byte[] U32(uint value) { var bytes = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, value); return bytes; }
    private static byte[] Frame(List<Field> fields)
    {
        var next = 16 + 16 * fields.Count; var bytes = new byte[next + fields.Sum(field => field.Bytes.Length)];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, 1); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), 7);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)bytes.Length); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), (uint)fields.Count);
        for (var i = 0; i < fields.Count; i++)
        {
            var field = fields[i]; var at = 16 + 16 * i;
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at), field.Id); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at + 2), field.Type);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at + 4), (uint)field.Bytes.Length); BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(at + 8), (ulong)next);
            field.Bytes.CopyTo(bytes, next); next += field.Bytes.Length;
        }
        return bytes;
    }

    // Frozen old HostEncoding parser from source SHA-256
    // 52a2ab2b48a049133a70995262120bdd6e1c899ca081cbb684749c4a4decf660.
    // Its dictionary copies are intentionally retained as a differential oracle.
    private static TransactionSnapshot LegacySnapshot(ReadOnlySpan<byte> bytes)
    {
        var fields = LegacyReadControl(bytes, 7);
        uint Get(ushort id) => fields.TryGetValue(id, out var field) && field.Length == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(field) : throw new DatabaseException("Native snapshot field is missing.", DatabaseErrorKind.ShapeMismatch);
        var state = Get(1); var outcome = Get(2); var attempted = Get(3); var cleanup = Get(4); var disposition = Get(5); var released = Get(6); var deadline = Get(7);
        if (state > 4 || outcome > 3 || attempted > 1 || cleanup > 4 || disposition > 3 || released > 1 || deadline > 1 || !fields.TryGetValue(8, out var correlation) || correlation.Length != 8)
            throw new DatabaseException("Native snapshot values are invalid.", DatabaseErrorKind.ShapeMismatch);
        return new((TransactionState)state, (TransactionOutcome)outcome, attempted == 1, new((CleanupStatus)cleanup, BinaryPrimitives.ReadUInt64LittleEndian(correlation), fields.TryGetValue(9, out var provider) ? HostEncoding.Utf8.GetString(provider) : null, released == 1, (ConnectionDisposition)disposition, deadline == 1));
    }
    private static Dictionary<ushort, byte[]> LegacyReadControl(ReadOnlySpan<byte> bytes, ushort kind)
    {
        if (bytes.Length < 16 || bytes.Length > 4 * 1024 * 1024 || BinaryPrimitives.ReadUInt16LittleEndian(bytes) != 1 || BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..]) != kind || BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != bytes.Length || BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]) != 0)
            throw new DatabaseException("Invalid native control record.", DatabaseErrorKind.ShapeMismatch);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        if (count > 64 || checked(16L + count * 16L) > bytes.Length) throw new DatabaseException("Invalid native control fields.", DatabaseErrorKind.ShapeMismatch);
        var result = new Dictionary<ushort, byte[]>(); var next = checked(16 + (int)count * 16); ushort previous = 0;
        for (var i = 0; i < count; i++)
        {
            var at = 16 + i * 16; var id = BinaryPrimitives.ReadUInt16LittleEndian(bytes[at..]); var type = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(at + 2)..]);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(at + 4)..]); var offset = BinaryPrimitives.ReadUInt64LittleEndian(bytes[(at + 8)..]);
            if (id <= previous || type is < 1 or > 7 || offset != (ulong)next || length > bytes.Length - next || type == 1 && length != 4 || type is 2 or 3 && length != 8)
                throw new DatabaseException("Invalid native control field range.", DatabaseErrorKind.ShapeMismatch);
            result.Add(id, bytes.Slice(next, (int)length).ToArray()); previous = id; next += (int)length;
        }
        if (next != bytes.Length) throw new DatabaseException("Native control record has trailing bytes.", DatabaseErrorKind.ShapeMismatch);
        return result;
    }

    private static void Write(string directory, string scope, int oracleInputs, Sample[] samples,
        ulong[]? before, ulong[]? after, ulong[]? drained, params byte[][] payloads)
    {
        using var file = File.Create(Path.Combine(directory, "snapshot-allocation.json")); using var writer = new Utf8JsonWriter(file, new() { Indented = true });
        writer.WriteStartObject(); writer.WriteNumber("SchemaVersion", 1); writer.WriteBoolean("NativeAot", true); writer.WriteString("Scope", scope);
        writer.WriteString("ExecutableSha256", Hash(Environment.ProcessPath!)); writer.WriteNumber("OracleInputs", oracleInputs);
        writer.WriteString("AllocationBoundary", "Raw whole-process GC.GetTotalAllocatedBytes(true); all threads; warmup outside; no control subtraction; public snapshots retain native-copy and result-object costs.");
        writer.WriteNumber("StopwatchFrequency", Stopwatch.Frequency); writer.WriteNumber("WarmupPerLane", 10000);
        writer.WriteStartArray("PayloadSha256"); foreach (var payload in payloads) writer.WriteStringValue(Convert.ToHexStringLower(SHA256.HashData(payload))); writer.WriteEndArray();
        void Owners(string name, ulong[]? values) { writer.WritePropertyName(name); if (values is null) { writer.WriteNullValue(); return; } writer.WriteStartArray(); foreach (var value in values) writer.WriteNumberValue(value); writer.WriteEndArray(); }
        Owners("NativeBefore", before); Owners("NativeAfter", after); Owners("NativeDrained", drained);
        writer.WriteString("NativeCounterOrder", "sources,sessions,operations,cursors,buffers,handles,cumulative-sql,cumulative-protocol");
        writer.WriteStartArray("Measurements");
        foreach (var sample in samples)
        {
            writer.WriteStartObject(); writer.WriteString("Lane", sample.Lane); writer.WriteNumber("Window", sample.Window); writer.WriteNumber("Operations", sample.Operations);
            writer.WriteNumber("AllocatedBefore", sample.Before.Bytes); writer.WriteNumber("AllocatedAfter", sample.After.Bytes); writer.WriteNumber("AllocatedBytes", sample.After.Bytes - sample.Before.Bytes);
            writer.WriteNumber("Gen0", sample.After.Gen0 - sample.Before.Gen0); writer.WriteNumber("Gen1", sample.After.Gen1 - sample.Before.Gen1); writer.WriteNumber("Gen2", sample.After.Gen2 - sample.Before.Gen2);
            writer.WriteNumber("StartedTicks", sample.Before.Ticks); writer.WriteNumber("FinishedTicks", sample.After.Ticks); writer.WriteEndObject();
        }
        writer.WriteEndArray(); writer.WriteEndObject(); writer.Flush();
        Console.WriteLine($"PASS snapshot scope={scope} oracle={oracleInputs} samples={samples.Length}");
    }
    private static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(file)); }
}
