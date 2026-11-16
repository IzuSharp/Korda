using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NativeWeave.Data;
using NativeWeave.Query.Internal;

// Internal maintainer sample. Its explicitly supplied native fixture is built once
// before these ordinary managed builds; it is not a production B04 DataSource.
unsafe
{
    if (args.Length == 0) throw new ArgumentException("Supply the precompiled query binding fixture path.");
    if (args.Contains("--require-aot", StringComparer.Ordinal) && RuntimeFeature.IsDynamicCodeSupported) throw new InvalidOperationException("Native AOT execution required.");
    var library = NativeLibrary.Load(Path.GetFullPath(args[0]));
    try
    {
        var calls = new NativeCalls("query.binding.fixture", name => NativeLibrary.TryGetExport(library, name, out var address) ? address : 0);
        var open = (delegate* unmanaged[Cdecl]<nint*, nint*, uint>)NativeLibrary.GetExport(library, "nw_test_open_v1");
        nint nativeSource = 0, nativeSession = 0;
        if (open(&nativeSource, &nativeSession) != 0) throw new InvalidOperationException("Fixture owner creation failed.");
        var sourceIdentity = new SourceBinding(calls, 1);
        using var source = new DatabaseOwner(sourceIdentity, DatabaseOwnerKind.Source, nativeSource);
        using var session = new DatabaseOwner(sourceIdentity, DatabaseOwnerKind.Session, nativeSession);
        var builder = new TableBuilder<Item>("items"); var id = builder.Column("id", Codecs.Int64, new() { IsPrimaryKey = true }); var label = builder.Column("label", Codecs.String);
        var table = builder.Build(row => new(row.Get(id), row.Get(label)), (row, value) => { row.Set(id, value.Id); row.Set(label, value.Label); });
        using var model = MetadataBindings.RegisterModel(source, table);
        using var query = QueryBinding.Create(session, model, table.Identity.Id);
        var idExpression = query.Column(id.Id); var labelExpression = query.Column(label.Id); var wanted = query.Value(OperandEncoding.Value(1, Codecs.Int64, 3, 42L));
        var predicate = query.Binary(1, idExpression, wanted);
        query.Clause(1, OperandEncoding.Clause(OperandEncoding.U64(1, predicate.In(query))));
        var fields = new[] { idExpression, labelExpression };
        query.Clause(6, OperandEncoding.Clause(OperandEncoding.Bytes(1, OperandEncoding.Tokens(query, fields), 8), OperandEncoding.Bytes(2, OperandEncoding.Projection(table.Row, codec => codec.Id, fields.Select(field => field.In(query)).ToArray()))));
        // Advanced composition: clone/seal/import while the original stays mutable.
        // The temporary source admission ends before destination mutation admission.
        var snapshot = query.SnapshotAndImport(query);
        query.Clause(9, OperandEncoding.Clause(OperandEncoding.U64(1, snapshot.In(query)), OperandEncoding.U32(2, 1)));
        using var plan = query.Seal(0);
        Console.WriteLine($"PASS query binding sample: real native direct calls; static metadata; self-composition; dynamicCode={RuntimeFeature.IsDynamicCodeSupported}");
    }
    finally { NativeLibrary.Free(library); }
}
internal sealed record Item(long Id, string Label);
