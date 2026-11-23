using Korda.Data;
using Korda.Database;
using Korda.Interop.Abi;
using Korda.Query.Internal;
using System.Buffers.Binary;

internal static partial class Suite
{
    private static void Reject<T>(Action action) where T : Exception { try { action(); } catch (T) { Check(true, "exact synchronous rejection"); return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static byte[] U64(ulong value) { var bytes = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(bytes, value); return bytes; }
    private static void PayloadValidation()
    {
        var primary = ControlRecord.Encode(7, new(1, 1, ControlRecord.U32(0)), new(2, 1, ControlRecord.U32(0)), new(3, 1, ControlRecord.U32(0)), new(4, 1, ControlRecord.U32(0)), new(5, 1, ControlRecord.U32(0)), new(6, 1, ControlRecord.U32(0)), new(7, 1, ControlRecord.U32(0)), new(8, 2, U64(1)));
        var progress = ControlRecord.Encode(15, new(1, 2, U64(2)), new(2, 1, ControlRecord.U32(1)), new(3, 1, ControlRecord.U32(0)), new(4, 1, ControlRecord.U32(2)));
        var ordinary = BulkErrorPayload.Read(primary, out var absent); Check(absent is null && ordinary.SequenceEqual(primary), "ordinary kind7 remains unchanged");
        var sequence = primary.Concat(progress).ToArray(); var first = BulkErrorPayload.Read(sequence, out var decoded); Check(first.SequenceEqual(primary) && decoded == new BulkProgress(2, 1, false, TransactionOutcome.RolledBack), "self-sized kind7+kind15 sequence");
        Reject<DatabaseException>(() => BulkErrorPayload.Read(sequence[..^1], out _)); Reject<DatabaseException>(() => BulkErrorPayload.Read(sequence.Concat([(byte)0]).ToArray(), out _)); Reject<DatabaseException>(() => BulkErrorPayload.Read(primary.Concat(primary).ToArray(), out _)); Reject<DatabaseException>(() => BulkErrorPayload.Read(sequence.Concat(progress).ToArray(), out _));
        var overflow = ControlRecord.Encode(15, new(1, 2, U64(ulong.MaxValue)), new(2, 1, ControlRecord.U32(1)), new(3, 1, ControlRecord.U32(0)), new(4, 1, ControlRecord.U32(0))); Reject<DatabaseException>(() => BulkErrorPayload.Read(primary.Concat(overflow).ToArray(), out _));
        var wrongType = progress.ToArray(); wrongType[18] = 3; Reject<DatabaseException>(() => BulkErrorPayload.Read(primary.Concat(wrongType).ToArray(), out _));
        var inner = new Exception(); inner.Data[BulkFailure.Key] = new BulkProgress(1, 1, false, TransactionOutcome.InProgress); Check(!BulkFailure.TryGetProgress(new Exception("outer", inner), out _), "BulkFailure never scans inner errors");
    }
    private static async Task RawContracts(DataSource source, DataSession session)
    {
        var model = source.RegisterModel(Items.Table); var projection = source.RegisterProjection(Items.Row);
        var insert = MutationEncoding.Values(source, Items.Table, [RowSnapshot.Capture(Items.Table, new Item(60, 1, "snapshot", 1))]);
        var update = MutationEncoding.Values(source, Items.Table, [new Changes<Item>().Set(Items.Name, (string?)null).Snapshot(Items.Table)]);
        var key = MutationEncoding.Values(source, Items.Table, [Key(60, 1).Values]);
        var checks = MutationEncoding.Values(source, Items.Table, [new ConcurrencyCheck<Item>().Expect(Items.Version, 1L).Snapshot(Items.Table)]);
        var tableField = new ControlField(1, 1, ControlRecord.U32(Items.Table.Identity.Id));
        for (uint kind = 0; kind <= 6; kind++)
        {
            ControlField[] fields = kind switch
            {
                0 => [tableField, new(2, 5, insert)],
                1 => [tableField, new(2, 5, insert), new(7, 1, ControlRecord.U32(projection))],
                2 => [tableField, new(2, 5, update), new(3, 5, key)],
                3 => [tableField, new(2, 5, update), new(3, 5, key), new(4, 5, checks)],
                4 => [tableField, new(3, 5, key)],
                5 => [tableField, new(3, 5, key), new(4, 5, checks)],
                _ => [tableField, new(2, 5, insert), new(5, 7, ControlRecord.Vector([Items.Tenant.Id, Items.Id.Id])), new(8, 5, update)]
            };
            for (var removed = 0; removed < fields.Length; removed++)
            { var bytes = ControlRecord.Encode(4, fields.Where((_, i) => i != removed).ToArray()); RawMutationReject(session, model, kind, bytes); }
            for (ushort extra = 1; extra <= 8; extra++) if (!fields.Any(f => f.Id == extra)) RawMutationReject(session, model, kind, ControlRecord.Encode(4, [.. fields, new(extra, 1, ControlRecord.U32(1))]));
        }
        Check(await session.FindAsync(Items.Table, Key(60, 1)) is null, "malformed mutation inventory had no write side effect");
        var wrongProjection = Projection<long>.Create([ResultField.Create("does_not_exist", Codecs.Int64)], row => row.Read(0, Codecs.Int64));
        await Throws<Exception>(() => session.InsertReturningAsync(Items.Table, new Item(60, 1, "bad returning", 1), wrongProjection)); Check(await session.FindAsync(Items.Table, Key(60, 1)) is null, "RETURNING columns validate before write");
        var relation = Relation<Item, Child>.CreateOptional(Items.Table, Items.Tenant, Children.Table, Children.Tenant).AndOptional(Items.Id, Children.ParentId);
        var owner = source.RegisterRelation(relation);
        var relationKey = MutationEncoding.Values(source, Items.Table, [Key(1, 1).Values]);
        Reject<BindingFailure>(() => MetadataBindings.CreateRelationLoad(session.Owner, owner, relationKey, projection));
        var reversed = MutationEncoding.Values(source, Items.Table, [Key(1, 1).Values.Reverse().ToArray()]); var childProjection = source.RegisterProjection(Children.Row);
        Reject<BindingFailure>(() => MetadataBindings.CreateRelationLoad(session.Owner, owner, reversed, childProjection));
        await using var other = Source(source.Provider, source.Provider == DatabaseProvider.Sqlite ? Path.Combine(Path.GetTempPath(), "nw-b14-unopened-" + Guid.NewGuid().ToString("N")) : Environment.GetEnvironmentVariable("NW_TEST_POSTGRES")!);
        var foreign = other.RegisterModel(Children.Table); RawMixedRelation(source, source.RegisterModel(Items.Table), foreign, ControlRecord.Relation(relation));
        Check((await session.LoadRelatedAsync(Key(1, 1), relation)).Count == 2, "failed native admissions leave existing registry usable");
    }
    private static unsafe void RawMutationReject(DataSession session, DatabaseOwner model, uint kind, byte[] bytes)
    {
        using var s = session.Owner.Borrow(DatabaseOwnerKind.Session); using var m = model.Borrow(DatabaseOwnerKind.Model); nint output = 123; var error = BindingChecks.Error(); uint status;
        fixed (byte* input = bytes) status = session.Source.Binding.Calls.nw_db_mutation_create_v1(s.Pointer, m.Pointer, kind, BindingChecks.View(input, bytes.Length), &output, &error);
        Check(status != NativeStatus.Ok && output == 0, "raw mutation required/forbidden fields reject atomically with zero output");
    }
    private static unsafe void RawMixedRelation(DataSource source, DatabaseOwner parent, DatabaseOwner child, byte[] bytes)
    {
        using var s = source.Owner.Borrow(DatabaseOwnerKind.Source); using var p = parent.Borrow(DatabaseOwnerKind.Model); using var c = child.Borrow(DatabaseOwnerKind.Model); nint output = 123; var error = BindingChecks.Error(); uint status;
        fixed (byte* input = bytes) status = source.Binding.Calls.nw_db_relation_register_v1(s.Pointer, p.Pointer, c.Pointer, BindingChecks.View(input, bytes.Length), &output, &error);
        Check(status == NativeStatus.InvalidState && error.Code == 13 && output == 0, "mixed source generation relation has stable13 and zero output");
    }
}
