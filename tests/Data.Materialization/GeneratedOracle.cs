using Korda.Data;

// Both static implementations consume the independent ShapeOracle from the
// handwritten suite; generated metadata never supplies the expected fields.
[WeaveTable("b12_generated", DescriptorName = "GeneratedRows")]
internal sealed partial class GeneratedRow
{
    [WeaveColumn("id"), WeaveKey] public long Id { get; }
    [WeaveColumn("optional")] public string? Optional { get; }
    internal static int Calls;
    internal static MappingFailure? Failure;
    public GeneratedRow(long id, string? optional)
    {
        Calls++;
        if (id == 3 && Failure is { } failure) throw failure;
        Id = id; Optional = optional;
    }
}

internal static class GeneratedOracle
{
    private static readonly TableBuilder<GeneratedRow> Builder = new("b12_generated");
    private static readonly Column<GeneratedRow, long> Id = Builder.Column("id", Codecs.Int64, new() { IsPrimaryKey = true });
    private static readonly Column<GeneratedRow, string?> Optional = Builder.Column("optional", Codecs.NullableReference(Codecs.String));
    internal static readonly Table<GeneratedRow> Manual = Builder.Build(static row => new(row.Get(Id), row.Get(Optional)),
        static (row, value) => { row.Set(Id, value.Id); row.Set(Optional, value.Optional); });
    internal static IEnumerable<Projection<GeneratedRow>> Projections => [Manual.Row, GeneratedRows.Row];
    internal static void Run()
    {
        Suite.Check(Manual.MetadataHash == GeneratedRows.Table.MetadataHash && Manual.Row.ShapeHash == GeneratedRows.Row.ShapeHash,
            "Generated and manual static descriptors have identical canonical identities");
        foreach (var projection in Projections)
        {
            GeneratedRow.Calls = 0;
            var good = new ShapeOracle(ShapeOracle.Value("id", Codecs.Int64, 7L), ShapeOracle.Value<string?>("optional", Codecs.NullableReference(Codecs.String), null));
            var actual = projection.Read(good);
            Suite.Check(actual.Id == 7 && actual.Optional is null && GeneratedRow.Calls == 1, "Shared oracle present SQL NULL");
            foreach (var bad in new[]
            {
                new ShapeOracle(ShapeOracle.Value("id", Codecs.Int64, 7L)),
                new ShapeOracle(ShapeOracle.Value("id", Codecs.Int64, 7L), ShapeOracle.Value("id", Codecs.NullableReference(Codecs.String), "duplicate")),
                new ShapeOracle(ShapeOracle.Value("id", Codecs.Int64, 7L), ShapeOracle.Value("optional", Codecs.Nullable(Codecs.Int64), (long?)3)),
                new ShapeOracle(ShapeOracle.Value("optional", Codecs.NullableReference(Codecs.String), "order"), ShapeOracle.Value("id", Codecs.Int64, 7L)),
                new ShapeOracle(ShapeOracle.Value("id", Codecs.Int64, 7L), ShapeOracle.Value("optional", Codecs.String, "nullability")),
                new ShapeOracle(ShapeOracle.Value("id", Codecs.Int64, 7L), ShapeOracle.Value<string?>("optional", Codecs.NullableReference(Codecs.String), null), ShapeOracle.Value("extra", Codecs.Int64, 1L)),
            })
                Suite.Check(Suite.Throws<DatabaseException>(() => projection.Read(bad)).Kind == DatabaseErrorKind.ShapeMismatch && GeneratedRow.Calls == 1,
                    "Shared generated/manual shape oracle rejects before constructor");
            var missing = new ShapeOracle(new("id", Codecs.Int64.Descriptor, null), ShapeOracle.Value<string?>("optional", Codecs.NullableReference(Codecs.String), null));
            Suite.Check(Suite.Throws<DatabaseException>(() => projection.Read(missing)).Kind == DatabaseErrorKind.Conversion && GeneratedRow.Calls == 1, "Shared nonnullable NULL conversion");
            GeneratedRow.Failure = new MappingFailure("generated/manual constructor");
            try
            {
                var throwing = new ShapeOracle(ShapeOracle.Value("id", Codecs.Int64, 3L), ShapeOracle.Value<string?>("optional", Codecs.NullableReference(Codecs.String), null));
                Suite.Check(ReferenceEquals(GeneratedRow.Failure, Suite.Throws<MappingFailure>(() => projection.Read(throwing))), "Shared constructor preserves original exception");
            }
            finally { GeneratedRow.Failure = null; }
            good.Retire(); Suite.Throws<ObjectDisposedException>(() => projection.Read(good));
        }
    }
}
