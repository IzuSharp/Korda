using System.Collections.Immutable;
using Korda.Query.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

internal static class ProjectionExperienceChecks
{
    internal static void Run(IEnumerable<MetadataReference> references, CSharpCompilationOptions options)
    {
        const string prefix = "#nullable enable\nusing System; using Korda.Data; using Korda.Query;\n";
        int checks = 0;
        string Compile(string source, string? expected = null)
        {
            var input = CSharpCompilation.Create("DbApiExperience", [CSharpSyntaxTree.ParseText(prefix + source)], references, options.WithGeneralDiagnosticOption(ReportDiagnostic.Error));
            GeneratorDriver driver = CSharpGeneratorDriver.Create(new ModelGenerator().AsSourceGenerator());
            driver = driver.RunGeneratorsAndUpdateCompilation(input, out var result, out var diagnostics);
            var errors = diagnostics.AddRange(result.GetDiagnostics()).Where(x => x.Severity == DiagnosticSeverity.Error).ToArray();
            if (errors.Any(x => x.Id == "CS8785")) throw new InvalidOperationException("Generator crashed: " + string.Join("\n", errors.Select(x => x.ToString())));
            if (expected is null)
            {
                if (errors.Length != 0) throw new InvalidOperationException(string.Join("\n", errors.Select(x => x.ToString())));
                using var emitted = new MemoryStream();
                var emission = result.Emit(emitted);
                if (!emission.Success) throw new InvalidOperationException(string.Join("\n", emission.Diagnostics));
            }
            else if (!errors.Any(x => x.Id == expected)) throw new InvalidOperationException("Missing " + expected + ": " + string.Join("\n", errors.Select(x => x.ToString())));
            checks++;
            return string.Join("\n", driver.GetRunResult().Results.Single().GeneratedSources.Select(x => x.SourceText.ToString()));
        }

        var convention = Compile("[WeaveTable] public sealed record Order(long Id, string? Note);");
        if (!convention.Contains("TableBuilder<global::Order>(\"Order\", null)", StringComparison.Ordinal) || !convention.Contains("IsPrimaryKey = true", StringComparison.Ordinal)) throw new InvalidOperationException("Convention metadata was not emitted.");
        var legacy = Compile("[WeaveTable(\"named\")] public sealed record Order(long Id);");
        if (legacy.Contains("IsPrimaryKey = true", StringComparison.Ordinal)) throw new InvalidOperationException("Existing named-table key behavior changed.");
        Compile("[WeaveTable(InferPrimaryKey=false)] public sealed record NoKey(string Name);");
        Compile("[WeaveTable(\"keyed\",InferPrimaryKey=true)] public sealed record Composite([property:WeaveKey] long Tenant,[property:WeaveKey] long Number);");
        const string projection = "[WeaveProjection(DescriptorName=\"Rows\")] public sealed record Summary([property:WeaveColumn(\"order_id\")] long Id, long Count);";
        var generated = Compile(projection + " public sealed record Root(long Id); public static class Calls { public static Projection<Summary> Map(Query<Root> query, SqlExpression<long> id, SqlExpression<long> count) => Rows.Bind(query,Id:id,Count:count); }");
        if (generated.Contains("TableBuilder", StringComparison.Ordinal) || !generated.Contains("\"order_id\"", StringComparison.Ordinal) || !generated.Contains(".Add(@Count", StringComparison.Ordinal)) throw new InvalidOperationException("DTO generation selected a table or lost an explicit expression.");
        Compile(projection + " public sealed record Root(long Id); public static class Calls { public static Projection<Summary> Map(Query<Root> query, SqlExpression<string> id, SqlExpression<long> count) => Rows.Bind(query,Id:id,Count:count); }", "CS1503");
        Compile("[WeaveProjection] public sealed record Shadows(long query,long builder,long row,long TEntity,long __Codec0);");
        Compile("[WeaveProjection(DescriptorName=\"__Codec0\")] public sealed record CodecShadow(long Id);");
        Compile("[WeaveProjection(DescriptorName=\"TEntity\")] public sealed record GenericShadow(long Id);");
        Compile("[WeaveProjection] public sealed class Chosen { public long Id {get;init;} public string? Name {get;init;} public Chosen(){} [WeaveConstructor] public Chosen([WeaveParameter(nameof(Id))] long key,string? name) {Id=key;Name=name;} }");
        Compile("[assembly:WeaveCodec(typeof(Id),typeof(Policies),nameof(Policies.Id))] [assembly:WeaveCodec(typeof(decimal),typeof(Policies),nameof(Policies.Money))] public readonly record struct Id(long Value); public static class Policies { public static ValueCodec<Id> Id {get;} = Codecs.Convert<Id,long>(Codecs.Int64,static v=>v.Value,static v=>new(v)); public static readonly ValueCodec<decimal> Money = Codecs.FixedScaleMoney(2); public static ValueCodec<decimal> Numeric => Codecs.Decimal(18,2); } [WeaveProjection] public sealed record Summary(Id Id,Id? Optional,decimal Money,decimal? Discount,[property:WeaveUseCodec(typeof(Policies),nameof(Policies.Numeric))] decimal Tax);");

        var failures = new (string Id, string Source)[]
        {
            ("NWQ0104", "[WeaveTable] public sealed record Missing(string Name);"),
            ("NWQ0102", "[WeaveTable] public sealed record Nullable(long? Id);"),
            ("NWQ0104", "[WeaveTable] public sealed record Ambiguous(long Id,long ID);"),
            ("NWQ0104", "[WeaveTable(\"both\"),WeaveProjection] public sealed record Both(long Id);"),
            ("NWQ0104", "[WeaveProjection] public sealed record KeyDto([property:WeaveKey] long Id);"),
            ("NWQ0104", "[WeaveProjection] public sealed record Aliases([property:WeaveColumn(\"x\")] long Id,[property:WeaveColumn(\"x\")] long Count);"),
            ("NWQ0103", "[WeaveProjection] public sealed class Hidden { public long Id {get;} [WeaveConstructor] private Hidden(long id){Id=id;} }"),
            ("NWQ0103", "[WeaveProjection] public sealed class Multiple { public long Id {get;init;} [WeaveConstructor] public Multiple(){} [WeaveConstructor] public Multiple(long id){Id=id;} }"),
            ("NWQ0103", "[WeaveProjection] public sealed class Duplicate { public long Id {get;} public Duplicate([WeaveParameter(nameof(Id))] long a,[WeaveParameter(nameof(Id))] long b){Id=a;} }"),
            ("NWQ0103", "[WeaveProjection] public sealed class Missing { public long Id {get;} public Missing([WeaveParameter(\"Missing\")] long key){Id=key;} }"),
            ("NWQ0103", "[WeaveProjection] public sealed class CaseNames { public int Id {get;} public long ID {get;} public CaseNames(int id,long ID){Id=id;this.ID=ID;} }"),
            ("NWQ0102", "[WeaveProjection] public sealed class NullDto { public string? Name {get;} public NullDto(string name){Name=name;} }"),
            ("NWQ0104", "[assembly:WeaveCodec(typeof(long),typeof(Policies),nameof(Policies.Value))] [assembly:WeaveCodec(typeof(long),typeof(Policies),nameof(Policies.Value))] public static class Policies { public static ValueCodec<long> Value=>Codecs.Int64; } [WeaveProjection] public sealed record Dto(long Id);"),
            ("NWQ0104", "public static class Policies { private static ValueCodec<long> Value=>Codecs.Int64; } [WeaveProjection] public sealed record Dto([property:WeaveUseCodec(typeof(Policies),\"Value\")] long Id);"),
            ("NWQ0104", "[WeaveProjection] public sealed record Dto([property:WeaveUseCodec(typeof(Dto),\"Policy\")] long Id) { private static ValueCodec<long> Policy=>Codecs.Int64; }"),
            ("NWQ0104", "public static class Policies { public static ValueCodec<long>? Value=>null; } [WeaveProjection] public sealed record Dto([property:WeaveUseCodec(typeof(Policies),nameof(Policies.Value))] long Id);"),
            ("NWQ0104", "file static class Policies { public static ValueCodec<long> Value=>Codecs.Int64; } [WeaveProjection] public sealed record Dto([property:WeaveUseCodec(typeof(Policies),nameof(Policies.Value))] long Id);"),
            ("NWQ0104", "public static class Policies { public static ValueCodec<int> Value=>Codecs.Int32; } [WeaveProjection] public sealed record Dto([property:WeaveUseCodec(typeof(Policies),nameof(Policies.Value))] long Id);"),
            ("NWQ0104", "public static class Policies { public static ValueCodec<long?> Value=>Codecs.Nullable(Codecs.Int64); } [WeaveProjection] public sealed record Dto([property:WeaveUseCodec(typeof(Policies),nameof(Policies.Value))] long? Id);"),
            ("NWQ0101", "[WeaveProjection] public sealed record Unsupported(decimal Amount);")
        };
        foreach (var failure in failures) Compile(failure.Source, failure.Id);
        Console.WriteLine($"DB-API-01 compiler: {checks} convention/projection/policy and negative mapping checks; generated Bind compiled against actual Query types.");
    }
}
