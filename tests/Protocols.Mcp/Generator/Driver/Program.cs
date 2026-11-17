using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NativeWeave.Server.Mcp.Generator;

int assertions = 0;
var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
    .Select(static path => MetadataReference.CreateFromFile(path)).ToArray();
string Generate(string source)
{
    var compilation = CSharpCompilation.Create("LayoutFixture", [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview))], references,
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    GeneratorDriver driver = CSharpGeneratorDriver.Create(new LayoutPreservationGenerator()).WithUpdatedParseOptions(new CSharpParseOptions(LanguageVersion.Preview));
    driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out var diagnostics);
    Check(!diagnostics.Any(static d => d.Severity == DiagnosticSeverity.Error), "generator diagnostics");
    Check(!updated.GetDiagnostics().Any(static d => d.Severity == DiagnosticSeverity.Error), string.Join("\n", updated.GetDiagnostics()));
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
Check(output.Contains("LayoutFixture|Root", StringComparison.Ordinal), "root marker");
Check(output.Contains("typeof(global::GenericBase<global::Padded>)", StringComparison.Ordinal), "closed generic base dependency");
Check(output.Contains("LayoutFixture|GenericBase`1", StringComparison.Ordinal), "generic definition marker");
Check(output.Contains("LayoutFixture|Child", StringComparison.Ordinal), "array/list element closure");
Check(output.Contains("LayoutFixture|NullableChild", StringComparison.Ordinal), "nullable value reference closure");
Check(!output.Contains("Unrelated", StringComparison.Ordinal), "ignored/private reference does not expand heap closure");
Check(!output.Contains("LayoutFixture|Padded", StringComparison.Ordinal), "inline values need no reference layout marker");
Check(output.Contains("ModuleInitializer", StringComparison.Ordinal), "annotations attached to reachable initializer");

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
Check(output.Contains("LayoutFixture|Container+Base`1", StringComparison.Ordinal), "inaccessible generic marker uses reflection name");
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
Check(output.Contains("LayoutFixture|Input", StringComparison.Ordinal), "inferred AddTool input discovery");
Check(output.Contains("LayoutFixture|Output", StringComparison.Ordinal), "inferred AddTool output discovery");
Check(output.Contains("LayoutFixture|Child", StringComparison.Ordinal), "AddTool transitive closure");

output = Generate("""
    using System.Collections.Generic;
    using System.Text.Json.Serialization;
    [JsonSerializable(typeof(Expanding<int>))] internal class Context { }
    internal class Expanding<T> { public Expanding<List<T>>? Next { get; set; } }
    """);
Check(output.Length < 100_000, "expanding generic discovery remains finite");
Check(output.Split("DynamicDependency", StringSplitOptions.None).Length - 1 <= 32, "finite depth before source emission");
Console.WriteLine($"MCP layout generator: {assertions} assertions passed.");
