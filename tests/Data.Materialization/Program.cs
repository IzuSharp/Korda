using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NativeWeave.Data;
using NativeWeave.Database;
using NativeWeave.Database.PostgreSql;
using NativeWeave.Database.Sqlite;
using NativeWeave.Samples.Materialization;

await Suite.Run(args);

internal static class Suite
{
    private static int _assertions;
    private static readonly List<ProviderResult> Providers = [];
    internal static void Check(bool value, string message) { _assertions++; if (!value) throw new InvalidOperationException(message); }
    internal static T Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T exception) { _assertions++; return exception; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T exception) { _assertions++; return exception; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static Projection<T> Fields<T>(RowMaterializer<T> materializer) => Projection<T>.Create(MaterializationExamples.Projection.Fields, materializer);
    internal static async Task Run(string[] args)
    {
        if (args.Length < 1) throw new ArgumentException("result-directory [--require-aot]");
        if (args.Contains("--require-aot", StringComparer.Ordinal)) Check(!RuntimeFeature.IsDynamicCodeSupported, "Actual Native AOT process");
        ProjectionOracle.Run();
        GeneratedOracle.Run();
        var pg = Environment.GetEnvironmentVariable("NW_TEST_POSTGRES") ?? throw new InvalidOperationException("Real PostgreSQL18 endpoint required.");
        var directory = Path.Combine(Path.GetTempPath(), "nw-b12-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var sqlite = new DataSourceBuilder().UseSqlite(Path.Combine(directory, "materialization.db")).ConfigureLimits(new DatabaseLimits { MaxConnections = 1, MaxBatchRows = 2 }).Build();
            var postgres = new DataSourceBuilder().UsePostgreSql(pg, o => o.TlsMode = PostgreSqlTlsMode.DisableForLocalDevelopment).ConfigureLimits(new DatabaseLimits { MaxConnections = 1, MaxBatchRows = 2 }).Build();
            foreach (var source in new[] { sqlite, postgres }) await Provider(source);
        }
        finally { Directory.Delete(directory, true); }
        var result = new Result(!RuntimeFeature.IsDynamicCodeSupported, _assertions, Providers);
        Directory.CreateDirectory(args[0]); File.WriteAllText(Path.Combine(args[0], "result.json"), JsonSerializer.Serialize(result, TestJson.Default.Result));
        Console.WriteLine($"PASS B12 actual two-provider materialization: {_assertions} assertions; AOT={result.NativeAot}; every failure retired managed batches/bytes and native cursor/operation/buffer owners");
    }
    private static async Task Provider(DataSource source)
    {
        var provider = source.Provider.ToString();
        var filename = source.Provider == DatabaseProvider.Sqlite ? "libnw_database_sqlite.so" : "libnw_database_postgresql.so";
        var path = Path.Combine(AppContext.BaseDirectory, "runtimes", "linux-x64", "native", filename);
        using var diagnostic = new Diagnostic(path);
        var nativeBefore = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        var counted = new CountedResources(); var cases = 0; var mapped = 0;
        await using var session = await source.OpenSessionAsync();
        var table = "b12_" + Guid.NewGuid().ToString("N");
        var binaryType = source.Provider == DatabaseProvider.Sqlite ? "BLOB" : "BYTEA";
        var jsonType = source.Provider == DatabaseProvider.Sqlite ? "TEXT" : "JSONB";
        await session.ExecuteAsync(SqlCommand.Create($"CREATE TABLE {table} (id BIGINT PRIMARY KEY, name TEXT NOT NULL, bytes {binaryType} NOT NULL, optional BIGINT NULL, settings {jsonType} NOT NULL)", kind: SqlCommandKind.Mutation));
        for (long id = 1; id <= 5; id++)
        {
            var values = new SqlParameters().Add("id", id, Codecs.Int64).Add("name", "row 🦀 " + id, Codecs.String)
                .Add("bytes", new byte[] { (byte)id, 0, 255 }, Codecs.Bytes).Add("optional", id % 2 == 0 ? id : (long?)null, Codecs.Nullable(Codecs.Int64))
                .Add("settings", new Envelope<Settings>(new(id, "static")), MaterializationExamples.SettingsCodec);
            await session.ExecuteAsync(SqlCommand.Create($"INSERT INTO {table} VALUES (@id,@name,@bytes,@optional,@settings)", values, SqlCommandKind.Mutation));
        }
        var command = SqlCommand.Create($"SELECT id,name,bytes,optional,settings FROM {table} ORDER BY id");
        async Task Retired()
        {
            counted.AssertRetired(); await diagnostic.WaitCursorZero();
            Check(await session.ScalarAsync(SqlCommand.Create("SELECT COUNT(*) FROM " + table), Codecs.Int64) == 5, "Mapper cleanup leaves actual session reusable");
            cases++;
        }
        var rows = await MaterializationExamples.ReadAsync(session, command);
        Check(rows.Count == 5 && rows[0].Optional is null && rows[1].Optional == 2 && rows[4].Settings.Value.Level == 5, "Complete owned shape, SQL NULL and static closed-generic JSON");
        Check(await MaterializationExamples.SumAsync(session, command) == 15, "Actual public async enumeration");
        await Retired();
        var owned = await counted.Stream(session, command, MaterializationExamples.Projection).ToListAsync();
        Check(owned.Count == 5 && counted.ReleasedBatches >= 3 && counted.PeakBytes > 0, "Actual bounded multi-batch owner accounting");
        owned[0].Bytes[0] = 99;
        Check(owned[1].Bytes[0] == 2 && rows[0].Bytes[0] == 1 && owned[0].Name == "row 🦀 1", "Retained string/arrays independently own data after native batch release");
        await Retired();
        foreach (var select in new[]
        {
            "id,name,bytes,settings", // missing nullable field must not become null
            "id,id AS name,bytes,optional,settings", // wrong logical codec
            "id,name AS id,bytes,optional,settings", // duplicate actual alias
            "name,id,bytes,optional,settings", // wrong ordered shape
            "id,name,bytes,optional,settings, id AS extra", // extra field
        })
        {
            var projection = Fields<OwnedRow>(row => { mapped++; return MaterializationExamples.Projection.Materializer(row); });
            var failure = await ThrowsAsync<DatabaseException>(() => counted.Stream(session, SqlCommand.Create($"SELECT {select} FROM {table}"), projection).ToListAsync());
            Check(failure.Kind == DatabaseErrorKind.ShapeMismatch && mapped == 0, "Provider rejects complete wrong shape before constructor");
            await Retired();
        }
        var nonnull = Projection<long>.Create([ResultField.Create("optional", Codecs.Int64)], row => { mapped++; return row.Read(0, Codecs.Int64); });
        var nullFailure = await ThrowsAsync<DatabaseException>(() => counted.Stream(session, SqlCommand.Create($"SELECT optional FROM {table} WHERE id=1"), nonnull).ToListAsync());
        Check(nullFailure.Kind == DatabaseErrorKind.Conversion && mapped == 0, "Nonnullable actual SQL NULL rejects before managed constructor");
        await Retired();
        var sentinel = new MappingFailure("constructor"); var attempts = 0; IReadOnlyList<OwnedRow>? published = null;
        var constructor = Fields<OwnedRow>(row => { if (++attempts == 3) throw sentinel; return MaterializationExamples.Projection.Materializer(row); });
        var observed = await ThrowsAsync<MappingFailure>(async () => published = await counted.Stream(session, command, constructor).ToListAsync());
        Check(ReferenceEquals(sentinel, observed) && attempts == 3 && published is null, "Throwing direct constructor publishes no partial list");
        await Retired();
        ThrowingSetter.Failure = new("setter");
        var setter = Projection<ThrowingSetter>.Create([ResultField.Create("id", Codecs.Int64)], row => new() { Id = row.Read(0, Codecs.Int64) });
        Check(ReferenceEquals(ThrowingSetter.Failure, await ThrowsAsync<MappingFailure>(() => counted.Stream(session, SqlCommand.Create("SELECT id FROM " + table), setter).ToListAsync())), "Direct setter exception identity");
        await Retired();
        ThrowingJson.Failure = new("static JSON OnDeserialized"); var codec = Codecs.Json(TestJson.Default.ThrowingJson);
        var json = Projection<ThrowingJson>.Create([ResultField.Create("settings", codec)], row => row.Read(0, codec));
        Check(ReferenceEquals(ThrowingJson.Failure, await ThrowsAsync<MappingFailure>(() => counted.Stream(session, SqlCommand.Create("SELECT settings FROM " + table), json).ToListAsync())), "Static JSON decoder exception identity");
        await Retired();
        var badJsonCodec = Codecs.Json(TestJson.Default.Int32);
        var badJson = Projection<int>.Create([ResultField.Create("settings", badJsonCodec)], row => row.Read(0, badJsonCodec));
        var jsonFailure = await ThrowsAsync<DatabaseException>(() => counted.Stream(session, SqlCommand.Create("SELECT settings FROM " + table), badJson).ToListAsync());
        Check(jsonFailure.Kind == DatabaseErrorKind.Conversion && jsonFailure.InnerException is JsonException, "JSON value mismatch remains typed conversion failure");
        await Retired();
        await using (var stream = counted.Stream(session, command, MaterializationExamples.Projection).GetAsyncEnumerator())
            Check(await stream.MoveNextAsync() && stream.Current.Id == 1, "One-row early exit");
        await Retired();
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            var failure = await ThrowsAsync<OperationCanceledException>(() => counted.Stream(session, command, MaterializationExamples.Projection, canceled.Token).ToListAsync());
            Check(failure.CancellationToken == canceled.Token, "Pre-canceled token identity and no native owner leak");
        }
        await Retired();
        using (var canceled = new CancellationTokenSource())
        {
            await using var stream = counted.Stream(session, command, MaterializationExamples.Projection, canceled.Token).GetAsyncEnumerator();
            Check(await stream.MoveNextAsync(), "Accepted row before cancellation"); canceled.Cancel();
            await ThrowsAsync<OperationCanceledException>(async () => await stream.MoveNextAsync());
        }
        await Retired();
        Check(await counted.Stream(session, command, MaterializationExamples.Projection).FirstAsync() is { Id: 1 }, "First terminal returns owned DTO");
        await Retired();
        await ThrowsAsync<InvalidOperationException>(() => counted.Stream(session, command, MaterializationExamples.Projection).SingleAsync());
        await Retired();
        var noRows = SqlCommand.Create($"SELECT id,name,bytes,optional,settings FROM {table} WHERE id<0");
        Check(await counted.Stream(session, noRows, MaterializationExamples.Projection).SingleOrDefaultAsync() is null, "Default cardinality and zero rows");
        await Retired();
        var generatedCommand = SqlCommand.Create($"SELECT id, CASE WHEN optional IS NULL THEN NULL ELSE name END AS optional FROM {table} ORDER BY id");
        foreach (var projection in GeneratedOracle.Projections)
        {
            GeneratedRow.Calls = 0;
            var actual = await counted.Stream(session, generatedCommand, projection).ToListAsync();
            Check(actual.Count == 5 && actual[0].Id == 1 && actual[0].Optional is null && actual[1].Optional == "row 🦀 2" && GeneratedRow.Calls == 5,
                "Actual generated/manual owned materialization shares independent expected rows");
            await Retired();
            GeneratedRow.Calls = 0; GeneratedRow.Failure = new MappingFailure("actual generated/manual constructor");
            IReadOnlyList<GeneratedRow>? partial = null;
            try
            {
                var failure = await ThrowsAsync<MappingFailure>(async () => partial = await counted.Stream(session, generatedCommand, projection).ToListAsync());
                Check(ReferenceEquals(failure, GeneratedRow.Failure) && GeneratedRow.Calls == 3 && partial is null, "Actual generated/manual throw has no partial result");
            }
            finally { GeneratedRow.Failure = null; }
            await Retired();
        }
        await session.ExecuteAsync(SqlCommand.Create("DROP TABLE " + table, kind: SqlCommandKind.Mutation));
        await session.DisposeAsync(); await source.DisposeAsync();
        counted.AssertRetired(); await diagnostic.WaitAllZero();
        Check(owned[4].Settings.Value.Tag == "static" && owned[4].Bytes[0] == 5, "DTO remains usable after source and runtime shutdown");
        var nativeAfter = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))); Check(nativeBefore == nativeAfter, "Ordinary DTO source leaves precompiled native image unchanged");
        Providers.Add(new(provider, cases, counted.ReleasedBatches, counted.Joins, counted.PeakBytes, diagnostic.Counts(), nativeBefore, nativeAfter));
    }
}
internal sealed class ThrowingSetter
{
    internal static MappingFailure Failure { get; set; } = new("unset");
    public long Id { get => 0; set => throw Failure; }
}
internal sealed class ThrowingJson : IJsonOnDeserialized
{
    internal static MappingFailure Failure { get; set; } = new("unset");
    public JsonElement Value { get; set; }
    public void OnDeserialized() => throw Failure;
}
internal sealed class Diagnostic : IDisposable
{
    private readonly nint _library;
    private readonly nint _count;
    internal Diagnostic(string path)
    {
        _library = NativeLibrary.Load(path);
        _count = NativeLibrary.GetExport(_library, "nw_db_diagnostic_count_v1");
    }
    internal unsafe ulong[] Counts()
    {
        var result = new ulong[6]; for (uint i = 0; i < result.Length; i++) result[i] = ((delegate* unmanaged[Cdecl]<uint, ulong>)_count)(i); return result;
    }
    internal async Task WaitCursorZero()
    {
        for (var i = 0; i < 100; i++)
        {
            var counts = Counts(); if (counts[2] == 0 && counts[3] == 0 && counts[4] == 0) { Suite.Check(true, "Native operations/cursors/buffers zero"); return; }
            await Task.Delay(10);
        }
        throw new InvalidOperationException("Native cursor leak: " + string.Join(',', Counts()));
    }
    internal async Task WaitAllZero()
    {
        for (var i = 0; i < 100; i++)
        {
            if (Counts().All(v => v == 0)) { Suite.Check(true, "All six actual native owner counters zero"); return; }
            await Task.Delay(10);
        }
        throw new InvalidOperationException("Native owner leak: " + string.Join(',', Counts()));
    }
    public void Dispose() => NativeLibrary.Free(_library);
}
internal sealed record ProviderResult(string Provider, int Cases, int ManagedBatchesReleased, int NativeJoinsObserved, long PeakChargedBatchBytes, ulong[] FinalNativeOwners, string NativeSha256Before, string NativeSha256After);
internal sealed record Result(bool NativeAot, int Assertions, List<ProviderResult> Providers);
[JsonSerializable(typeof(Result))]
[JsonSerializable(typeof(ThrowingJson))]
[JsonSerializable(typeof(int))]
internal partial class TestJson : JsonSerializerContext { }
