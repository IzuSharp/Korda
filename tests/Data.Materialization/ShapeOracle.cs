using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Korda.Data;
using Korda.Database.Internal;
using Korda.Query.Internal;

// Handwritten, independent B01 row-lease oracle: it does not encode a db-wire
// envelope or call the production materializer to calculate expected results.
internal sealed class ShapeOracle : IRowLease
{
    internal readonly record struct Cell(string Alias, CodecDescriptor Type, ReadOnlyMemory<byte>? Bytes);
    private readonly Cell[] _cells;
    private bool _alive = true;
    internal ShapeOracle(params Cell[] cells) => _cells = cells;
    public int Count => _cells.Length;
    public string Name(int ordinal) => _cells[ordinal].Alias;
    public CodecDescriptor Codec(int ordinal) => _cells[ordinal].Type;
    public ReadOnlyMemory<byte>? Value(int ordinal) => _cells[ordinal].Bytes;
    public void AssertAlive() => ObjectDisposedException.ThrowIf(!_alive, this);
    internal void Retire() => _alive = false;
    internal static Cell Value<T>(string name, ValueCodec<T> codec, T value)
    { var bytes = codec.Encode(value); return new(name, codec.Descriptor, bytes is null ? (ReadOnlyMemory<byte>?)null : new ReadOnlyMemory<byte>(bytes)); }
}

internal static unsafe class ProjectionOracle
{
    private static int _releases;
    // Only a lifetime owner for construction-only tests. Every query API address
    // is absent. Real query and provider operations use B04 in the composed suite.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Release(nint _) => Interlocked.Increment(ref _releases);
    private static QueryBinding Query()
    {
        var calls = new NativeCalls("construction-only", name => name == "nw_db_owner_release_v1"
            ? (nint)(delegate* unmanaged[Cdecl]<nint, void>)&Release : 0);
        return new(new(new(calls, 1), DatabaseOwnerKind.Query, 1));
    }
    internal static void Run()
    {
        var before = _releases;
        using (var query = Query())
        using (var foreign = Query())
        {
            Projection<SampleRow>? published = null;
            ExpressionReference[]? expressions = null;
            var builder = new ProjectionDefinition<SampleRow>(query, (p, e) => { published = p; expressions = e; });
            var id = builder.Add(new(query, 1), Codecs.Int64, "Id");
            var optional = builder.Add(new(query, 2), Codecs.NullableReference(Codecs.String), "Optional");
            Suite.Throws<ArgumentException>(() => builder.Add(new(query, 3), Codecs.Int64, "Id"));
            Suite.Throws<ArgumentException>(() => builder.Add(new(foreign, 1), Codecs.Int64, "Foreign"));
            Suite.Throws<ArgumentException>(() => builder.Add(default, Codecs.Int64, "Default"));
            var called = 0;
            var projection = builder.Build(row => { called++; return new(row.Get(id), row.Get(optional)); });
            Suite.Check(ReferenceEquals(projection, published) && expressions is [{ Value: 1 }, { Value: 2 }], "Frozen native references and token identity");
            Suite.Check(id.Ordinal == 0 && optional.Name == "Optional", "Typed token metadata");
            var good = new ShapeOracle(ShapeOracle.Value("Id", Codecs.Int64, 7L), ShapeOracle.Value<string?>("Optional", Codecs.NullableReference(Codecs.String), null));
            var actual = projection.Read(good);
            Suite.Check(actual == new SampleRow(7, null) && called == 1, $"Present nullable NULL reaches direct typed constructor: {actual}; calls={called}");
            var failures = new[]
            {
                new ShapeOracle(ShapeOracle.Value("Id", Codecs.Int64, 7L)),
                new ShapeOracle(ShapeOracle.Value("Id", Codecs.Int64, 7L), ShapeOracle.Value("Id", Codecs.NullableReference(Codecs.String), "wrong alias")),
                new ShapeOracle(ShapeOracle.Value("Id", Codecs.Int64, 7L), ShapeOracle.Value("Optional", Codecs.Nullable(Codecs.Int64), (long?)3)),
                new ShapeOracle(ShapeOracle.Value("Optional", Codecs.NullableReference(Codecs.String), "order"), ShapeOracle.Value("Id", Codecs.Int64, 7L)),
                new ShapeOracle(ShapeOracle.Value("Id", Codecs.Int64, 7L), ShapeOracle.Value("Optional", Codecs.String, "nullability")),
            };
            foreach (var row in failures)
            {
                var failure = Suite.Throws<DatabaseException>(() => projection.Read(row));
                Suite.Check(failure.Kind == DatabaseErrorKind.ShapeMismatch && called == 1, "Shape mismatch precedes nullable getter/constructor");
            }
            var absent = new ShapeOracle(new("Id", Codecs.Int64.Descriptor, null), ShapeOracle.Value<string?>("Optional", Codecs.NullableReference(Codecs.String), null));
            Suite.Check(Suite.Throws<DatabaseException>(() => projection.Read(absent)).Kind == DatabaseErrorKind.Conversion && called == 1, "Nonnullable NULL is conversion failure before constructor");
            Suite.Throws<InvalidOperationException>(() => builder.Add(new(query, 3), Codecs.Int64, "AfterBuild"));
            Suite.Throws<InvalidOperationException>(() => builder.Build(_ => new(1, null)));
            good.Retire(); Suite.Throws<ObjectDisposedException>(() => projection.Read(good));
            var foreignBuilder = new ProjectionDefinition<long>(query, (_, _) => { });
            var foreignToken = foreignBuilder.Add(new(query, 3), Codecs.Int64, "Other");
            var bad = foreignBuilder.Build(row => row.Get(id));
            Suite.Throws<ArgumentException>(() => bad.Read(new ShapeOracle(ShapeOracle.Value("Other", Codecs.Int64, 1L))));
            Suite.Throws<InvalidOperationException>(() => _ = default(ResultColumn<long>).Ordinal);
            var empty = new ProjectionDefinition<long>(query, (_, _) => { });
            Suite.Throws<InvalidOperationException>(() => empty.Build(_ => 0));
            Suite.Throws<ArgumentException>(() => Projection<long>.Create([ResultField.Create("X", Codecs.Int64), ResultField.Create("X", Codecs.Int64)], _ => 0));
            var sentinel = new MappingFailure("constructor oracle");
            var throwing = Projection<long>.Create([ResultField.Create("X", Codecs.Int64)], _ => throw sentinel);
            Suite.Check(ReferenceEquals(sentinel, Suite.Throws<MappingFailure>(() => throwing.Read(new ShapeOracle(ShapeOracle.Value("X", Codecs.Int64, 1L))))), "Original constructor exception identity preserved");
            query.Dispose(); Suite.Throws<ObjectDisposedException>(() => empty.Add(new(query, 3), Codecs.Int64, "Closed"));
        }
        Suite.Check(_releases == before + 2, "Construction-only native owner release balance");
    }
    internal sealed record SampleRow(long Id, string? Optional);
}
internal sealed class MappingFailure(string message) : Exception(message);
