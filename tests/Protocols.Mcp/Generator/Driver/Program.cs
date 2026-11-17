using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Emit;
using NativeWeave.Server.Mcp.Generator;

int assertions = 0;
var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
    .Select(static path => MetadataReference.CreateFromFile(path)).ToArray();
string Generate(string source, bool stale = false, IIncrementalGenerator? additional = null, MetadataReference? reference = null)
{
    var compilation = CSharpCompilation.Create("LayoutFixture", [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview))], reference is null ? references : [.. references, reference],
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    var generators = additional is null ? new ISourceGenerator[] { new LayoutPreservationGenerator().AsSourceGenerator() }
        : [new LayoutPreservationGenerator().AsSourceGenerator(), additional.AsSourceGenerator()];
    GeneratorDriver driver = CSharpGeneratorDriver.Create(generators).WithUpdatedParseOptions(new CSharpParseOptions(LanguageVersion.Preview));
    driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out var diagnostics);
    Check(!diagnostics.Any(static d => d.Severity == DiagnosticSeverity.Error), "generator diagnostics");
    Check(!updated.GetDiagnostics().Any(static d => d.Severity == DiagnosticSeverity.Error), string.Join("\n", updated.GetDiagnostics()));
    var proof = updated.WithAnalyzers([new LayoutProofAnalyzer()]).GetAnalyzerDiagnosticsAsync().GetAwaiter().GetResult();
    Check(proof.Count(static diagnostic => diagnostic.Id == "NWMCP001") == (stale ? 1 : 0), "final proof validation: " + string.Join("\n", proof));
    Check(!proof.Any(static diagnostic => diagnostic.Id == "AD0001"), "analyzer must not throw");
    return string.Join("\n", updated.SyntaxTrees.Skip(1).Select(static t => t.ToString()));
}
void Check(bool condition, string message)
{ if (!condition) throw new InvalidOperationException(message); assertions++; }

const string source = """
    using System;
    using System.Collections.Generic;
    using System.Runtime.InteropServices;
    using System.Text.Json.Serialization;
    [JsonSerializable(typeof(Root))]
    internal class Context { }
    internal sealed class Root : GenericBase<Padded>
    {
        public Child[] Items { get; set; } = [];
        public Optional? Value { get; set; }
        public List<Child> Values { get; set; } = [];
        [JsonIgnore] public Unrelated? Ignore { get; set; }
        private Unrelated? Hidden;
    }
    internal class GenericBase<T> { private T? Hidden; public int Number { get; set; } }
    internal struct Optional { public NullableChild? Child { get; set; } }
    internal sealed class NullableChild { public int Number { get; set; } }
    internal sealed class Child { public int Number { get; set; } }
    internal sealed class Unrelated { public int Number { get; set; } }
    [StructLayout(LayoutKind.Sequential, Size=32768)]
    internal struct Padded { private byte Value; }
    """;
string output = Generate(source);
Check(output == Generate(source), "deterministic generation");
Check(output.Contains("definition == typeof(global::Root)", StringComparison.Ordinal), "root proof");
Check(output.Contains("typeof(global::GenericBase<global::Padded>)", StringComparison.Ordinal), "closed generic base dependency");
Check(output.Contains("definition == typeof(global::GenericBase<>)", StringComparison.Ordinal), "generic definition proof");
Check(output.Contains("definition == typeof(global::Child)", StringComparison.Ordinal), "array/list element closure");
Check(output.Contains("definition == typeof(global::NullableChild)", StringComparison.Ordinal), "nullable value reference closure");
Check(!output.Contains("Unrelated", StringComparison.Ordinal), "ignored/private reference does not expand heap closure");
Check(!output.Contains("definition == typeof(global::Padded)", StringComparison.Ordinal), "inline values need no reference layout proof");
Check(output.Contains("ModuleInitializer", StringComparison.Ordinal), "annotations attached to reachable initializer");
Check(!output.Contains("AssemblyMetadata", StringComparison.Ordinal), "no whole-assembly attribute proof");

output = Generate("""
    using System.Text.Json.Serialization;
    internal class Container
    {
        private class Base<T> { private T? Hidden; }
        private sealed class Dto : Base<decimal> { public int Number { get; set; } }
        [JsonSerializable(typeof(Dto))] internal class Context { }
    }
    """);
