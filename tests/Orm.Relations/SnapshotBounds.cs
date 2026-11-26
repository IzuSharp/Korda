using Korda.Data;
using Korda.Database;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

internal static partial class Suite
{
    private static long CapacityAllocation(Action action, string label, long ceiling = 64 * 1024)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        var rejected = false;
        try { action(); }
        catch (DatabaseException error) when (error.Kind == DatabaseErrorKind.ResourceLimit) { rejected = true; }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(rejected, label + " rejects with resource-limit before dispatch");
        Check(allocated < ceiling, label + " rejection avoids copying the oversized input: " + allocated);
        return allocated;
    }

    private static void BoundedCodecs()
    {
        var json = Codecs.Json(BoundsJson.Default.BudgetPayload);
        var plain = new BudgetPayload("prewarm");
        Check(json.EncodeBounded(plain, 1024)!.SequenceEqual(json.Encode(plain)!), "bounded JSON preserves exact static serializer bytes");
        var nearLimit = new BudgetPayload(new string('x', 7000));
        Check(json.EncodeBounded(nearLimit, 8192)!.SequenceEqual(json.Encode(nearLimit)!), "valid near-limit JSON accepts upstream buffer hints above the logical payload budget");
        var large = new string('x', 2 * 1024 * 1024);
        var escaped = new string('<', 2 * 1024 * 1024);
        var bytes = new byte[2 * 1024 * 1024];
        var nullableString = Codecs.NullableReference(Codecs.String);
        var nullableBytes = Codecs.NullableReference(Codecs.Bytes);
        var nullableJson = Codecs.NullableReference(json);
        _ = nullableString.EncodeBounded("warm", 32); _ = nullableBytes.EncodeBounded([1], 32); _ = nullableJson.EncodeBounded(plain, 1024);
        var textCost = CapacityAllocation(() => nullableString.EncodeBounded(large, 1024), "wide string");
        var bytesCost = CapacityAllocation(() => nullableBytes.EncodeBounded(bytes, 1024), "wide bytes");
        var jsonCost = CapacityAllocation(() => nullableJson.EncodeBounded(new(large), 1024), "wide JSON");
        var escapeCost = CapacityAllocation(() => nullableJson.EncodeBounded(new(escaped), 1024), "wide escaped JSON before upstream scratch rent");
        CapacityAllocation(() => Codecs.Int64.EncodeBounded(1L, 7), "fixed scalar width");
        CapacityAllocation(() => Codecs.Nullable(Codecs.Int64).EncodeBounded(1L, 7), "nullable fixed scalar width");
        Check(nullableString.EncodeBounded(null, 0) is null && nullableBytes.EncodeBounded(null, 0) is null && nullableJson.EncodeBounded(null, 0) is null, "SQL NULL requires no payload even under zero remaining budget");
        Check(json.EncodeBounded(null!, 4)!.SequenceEqual("null"u8.ToArray()), "nonnullable JSON null remains four payload bytes");
        CapacityAllocation(() => json.EncodeBounded(null!, 3), "JSON null byte limit");
        var configured = new BoundsJson(new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = true,
            IndentCharacter = '\t',
            IndentSize = 1,
            NewLine = "\r\n",
            MaxDepth = 16
        });
        var custom = Codecs.Json(configured.BudgetPayload);
        var value = new BudgetPayload("<é\n");
        Check(custom.EncodeBounded(value, 512)!.SequenceEqual(custom.Encode(value)!), "bounded path preserves explicit static serializer encoder and indentation options");
        var numbers = Codecs.Json(BoundsJson.Default.Int32Array);
        _ = numbers.EncodeBounded([1, 2], 8192);
        var many = new int[100_000];
        var aggregateCost = CapacityAllocation(() => numbers.EncodeBounded(many, 8192), "aggregate JSON payload", 256 * 1024);
        var marker = new InputFailure();
        var options = new JsonSerializerOptions { TypeInfoResolver = BoundsJson.Default };
        var throwing = Codecs.Json(JsonMetadataServices.CreateValueInfo<ConverterValue>(options, new FailingConverter(marker)));
        try { _ = throwing.EncodeBounded(new(), 256); throw new InvalidOperationException("Expected original converter failure."); }
        catch (InputFailure failure) { Check(ReferenceEquals(failure, marker), "discarding pending oversized JSON preserves the original converter exception"); }
        DepthPayload? node = null;
        for (var i = 0; i < 66; i++) node = new(node);
        var depth = Codecs.Json(BoundsJson.Default.DepthPayload);
        Reject<InvalidOperationException>(() => depth.Encode(node!));
        Reject<InvalidOperationException>(() => depth.EncodeBounded(node!, 4096));
        Console.WriteLine($"B14 bounded snapshot rejection allocations: text={textCost}; bytes={bytesCost}; JSON={jsonCost}; escaped JSON={escapeCost}; aggregate JSON={aggregateCost}");
    }

    private sealed class ConverterValue;
    private sealed class FailingConverter(InputFailure failure) : JsonConverter<ConverterValue>
    {
        public override ConverterValue? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, ConverterValue value, JsonSerializerOptions options)
        {
            // This bounded token expands beyond the logical budget while still
            // pending in the writer. A Dispose flush must not replace failure.
            writer.WriteStringValue(new string('<', 100));
            throw failure;
        }
    }

    private static async Task SnapshotBudgets(DatabaseProvider provider, string connection)
    {
        const int limit = 8192;
        await using var source = Source(provider, connection, new() { MaxParameterBytes = limit });
        await using var session = await source.OpenSessionAsync();
        await Execute(session, provider == DatabaseProvider.Sqlite
            ? "CREATE TABLE b14_budget_rows(\"Id\" BIGINT PRIMARY KEY,\"Text\" TEXT NULL,\"Bytes\" BLOB NULL,\"Json\" TEXT NULL)"
            : "CREATE TABLE b14_budget_rows(\"Id\" BIGINT PRIMARY KEY,\"Text\" TEXT NULL,\"Bytes\" BYTEA NULL,\"Json\" JSONB NULL)");
        var b = new TableBuilder<BudgetRow>("b14_budget_rows");
        var id = b.Column("Id", Codecs.Int64, new() { IsPrimaryKey = true });
        var text = b.Column("Text", Codecs.NullableReference(Codecs.String));
        var bytes = b.Column("Bytes", Codecs.NullableReference(Codecs.Bytes));
        var json = b.Column("Json", Codecs.NullableReference(Codecs.Json(BoundsJson.Default.BudgetPayload)));
        var table = b.Build(row => new(row.Get(id), row.Get(text), row.Get(bytes), row.Get(json)),
            (row, entity) => { row.Set(id, entity.Id); row.Set(text, entity.Text); row.Set(bytes, entity.Bytes); row.Set(json, entity.Json); });
        var large = new string('x', 1024 * 1024); var escaped = new string('<', 1024 * 1024); var blob = new byte[1024 * 1024];
        var requests = 0;
        source.TestCommandBoundary = boundary => { if (boundary == HostCommandBoundary.BeforeRequest) Interlocked.Increment(ref requests); };
        try
        {
            foreach (var row in new BudgetRow[] { new(1, large, null, null), new(2, null, blob, null), new(3, null, null, new(large)), new(4, null, null, new(escaped)) })
            {
                var before = Volatile.Read(ref _retirements);
                var failure = await Throws<DatabaseException>(() => session.InsertAsync(table, row));
                Check(failure.Kind == DatabaseErrorKind.ResourceLimit && Volatile.Read(ref _retirements) == before
                    && Volatile.Read(ref requests) == 0, "oversized entity rejects before either native completion path is entered");
            }
        }
        finally { source.TestCommandBoundary = null; }
        Check(await Count(session, "b14_budget_rows") == 0, "wide input preflight has no SQL side effect");
        if (provider == DatabaseProvider.Sqlite)
        {
            // The existing provider grant also charges simultaneous SQL + two
            // wire-sized input copies; fitting the logical payload ceiling does
            // not guarantee that this nonwaiting physical grant is available.
            var before = Volatile.Read(ref _retirements);
            var statements = StatementCount(source);
            var ready = 0; var takes = 0; var publications = 0; var snapshots = 0;
            HostCommandSnapshot completion = default;
            source.TestCommandBoundary = boundary =>
            {
                if (boundary == HostCommandBoundary.Ready) Interlocked.Increment(ref ready);
                if (boundary == HostCommandBoundary.BeforeTake) Interlocked.Increment(ref takes);
                if (boundary == HostCommandBoundary.Published) Interlocked.Increment(ref publications);
            };
            source.TestCommandSnapshot = snapshot => { completion = snapshot; Interlocked.Increment(ref snapshots); };
            DatabaseException physical;
            try { physical = await Throws<DatabaseException>(() => session.InsertAsync(table, new(98, null, null, new(new string('x', 7000))))); }
            finally { source.TestCommandBoundary = null; source.TestCommandSnapshot = null; }
            Check(physical.Kind == DatabaseErrorKind.ResourceLimit, "physical input admission preserves the native resource-limit error");
            // A failed typed take publishes safely without fallback only after
            // the separate rich-error take has consumed the native error.
            Check(snapshots == 1 && ready == 1 && takes == 1 && publications == 1
                && completion.RequestKind == 2 && completion.ExpectedKind == 100
                && completion.SetupReturned && completion.RequestEntered && completion.RequestReturned
                && completion.Accepted && completion.Ready && !completion.ResultTaken && completion.Published
                && completion.NativeSafe && !completion.Indeterminate && !completion.FallbackStarted
                && !completion.Rooted && !completion.SlotReserved && completion.Controls == 0
                && completion.CallbackEntries == 0 && completion.RegistrationDrained,
                "physical input rejection consumes one admitted mutation error and drains every managed completion obligation");
            Check(Volatile.Read(ref _retirements) == before, "physical input rejection uses the direct command completion path");
            Check(StatementCount(source) == statements, "physical input rejection dispatches no SQL statement");
            Check(await Count(session, "b14_budget_rows") == 0, "physical input rejection precedes provider statement dispatch");
        }
        var valid = new BudgetRow(99, null, null, new(new string('x', 3000)));
        Check(await session.InsertAsync(table, valid) == 1, "bounded JSON within actual physical input capacity remains valid");
        var read = await session.FindAsync(table, table.Key(id, 99L));
        Check(read?.Json == valid.Json, "bounded static JSON retains actual two-provider value parity");
        var bulk = await Throws<DatabaseException>(() => session.InsertManyAsync(table, BudgetRows(new(100, null, null, new(escaped))), new() { BatchSize = 1 }));
        Check(bulk.Kind == DatabaseErrorKind.ResourceLimit && BulkFailure.TryGetProgress(bulk, out var progress) && progress == new BulkProgress(0, 0, false, TransactionOutcome.RolledBack), "bounded writer failure keeps bulk progress and cleans its real transaction");
        Check(await Count(session, "b14_budget_rows") == 1, "rejected bulk leaves preceding committed data unchanged");
        var converterFailure = new InputFailure();
        var info = JsonMetadataServices.CreateValueInfo<ConverterValue>(new() { TypeInfoResolver = BoundsJson.Default }, new FailingConverter(converterFailure));
        var converterBuilder = new TableBuilder<ConverterRow>("b14_converter_rows");
        var converterId = converterBuilder.Column("id", Codecs.Int64, new() { IsPrimaryKey = true });
        var converterValue = converterBuilder.Column("value", Codecs.Json(info));
        var converterTable = converterBuilder.Build(row => new(row.Get(converterId), row.Get(converterValue)), (row, entity) => { row.Set(converterId, entity.Id); row.Set(converterValue, entity.Value); });
        var original = await Throws<InputFailure>(() => session.InsertManyAsync(converterTable, ConverterRows(), new() { BatchSize = 1 }));
        Check(ReferenceEquals(original, converterFailure) && BulkFailure.TryGetProgress(original, out var converterProgress) && converterProgress == new BulkProgress(0, 0, false, TransactionOutcome.RolledBack), "actual bulk preserves the original static converter failure and cleans its transaction before completion");

        var builder = new TableBuilder<WideRow>("b14_wide");
        var first = builder.Column("first", Codecs.String); var second = builder.Column("second", Codecs.String);
        var assigned = 0;
        var wide = builder.Build(row => new(row.Get(first), row.Get(second)), (row, entity) =>
        { row.Set(first, entity.First); assigned++; row.Set(second, entity.Second); assigned++; });
        var entity = new WideRow(new string('a', 700), new string('b', 700));
        var allocated = CapacityAllocation(() => RowSnapshot.Capture(wide, entity, 1024), "aggregate row snapshot");
        Check(assigned == 1, "remaining row budget rejects the second encoded copy before retaining it");
        var exact = RowSnapshot.Capture(wide, new("abc", "def"), 6);
        Check(exact.Sum(cell => cell.Bytes?.Length ?? 0) == 6, "retained snapshot bytes can exactly consume their finite budget");
        var owned = RowSnapshot.Capture(table, valid);
        var manyRows = Enumerable.Repeat(owned, 128).ToArray();
        CapacityAllocation(() => MutationEncoding.Values(source, table, manyRows), "aggregate canonical wire");
        Console.WriteLine($"B14 {provider} retained row budget=1024; rejection allocation={allocated}; wire budget={limit}");
    }

    private static async IAsyncEnumerable<BudgetRow> BudgetRows(BudgetRow row) { await Task.Yield(); yield return row; }
    private static async IAsyncEnumerable<ConverterRow> ConverterRows() { await Task.Yield(); yield return new(1, new()); }
    private sealed record ConverterRow(long Id, ConverterValue Value);
    private sealed record WideRow(string First, string Second);
    private sealed record BudgetRow(long Id, string? Text, byte[]? Bytes, BudgetPayload? Json);
}

internal sealed record BudgetPayload(string Text);
internal sealed class DepthPayload(DepthPayload? next)
{
    public DepthPayload? Next { get; } = next;
}
[JsonSerializable(typeof(BudgetPayload))]
[JsonSerializable(typeof(DepthPayload))]
[JsonSerializable(typeof(int[]))]
internal partial class BoundsJson : JsonSerializerContext { }
