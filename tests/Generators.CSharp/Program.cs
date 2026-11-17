using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using NativeWeave.Generators.CSharp;
using NativeWeave.Generators.CSharp.Metadata;

if (args.Length is < 5 or > 6) throw new ArgumentException("sidecar adapter inventory symbols compiled-consumer [--write-reference]");
var sidecar = File.ReadAllBytes(args[0]); var adapter = File.ReadAllBytes(args[1]);
var inventory = File.ReadAllBytes(args[2]); var linked = File.ReadAllLines(args[3]);
var assertions = 0;
void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); assertions++; }
void Reject(Action action, string code)
{
    try { action(); }
    catch (Exception e) when (e.GetType().FullName == "NativeWeave.Generators.Model.BindingValidationException" &&
        Equals(e.GetType().GetProperty("Code", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(e), code))
    { assertions++; return; }
    throw new InvalidOperationException("Accepted invalid " + code);
}
var rejectsNoop = false;
try { Reject(() => { }, "NWGEN001"); }
catch (InvalidOperationException) { rejectsNoop = true; }
Check(rejectsNoop, "negative helper fails when the action accepts input");
byte[] Edit(byte[] input, Action<JsonNode> change) { var value = JsonNode.Parse(input)!; change(value); return Encoding.UTF8.GetBytes(value.ToJsonString()); }
var emitted = BindingEmitter.Emit(sidecar, adapter, inventory, linked, "nativeweave_exports_conformance", "NativeWeave.Generated.Conformance");
Check(emitted == BindingEmitter.Emit(sidecar, adapter, inventory, linked.Reverse(), "nativeweave_exports_conformance", "NativeWeave.Generated.Conformance"), "deterministic emission");
Check(!emitted.Source.Contains("DllImport(", StringComparison.Ordinal) && !emitted.Source.Contains("SuppressGCTransition", StringComparison.Ordinal), "framework imports and legal GC transitions");
Check(emitted.Source.Contains("NativeResourceOwner", StringComparison.Ordinal) && emitted.Source.Contains(".Resource.Release(ref ", StringComparison.Ordinal), "real A03 lifetime reuse");
Check(emitted.Source.Contains("StartExited(", StringComparison.Ordinal) && emitted.Source.Contains("MappingFailed();", StringComparison.Ordinal) && emitted.Source.Contains("RequestRetire();", StringComparison.Ordinal), "D06 static coordinator guards");
Check(!CSharpSyntaxTree.ParseText(emitted.Source, new CSharpParseOptions(LanguageVersion.CSharp15)).GetDiagnostics().Any(x => x.Severity == DiagnosticSeverity.Error), "generated syntax");
Reject(() => BindingEmitter.Emit(sidecar, adapter, inventory, linked, "../../bad"), "NWGEN001");
Reject(() => BindingEmitter.Emit(sidecar, adapter, inventory, linked, "native", "Bad.Generic<T>"), "NWGEN001");
Reject(() => BindingEmitter.Emit(sidecar, adapter, inventory, linked.Skip(1), "native"), "NWABI002");
Reject(() => BindingEmitter.Emit(sidecar, adapter.Concat(new byte[] { 32 }).ToArray(), inventory, linked, "native"), "NWGEN001");
Reject(() => BindingEmitter.Emit(sidecar, adapter, Edit(inventory, x => x["exports"]![0]!["generic"] = true), linked, "native"), "NWABI001");
Reject(() => BindingEmitter.Emit(Edit(sidecar, x => x["exports"]![0]!["parameters"]![0]!["type"] = "bool"), adapter, inventory, linked, "native"), "NWABI001");
Reject(() => BindingEmitter.Emit(Edit(sidecar, x => { x["owners"]![0]!["release_executor"] = "owner-thread"; x["exports"]!.AsArray().Single(v => v!["symbol"]!.GetValue<string>() == "nw_exports_release_v1")!["thread"] = "owner-thread"; }), adapter, inventory, linked, "native"), "NWOWN001");
Reject(() => BindingEmitter.Emit(Edit(sidecar, x => x["exports"]!.AsArray().Single(v => v!["symbol"]!.GetValue<string>() == "nw_exports_read_v1")!["parameters"]![0]!["borrow_end"] = "retirement"), adapter, inventory, linked, "native"), "NWASYNC001");
foreach (var reserved in new[] { "Operation_fixture", "IOperationSetup_fixture", "BindingCallbacks", "BindingOperations" })
    Reject(() => BindingEmitter.Emit(Edit(sidecar, x => x["records"]!.AsArray().Add(new JsonObject
    {
        ["name"] = reserved,
        ["size"] = 4,
        ["alignment"] = 4,
        ["fields"] = new JsonArray(new JsonObject { ["name"] = "value", ["type"] = "u32", ["offset"] = 0, ["reserved"] = false })
    })), adapter, inventory, linked, "native"), "NWGEN001");
var nullableOwner = BindingEmitter.Emit(Edit(sidecar, x => x["exports"]!.AsArray().Single(v => v!["symbol"]!.GetValue<string>() == "nw_exports_read_v1")!["parameters"]![0]!["nullable"] = true), adapter, inventory, linked, "native");
Check(nullableOwner.Source.Contains("Owner_NwFixtureOwner? @owner", StringComparison.Ordinal) &&
    nullableOwner.Source.Contains("@owner is null ? 0 : @owner.Resource.Acquire", StringComparison.Ordinal) &&
    nullableOwner.Source.Contains("if (@owner is not null) @owner.Resource.Release", StringComparison.Ordinal), "nullable owner signature and guarded lease preserve a null pointer");
// Parameter names are source compatibility inputs; generated locals never shadow them.
var collisions = BindingEmitter.Emit(Edit(sidecar, x => x["exports"]!.AsArray().Single(v => v!["symbol"]!.GetValue<string>() == "nw_exports_notification_v1")!["parameters"]![1]!["name"] = "__nw_status"), adapter, inventory, linked, "native");
Check(collisions.Source.Contains("__nw__result", StringComparison.Ordinal), "reserved-local collision avoidance");
using (var cancellation = new CancellationTokenSource())
{
    cancellation.Cancel();
    try { BindingEmitter.Emit(sidecar, adapter, inventory, linked, "native", cancellationToken: cancellation.Token); throw new Exception("Cancellation ignored"); }
    catch (OperationCanceledException) { assertions++; }
}

var metadata = new TextInput("Bindings.nwmetadata", emitted.Metadata);
var secondText = emitted.Metadata.Replace("module\tnativeweave.exports.conformance", "module\tnativeweave.exports.other", StringComparison.Ordinal);
var second = new TextInput("Other.nwmetadata", secondText);
var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p)).ToArray();
var emptySchema = Edit(sidecar, x => { foreach (var key in new[] { "exports", "records", "owners", "operations", "callbacks", "managed_symbols" }) x[key] = new JsonArray(); });
var emptyInventory = Edit(inventory, x => x["exports"] = new JsonArray());
var empty = BindingEmitter.Emit(emptySchema, adapter, emptyInventory, [], "native", "Empty.class");
var emptyCompilation = CSharpCompilation.Create("EmptyComponent", [CSharpSyntaxTree.ParseText(empty.Source, new CSharpParseOptions(LanguageVersion.CSharp15))], references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
Check(!emptyCompilation.GetDiagnostics().Any(x => x.Severity == DiagnosticSeverity.Error), "empty component compiles without a fake runtime");
var modules = new[] { "module.same-name", "module.same_name" }.Select(name =>
    BindingEmitter.Emit(Edit(emptySchema, x => x["module"]!["id"] = name), adapter, emptyInventory, [], "native").Source).ToArray();
Check(modules[0] != modules[1], "qualified module identities cannot collide by punctuation stripping");
var compilation = CSharpCompilation.Create("IncrementalConsumer", [CSharpSyntaxTree.ParseText("internal class Closed<T> { internal sealed class Nested {} }", new CSharpParseOptions(LanguageVersion.CSharp15))], references,
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
GeneratorDriver driver = CSharpGeneratorDriver.Create([new BindingMetadataGenerator().AsSourceGenerator()], [metadata, second],
    new CSharpParseOptions(LanguageVersion.CSharp15), driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var generated, out var diagnostics);
Check(!diagnostics.Any(x => x.Severity == DiagnosticSeverity.Error) && !generated.GetDiagnostics().Any(x => x.Severity == DiagnosticSeverity.Error), "compiler-compatible actual metadata output");
var before = driver.GetRunResult().Results[0].GeneratedSources.ToDictionary(x => x.HintName, x => x.SourceText.ToString());
Check(before.Count == 2 && before.Values.All(x => !x.Contains("LibraryImport", StringComparison.Ordinal)), "no peer-generator import dependency");
driver = driver.RunGenerators(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText("internal class UnrelatedEdit {}", new CSharpParseOptions(LanguageVersion.CSharp15))));
Check(before.All(x => driver.GetRunResult().Results[0].GeneratedSources.Single(s => s.HintName == x.Key).SourceText.ToString() == x.Value), "unrelated edit preserves generated bytes");
Check(driver.GetRunResult().Results[0].TrackedSteps["NativeWeaveMetadata"].SelectMany(x => x.Outputs).All(x => x.Reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged), "value-stable incremental cache");
var digestLine = emitted.Metadata.Split('\n').Single(x => x.StartsWith("digest\t", StringComparison.Ordinal));
var replacement = new TextInput(metadata.Path, emitted.Metadata.Replace(digestLine, "digest\t" + new string('a', 64), StringComparison.Ordinal));
driver = driver.ReplaceAdditionalText(metadata, replacement).RunGenerators(compilation);
var after = driver.GetRunResult().Results[0].GeneratedSources.ToDictionary(x => x.HintName, x => x.SourceText.ToString());
Check(before.Count(x => after[x.Key] != x.Value) == 1, "one semantic input changes one descriptor");
var duplicates = CSharpGeneratorDriver.Create([new BindingMetadataGenerator().AsSourceGenerator()], [metadata, new TextInput("Duplicate.nwmetadata", emitted.Metadata)], new CSharpParseOptions(LanguageVersion.CSharp15)).RunGenerators(compilation).GetRunResult();
Check(duplicates.Diagnostics.Any(x => x.Id == "NWGEN001") && duplicates.GeneratedTrees.Length == 0, "duplicate module hard failure without partial generation");
var malformed = CSharpGeneratorDriver.Create([new BindingMetadataGenerator().AsSourceGenerator()], [new TextInput("Bad.nwmetadata", emitted.Metadata + "unknown\tshape\n")], new CSharpParseOptions(LanguageVersion.CSharp15)).RunGenerators(compilation).GetRunResult();
Check(malformed.Diagnostics.Any(x => x.Id == "NWGEN001"), "unknown metadata row rejected");
try { CSharpGeneratorDriver.Create([new BindingMetadataGenerator().AsSourceGenerator()], [metadata], new CSharpParseOptions(LanguageVersion.CSharp15)).RunGenerators(compilation, new CancellationToken(true)); throw new Exception("Generator cancellation ignored"); }
catch (OperationCanceledException) { assertions++; }

foreach (var path in new[] { typeof(BindingEmitter).Assembly.Location, args[4] })
{
    using var stream = File.OpenRead(path); using var pe = new PEReader(stream); var data = pe.GetMetadataReader();
    Check(!data.TypeDefinitions.Any(x => (data.GetTypeDefinition(x).Attributes & TypeAttributes.VisibilityMask) == TypeAttributes.Public), "no public raw or maintainer application types: " + path);
}
var host = typeof(BindingMetadataGenerator).Assembly;
Check(host.GetExportedTypes().SequenceEqual([typeof(BindingMetadataGenerator)]), "exact compiler plugin type surface");
Check(host.GetCustomAttribute<System.Runtime.Versioning.TargetFrameworkAttribute>()!.FrameworkName == ".NETStandard,Version=v2.0", "compiler host separated from net11 facade");
var xml = XDocument.Load(Path.ChangeExtension(host.Location, ".xml"));
var expectedIds = new[] { "T:NativeWeave.Generators.CSharp.Metadata.BindingMetadataGenerator", "M:NativeWeave.Generators.CSharp.Metadata.BindingMetadataGenerator.#ctor", "M:NativeWeave.Generators.CSharp.Metadata.BindingMetadataGenerator.Initialize(Microsoft.CodeAnalysis.IncrementalGeneratorInitializationContext)" };
foreach (var id in expectedIds)
    Check(xml.Descendants("member").Any(x => (string?)x.Attribute("name") == id && x.Element("summary") != null), "compiler plugin XML " + id);
var entry = typeof(BindingMetadataGenerator);
var method = entry.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Single();
Check(entry.IsSealed && entry.GetConstructors().Length == 1 && entry.GetConstructors()[0].GetParameters().Length == 0 &&
    method.Name == "Initialize" && method.ReturnType == typeof(void) && method.GetParameters().Length == 1 &&
    method.GetParameters()[0].Name == "context" && method.GetParameters()[0].ParameterType == typeof(IncrementalGeneratorInitializationContext) &&
    !method.GetParameters()[0].HasDefaultValue && entry.GetInterfaces().SequenceEqual([typeof(IIncrementalGenerator)]), "exact compiled compiler API baseline");
var reference = new StringBuilder("# NativeWeave C# generation API reference\n\nGenerated from compiled assembly metadata and XML documentation.\n\nThe maintainer executable and generated runtime bindings expose **0 public application types and 0 public application members**.\n\nThe separate netstandard2.0 compiler-host entry point exports one sealed type implementing `Microsoft.CodeAnalysis.IIncrementalGenerator`:\n\n");
foreach (var id in expectedIds)
{
    var member = xml.Descendants("member").Single(x => (string?)x.Attribute("name") == id);
    reference.Append("- `").Append(id).Append("` — ").Append(string.Join(' ', member.Element("summary")!.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))).Append('\n');
}
reference.Append("\n`Initialize` takes the required value-type `context` parameter and returns void. The stateless constructor has no parameters. There are no optional defaults or additional public members.\n\nThe compiler invokes this build-only surface; applications do not load it at runtime. See [C# bindings and compiler metadata](../../docs/guides/generators-csharp.md).\n");
var referencePath = "src/Generators.CSharp/api-reference.md";
if (args.Contains("--write-reference", StringComparer.Ordinal)) File.WriteAllText(referencePath, reference.ToString());
else Check(File.ReadAllText(referencePath) == reference.ToString(), "compiled API/XML reference is current");
Console.WriteLine($"PASS A10 emitter/incremental/compiler/API: {assertions} assertions; Roslyn {typeof(CSharpCompilation).Assembly.GetName().Version}; no runtime binder.");

internal sealed class TextInput(string path, string content) : AdditionalText
{
    public override string Path => path;
    public override SourceText GetText(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return SourceText.From(content, Encoding.UTF8); }
}