Check(output.Contains("\"Container.Base`1\", \"LayoutFixture\"", StringComparison.Ordinal), "inaccessible generic base documentation target");
Check(output.Contains("\"Container.Dto\", \"LayoutFixture\"", StringComparison.Ordinal), "inaccessible DTO documentation target");
Check(output.Contains("name == \"Container+Base`1\"", StringComparison.Ordinal), "inaccessible generic proof uses reflection name");
Check(!output.Contains("typeof(global::Container.", StringComparison.Ordinal), "no inaccessible typeof expression");

output = Generate("""
    internal sealed class Input { public Child? Child { get; set; } }
    internal sealed class Child { public int Number { get; set; } }
    internal sealed class Output { public int Number { get; set; } }
    internal static class Application
    {
        internal static void Register(NativeWeave.Server.Mcp.McpRegistry registry) => registry.AddTool(new Input(), new Output());
    }
    namespace NativeWeave.Server.Mcp
    {
        internal sealed class McpRegistry { internal void AddTool<TInput, TOutput>(TInput input, TOutput output) { } }
    }
    """);
Check(output.Contains("definition == typeof(global::Input)", StringComparison.Ordinal), "inferred AddTool input discovery");
Check(output.Contains("definition == typeof(global::Output)", StringComparison.Ordinal), "inferred AddTool output discovery");
Check(output.Contains("definition == typeof(global::Child)", StringComparison.Ordinal), "AddTool transitive closure");

output = Generate("""
    using System.Collections.Generic;
    using System.Text.Json.Serialization;
    [JsonSerializable(typeof(Expanding<int>))] internal class Context { }
    internal class Expanding<T> { public Expanding<List<T>>? Next { get; set; } }
    """);
Check(output.Length < 100_000, "expanding generic discovery remains finite");
Check(output.Split("DynamicDependency", StringSplitOptions.None).Length - 1 <= 33, "finite depth before source emission");

const string partial = """
    using System.Text.Json.Serialization;
    [JsonSerializable(typeof(PartialDto))] internal class Context { }
    internal partial class PartialDto { }
    """;
Generate(partial, stale: true, new ExtraGenerator("internal partial class PartialDto { private long _late; }"));
Generate(partial, stale: false, new ExtraGenerator("internal partial class PartialDto { internal long Read() => 1; }"));
Generate("""
    using System;
    using System.Text.Json.Serialization;
    [JsonSerializable(typeof(PrimaryDto))] internal class Context { }
    internal class PrimaryDto(int number)
    {
        public int Number => number;
        public event Action? Changed;
        public void Raise() => Changed?.Invoke();
    }
    """);
var external = CSharpCompilation.Create("RefOnly", [CSharpSyntaxTree.ParseText("public sealed class RefDto { private long _hidden; public int Number { get; set; } }")], references,
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
using var refBytes = new MemoryStream();
Check(external.Emit(refBytes, options: new EmitOptions(metadataOnly: true, includePrivateMembers: false)).Success, "actual reference-assembly fixture emission");
output = Generate("""
    using System.Text.Json.Serialization;
    [JsonSerializable(typeof(RefDto))] internal class Context { }
    """, reference: MetadataReference.CreateFromImage(refBytes.ToArray()));
Check(!output.Contains("definition == typeof(global::RefDto)", StringComparison.Ordinal), "ref-only imported types receive no field count proof");
Check(output.Contains("DynamicDependency", StringComparison.Ordinal), "ref-only field retention may coexist with absent count proof");

Generate("""
    using System.Text.Json.Serialization;
    [JsonSerializable(typeof(PartialDto))] internal class Context { }
    internal partial interface Surface { }
    internal class PartialDto : Surface { }
    """, stale: true, new ExtraGenerator("internal partial interface Surface { private static readonly object Hidden = new(); }"));
Console.WriteLine($"MCP layout generator: {assertions} assertions passed.");

internal sealed class ExtraGenerator(string source) : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context) =>
        context.RegisterSourceOutput(context.CompilationProvider, (output, _) => output.AddSource("ExtraDto.g.cs", source));
}
