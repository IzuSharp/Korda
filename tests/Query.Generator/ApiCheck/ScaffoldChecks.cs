using Korda.Database;
using Korda.Query.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal static class ScaffoldCompilerChecks
{
    internal static void Run(IEnumerable<MetadataReference> references, CSharpCompilationOptions options)
    {
        int checks = 0;
        string[] Compile(DatabaseProvider provider, SchemaTable table, SchemaScaffoldOptions scaffold, string extra = "")
        {
            // Exercise the actual pure scaffolder. A hand-copied fixture would miss
            // constructor-order or escaping regressions in its emitted source.
            var source = SchemaScaffolder.Generate(provider, table, scaffold).Source;
            var input = CSharpCompilation.Create("ScaffoldedConsumer",
                [CSharpSyntaxTree.ParseText(source, path: "scaffolded.cs"), CSharpSyntaxTree.ParseText("#nullable enable\n" + extra, path: "calls.cs")],
                references, options.WithGeneralDiagnosticOption(ReportDiagnostic.Error));
            GeneratorDriver driver = CSharpGeneratorDriver.Create(new ModelGenerator().AsSourceGenerator());
            driver = driver.RunGeneratorsAndUpdateCompilation(input, out var result, out var diagnostics);
            var errors = diagnostics.AddRange(result.GetDiagnostics()).Where(x => x.Severity == DiagnosticSeverity.Error).ToArray();
            if (errors.Length != 0) throw new InvalidOperationException("Scaffold compilation failed:\n" + source + "\n" + string.Join("\n", errors.Select(x => x.ToString())));
            using var emitted = new MemoryStream();
            var emission = result.Emit(emitted);
            if (!emission.Success) throw new InvalidOperationException(string.Join("\n", emission.Diagnostics));
            var generated = driver.GetRunResult().Results.Single().GeneratedSources.Single().SourceText;
            var columnAssignments = CSharpSyntaxTree.ParseText(generated).GetRoot().DescendantNodes()
                .OfType<AssignmentExpressionSyntax>()
                .Where(x => x.Right is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "builder" }, Name.Identifier.ValueText: "Column" } })
                .Select(x => ((MemberAccessExpressionSyntax)x.Left).Name.Identifier.ValueText).ToArray();
            checks++;
            return columnAssignments;
        }
        void Order(string[] actual, params string[] expected)
        {
            if (!actual.SequenceEqual(expected)) throw new InvalidOperationException("Scaffolded mapping order changed: " + string.Join(", ", actual));
            checks++;
        }

        var composite = new SchemaTable(null, "orders", [new("Id", "INTEGER", false, true), new("Tenant", "INTEGER", false, true), new("Note", "TEXT", true, false)]);
        Order(Compile(DatabaseProvider.Sqlite, composite, new("Scaffolded", "OrderCopy") { PrimaryKeyOrder = ["Tenant", "Id"], GeneratedColumns = [] },
            "namespace Scaffolded; public static class Calls { public static global::Korda.Data.EntityKey<OrderCopy> Key() => OrderCopyMapping.Table.Key(OrderCopyMapping.Tenant, 7L).And(OrderCopyMapping.Id, 42L); }"), "Tenant", "Id", "Note");

        var caseDistinct = new SchemaTable("public", "case_distinct", [new("Id", "integer", false, true), new("ID", "bigint", false, true), new("Note", "text", true, false)]);
        Order(Compile(DatabaseProvider.PostgreSql, caseDistinct, new("Scaffolded", "CaseDistinct") { PrimaryKeyOrder = ["ID", "Id"], GeneratedColumns = [] },
            "namespace Scaffolded; public static class Calls { public static global::Korda.Data.EntityKey<CaseDistinct> Key() => CaseDistinctMapping.Table.Key(CaseDistinctMapping.ID, 7L).And(CaseDistinctMapping.Id, 42); }"), "ID", "Id", "Note");

        var escaped = new SchemaTable("quote\"\n\u0085\u2028\u2029", "order\"\r\n\t\b\\s", [
            new("Table", "text", true, false), new("Row", "text", true, false), new("Entity", "text", true, false),
            new("EntityMapping", "text", true, false), new("class", "text", true, false), new("__arglist", "text", true, false),
            new("x-\"\u2028y", "text", true, false)]);
        Order(Compile(DatabaseProvider.PostgreSql, escaped, new("ScaffoldEscaped", "Entity") { GeneratedColumns = [] }),
            "Column1", "Column2", "Column3", "Column4", "Column5", "Column6", "Column7");

        var hinted = new SchemaTable("billing", "payments", [new("Id", "bigint", false, true), new("Total", "numeric(18,2)", true, false)]);
        Order(Compile(DatabaseProvider.PostgreSql, hinted, new("ScaffoldHints", "Payment")
        {
            GeneratedColumns = [],
            ColumnHints = [new("Id", "global::ScaffoldHints.Id", "global::ScaffoldHints.Policies", "Id"), new("Total", "decimal", "global::ScaffoldHints.Policies", "Money", "Amount")]
        }, "namespace ScaffoldHints; public readonly record struct Id(long Value); public static class Policies { public static global::Korda.Data.ValueCodec<Id> Id => global::Korda.Data.Codecs.Convert<Id,long>(global::Korda.Data.Codecs.Int64, static value => value.Value, static value => new(value)); public static global::Korda.Data.ValueCodec<decimal> Money => global::Korda.Data.Codecs.Decimal(18,2); }"), "Id", "Amount");

        Console.WriteLine($"DB-API-01 scaffold compiler: {checks} actual emitted-source compilation/order checks; composite keys, exact case-distinct parameters, reserved/escaped names and custom nullable codec hints passed.");
    }
}
