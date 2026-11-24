using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Korda.Query.Generator;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

string root = Path.GetFullPath(args[0]), design = Path.GetFullPath(args[1]), sdk = Path.GetFullPath(args[2]);
string output = Path.Combine(root, "src/Data.Abstractions/bin/Release/net11.0");
var files = Directory.GetFiles(Path.Combine(sdk, "packs/Microsoft.NETCore.App.Ref/11.0.0/ref/net11.0"), "*.dll").ToDictionary(x => Path.GetFileName(x)!, StringComparer.OrdinalIgnoreCase);
foreach (string path in Directory.GetFiles(Path.Combine(root, "src/Data.NativeHost/bin/Release/net11.0"), "*.dll")) if (Path.GetFileName(path) != "Korda.Data.dll") files[Path.GetFileName(path)] = path;

var references = files.Values.Select(x => MetadataReference.CreateFromFile(x)).ToList();
var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable, metadataImportOptions: MetadataImportOptions.All);
var format = new SymbolDisplayFormat(typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
    genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters | SymbolDisplayGenericsOptions.IncludeTypeConstraints | SymbolDisplayGenericsOptions.IncludeVariance,
    memberOptions: SymbolDisplayMemberOptions.IncludeAccessibility | SymbolDisplayMemberOptions.IncludeModifiers | SymbolDisplayMemberOptions.IncludeType | SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeConstantValue,
    parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName | SymbolDisplayParameterOptions.IncludeDefaultValue | SymbolDisplayParameterOptions.IncludeOptionalBrackets | SymbolDisplayParameterOptions.IncludeParamsRefOut,
    miscellaneousOptions: SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);
using var renameJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "src/Query.Generator/db-rename-01.names.json")));
var attributeRenames = renameJson.RootElement.GetProperty("attributes").EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetString()!, StringComparer.Ordinal);
string[] attributeSuffixes = ["Table", "Column", "Key", "Version", "Ignore", "Projection", "Constructor", "Parameter", "Codec", "UseCodec"];
if (renameJson.RootElement.GetProperty("task").GetString() != "DB-Rename-01" || renameJson.RootElement.GetProperty("namespace").GetString() != "Korda.Data" || attributeRenames.Count != attributeSuffixes.Length || attributeSuffixes.Any(suffix => !attributeRenames.TryGetValue("Weave" + suffix + "Attribute", out var current) || current != "Db" + suffix + "Attribute"))
    throw new Exception("Unexpected public Attribute naming migration.");
// The Design checkout is immutable. Project only its reviewed public names;
// constructor parameters, defaults, members and all other signatures stay exact.
string CurrentNames(string historical)
{
    historical = historical.Replace("NativeWeave.", "Korda.", StringComparison.Ordinal);
    foreach (var pair in attributeRenames)
        historical = Regex.Replace(historical, @"\b" + Regex.Escape(pair.Key) + @"\b", pair.Value);
    return historical;
}
using var policyJson = JsonDocument.Parse(CurrentNames(File.ReadAllText(Path.Combine(design, "docs/api/database-symbol-policies.json"))));
var policies = policyJson.RootElement.GetProperty("types").EnumerateObject().Where(x => x.Value.GetProperty("tasks").EnumerateArray().Any(t => t.GetString() == "B10")).Where(x => x.Name.EndsWith("Attribute", StringComparison.Ordinal)).ToDictionary(x => x.Name, x => x.Value);
var expectedCompilation = CSharpCompilation.Create("PinnedDatabaseDeclarations", [CSharpSyntaxTree.ParseText(CurrentNames(File.ReadAllText(Path.Combine(design, "experiments/public-api/NativeWeave.Api.Design/Database.cs"))))], references, options);
var productReferences = references.Append(MetadataReference.CreateFromFile(Path.Combine(output, "Korda.Data.dll"))).ToList();
var productCompilation = CSharpCompilation.Create("ProductServerPublicApi", references: productReferences, options: options);
static IEnumerable<ISymbol> PublicMembers(INamedTypeSymbol type) => type.GetMembers().Where(s => s.DeclaredAccessibility == Accessibility.Public && s is not IMethodSymbol { MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove }).Prepend(type);
string Signature(ISymbol s)
{
    string value = s.ToDisplayString(format);
    if (s is INamedTypeSymbol t) value += $" | kind={t.TypeKind};static={t.IsStatic};sealed={t.IsSealed};abstract={t.IsAbstract};base={t.BaseType?.ToDisplayString(format)};interfaces={string.Join(';', t.Interfaces.Select(x => x.ToDisplayString(format)))}";
    if (s is INamedTypeSymbol { TypeKind: TypeKind.Delegate } d) value += " | invoke=" + d.DelegateInvokeMethod!.ToDisplayString(format);
    if (s is IPropertySymbol p) value += $" | getter={p.GetMethod?.DeclaredAccessibility};setter={p.SetMethod?.DeclaredAccessibility};init={p.SetMethod?.IsInitOnly}";
    return value;
}
var expected = new SortedDictionary<string, string>(StringComparer.Ordinal);
var actual = new SortedDictionary<string, string>(StringComparer.Ordinal);
var documented = new HashSet<string>(StringComparer.Ordinal);
foreach (string name in policies.Keys.Order(StringComparer.Ordinal))
{
    string metadataName = name;
    var declared = expectedCompilation.GetTypeByMetadataName(metadataName) ?? throw new Exception("Missing design type: " + name);
    foreach (var s in PublicMembers(declared)) { expected.Add(s.GetDocumentationCommentId()!, Signature(s)); if (!s.IsImplicitlyDeclared) documented.Add(s.GetDocumentationCommentId()!); }
}
foreach (var pair in attributeRenames)
{
    if (productCompilation.GetTypeByMetadataName("Korda.Data." + pair.Key) is not null) throw new Exception("Retired public Attribute is still exported: " + pair.Key);
    var implemented = productCompilation.GetTypeByMetadataName("Korda.Data." + pair.Value) ?? throw new Exception("Missing public Attribute: " + pair.Value);
    foreach (var symbol in PublicMembers(implemented)) actual.Add(symbol.GetDocumentationCommentId()!, Signature(symbol));
}
using var currentContract = JsonDocument.Parse(CurrentNames(File.ReadAllText(Path.Combine(root, "src/Data.NativeHost/db-api-01.api.json"))));
var currentMembers = currentContract.RootElement.GetProperty("assemblies").GetProperty("Korda.Data").EnumerateArray().ToArray();
var currentAdditions = currentMembers.Select(x => x.GetProperty("id").GetString()!).ToHashSet(StringComparer.Ordinal);
documented.UnionWith(currentMembers.Where(x => !x.GetProperty("implicit_member").GetBoolean()).Select(x => x.GetProperty("id").GetString()!));
// The shared compiled-assembly gate checks exact additive signatures and XML.
// This historical gate still rejects every removed or changed frozen member.
var errors = expected.Where(x => !actual.TryGetValue(x.Key, out var got) || got != x.Value).Select(x => $"{x.Key}\n  design: {x.Value}\n  actual: {(actual.TryGetValue(x.Key, out var got) ? got : "<missing>")}")
    .Concat(actual.Keys.Except(expected.Keys).Where(x => !currentAdditions.Contains(x)).Select(x => "Unexpected product member: " + x)).ToArray();
if (errors.Length != 0) throw new Exception(string.Join("\n", errors));
string baselinePath = Path.Combine(root, "src/Query.Generator/public-api.expected.json");
string baseline = JsonSerializer.Serialize(new { design_commit = "c62cdb8aee833b6ab1a03e29a3f59f81a1b483f9", naming_migration = "DB-Rename-01", symbols = expected }, new JsonSerializerOptions { WriteIndented = true }) + "\n";
if (args.Contains("--write")) File.WriteAllText(baselinePath, baseline);
else if (File.ReadAllText(baselinePath) != baseline) throw new Exception("Pinned API baseline drift.");

var docs = XDocument.Load(Path.Combine(output, "Korda.Data.xml")).Descendants("member").ToDictionary(x => x.Attribute("name")!.Value);
string? Remarks(XElement doc) => doc.Element("remarks")?.Value ?? (doc.Element("inheritdoc")?.Attribute("cref")?.Value is { } parent && docs.TryGetValue(parent, out var inherited) ? Remarks(inherited) : null);
var reference = new System.Text.StringBuilder("# Model generation API reference\n\nGenerated from all ten compiled public database attributes and their XML comments. The B10 declarations retain the exact pinned Design contract with the explicit DB-Rename-01 name mapping; DB-API-01 additions are checked by the shared compiled-assembly gate. B01/B09 descriptor APIs retain their existing ownership.\n\n");
foreach (var pair in actual.Where(x => documented.Contains(x.Key)))
{
    if (!docs.TryGetValue(pair.Key, out var doc) || doc.Element("summary") is null || string.IsNullOrWhiteSpace(Remarks(doc))) throw new Exception("Missing summary/resolvable remarks: " + pair.Key);
    var symbol = DocumentationCommentId.GetFirstSymbolForDeclarationId(pair.Key, productCompilation)!;
    if (symbol is IMethodSymbol method)
    {
        foreach (var p in method.Parameters) if (!doc.Elements("param").Any(x => x.Attribute("name")?.Value == p.Name)) throw new Exception("Missing param " + pair.Key + ":" + p.Name);
        foreach (var p in method.TypeParameters) if (!doc.Elements("typeparam").Any(x => x.Attribute("name")?.Value == p.Name)) throw new Exception("Missing typeparam " + pair.Key + ":" + p.Name);
        if (!method.ReturnsVoid && method.MethodKind == MethodKind.Ordinary && doc.Element("returns") is null) throw new Exception("Missing returns: " + pair.Key);
    }
    static string Text(string text) => string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    reference.Append("## `").Append(pair.Key).Append("`\n\n```csharp\n").Append(pair.Value).Append("\n```\n\n").Append(Text(doc.Element("summary")!.Value)).Append("\n\n").Append(Text(Remarks(doc)!)).Append("\n\n");
    foreach (var item in doc.Elements().Where(x => x.Name.LocalName is "param" or "typeparam" or "returns" or "exception")) reference.Append("- **").Append(item.Attribute("name")?.Value ?? item.Attribute("cref")?.Value ?? item.Name.LocalName).Append("**: ").Append(Text(item.Value)).Append('\n');
    reference.Append('\n');
}
string referencePath = Path.Combine(root, "src/Query.Generator/api-reference.md");
if (args.Contains("--write")) File.WriteAllText(referencePath, reference.ToString());
else if (File.ReadAllText(referencePath) != reference.ToString()) throw new Exception("Generated reference is stale.");

var prefix = "#nullable enable\nusing System; using Korda.Data;\nnamespace Fixtures;\n";
CSharpCompilation Input(params string[] sources) => CSharpCompilation.Create("GeneratedConsumer", sources.Select((s, i) => CSharpSyntaxTree.ParseText(prefix + s, path: $"fixture-{i}.cs")), productReferences, options);
GeneratorDriver Driver(string? codecs = null) => CSharpGeneratorDriver.Create([new ModelGenerator().AsSourceGenerator()], optionsProvider: new ProfileOptions(codecs), driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
string Output(GeneratorDriver driver) => string.Join("\n", driver.GetRunResult().Results.Single().GeneratedSources.OrderBy(x => x.HintName, StringComparer.Ordinal).Select(x => x.HintName + "\n" + x.SourceText));
GeneratorDriver Compile(CSharpCompilation input, GeneratorDriver driver, bool success, out ImmutableArray<Diagnostic> diagnostics)
{
    driver = driver.RunGeneratorsAndUpdateCompilation(input, out var result, out diagnostics);
    diagnostics = diagnostics.AddRange(result.GetDiagnostics().Where(x => x.Severity == DiagnosticSeverity.Error));
    if (diagnostics.Any(x => x.Id == "CS8785") || success && diagnostics.Any(x => x.Severity == DiagnosticSeverity.Error)) throw new Exception(string.Join("\n", diagnostics));
    if (success) { using var emitted = new MemoryStream(); var actual = result.Emit(emitted); if (!actual.Success) throw new Exception(string.Join("\n", actual.Diagnostics)); }
    return driver;
}
const string first = "[DbTable(\"students\", DescriptorName=\"Students\")] public sealed partial record Student([property:DbKey] long Id, [property:DbColumn(\"display_name\")] string? Name, [property:DbVersion] int Version);";
const string partial = "[DbTable(\"partial\")] public sealed partial class Partial { public int Id {get;init;} }";
const string rest = "public sealed partial class Partial { public Guid Token {get;init;} [DbIgnore] public string Debug => \"ignored\"; }";
var input = Input(first, partial, rest);
var generated = Compile(input, Driver(), true, out _);
string outputText = Output(generated);
if (outputText != Output(Compile(input, Driver(), true, out _))) throw new Exception("Fresh-driver nondeterminism.");
string golden = Path.Combine(root, "tests/Query.Generator/golden/models.g.cs.txt");
if (args.Contains("--write")) { Directory.CreateDirectory(Path.GetDirectoryName(golden)!); File.WriteAllText(golden, outputText); }
else if (File.ReadAllText(golden) != outputText) throw new Exception("Generated golden drift.");
var unrelated = input.AddSyntaxTrees(CSharpSyntaxTree.ParseText("internal static class Unrelated { internal const int Value = 7; }", path: "unrelated.cs"));
var cached = Compile(unrelated, generated, true, out _);
if (Output(cached) != outputText) throw new Exception("Unrelated change altered generated mappings.");
var steps = cached.GetRunResult().Results.Single().TrackedSteps["StaticMappings"].SelectMany(x => x.Outputs).ToArray();
if (steps.Any(x => x.Reason is not (IncrementalStepRunReason.Unchanged or IncrementalStepRunReason.Cached))) throw new Exception("Semantic mapping output was not incrementally reused.");
var changed = input.ReplaceSyntaxTree(input.SyntaxTrees.First(), CSharpSyntaxTree.ParseText(prefix + first.Replace("int Version", "long Version", StringComparison.Ordinal), path: "fixture-0.cs"));
if (Output(Compile(changed, cached, true, out _)) == outputText) throw new Exception("Relevant model edit did not invalidate output.");
Compile(Input("[DbTable(\"shadow\",DescriptorName=\"Mappings\")] public sealed record Shadow(long builder,long row,long value);", "[DbTable(\"scope\",DescriptorName=\"value\")] public sealed record Scope(long row,long builder);"), Driver(), true, out _);
var collisionInput = CSharpCompilation.Create("DistinctHints", [CSharpSyntaxTree.ParseText("using Korda.Data; namespace A.B { [DbTable(\"first\")] public sealed record C(int Id); } namespace A { [DbTable(\"second\")] public sealed record B_C(int Id); }")], productReferences, options);
var collisionOutput = Compile(collisionInput, Driver(), true, out _).GetRunResult().Results.Single().GeneratedSources;
if (collisionOutput.Length != 2 || collisionOutput.Select(x => x.HintName).Distinct(StringComparer.Ordinal).Count() != 2) throw new Exception("Distinct metadata names collided in generated hints.");
var cases = new (string Id, string Source, string Span, string? Profile)[] {
    ("NWQ0104", "[DbTable(\"bad\",DescriptorName=\"Mappings\")] public sealed record Bad(int Mappings);", "Mappings", null),
    ("NWQ0103", "[DbTable(\"bad\")] public sealed class Bad { public int Value {get;} public Bad(in int value){Value=value;} }", "Bad", null),
    ("NWQ0106", "[DbTable(\"bad\")] file sealed record Bad(int Id);", "Bad", null),
    ("NWQ0101", "[DbTable(\"bad\")] public sealed record Bad(decimal Value);", "Value", null),
    ("NWQ0102", "[DbTable(\"bad\")] public sealed record Bad([property:DbKey] long? Id);", "Id", null),
    ("NWQ0102", "[DbTable(\"bad\")] public sealed class Bad { public string? Value {get;} public Bad(string value){Value=value;} }", "value", null),
    ("NWQ0103", "[DbTable(\"bad\")] public sealed class Bad { public int Value {get;init;} public Bad(){} public Bad(int value){Value=value;} }", "Bad", null),
    ("NWQ0104", "[DbTable(\"bad\")] public sealed record Bad([property:DbColumn(\"x\")] int First,[property:DbColumn(\"x\")] int Second);", "Second", null),
    ("NWQ0105", "[DbTable(\"bad\")] public sealed record Bad(int Value);", "Value", "1"),
    ("NWQ0106", "[DbTable(\"bad\")] public sealed record Bad<T>(T Value);", "Bad", null),
    ("NWQ0107", "[DbTable(\"bad\")] public sealed record Bad(object Value);", "Value", null),
    ("NWQ0107", "[DbTable(\"bad\")] public sealed record Bad(object[] Value);", "Value", null),
    ("NWQ0107", "[DbTable(\"bad\")] public sealed record Bad(Type Value);", "Value", null),
    ("NWQ0104", "[DbTable(\"bad\")] public sealed record Bad([property:DbIgnore,DbKey] int Value);", "Value", null),
    ("NWQ0103", "[DbTable(\"bad\")] public sealed class Bad { public int Id {get;init;} [DbIgnore] public required string Value {get;init;} }", "Bad", null)
};
foreach (var test in cases)
{
    Compile(Input(test.Source), Driver(test.Profile), false, out var diagnostics);
    var diagnostic = diagnostics.FirstOrDefault(x => x.Id == test.Id) ?? throw new Exception("Missing " + test.Id + ": " + string.Join("\n", diagnostics));
    if (!diagnostic.Location.IsInSource || diagnostic.Location.SourceTree!.GetText().ToString(diagnostic.Location.SourceSpan) != test.Span) throw new Exception("Incorrect source span: " + diagnostic);
    if (diagnostics.Any(x => x.Id.StartsWith("NWQ00", StringComparison.Ordinal))) throw new Exception("Historical diagnostic family was repurposed.");
}
ProjectionExperienceChecks.Run(productReferences, options);
ScaffoldCompilerChecks.Run(productReferences, options);
Console.WriteLine($"B10/DB-Rename-01: {actual.Count} public Attribute symbols across all ten Db types; retired names absent; exact historical signatures plus additive contract; XML/reference and golden checked; compiled partial/nullable/version mappings; {cases.Length} negative cases across all 7 diagnostic IDs; fresh-driver determinism, unrelated cache reuse and relevant invalidation passed.");

internal sealed class ProfileOptions(string? codecs) : AnalyzerConfigOptionsProvider
{
    private readonly AnalyzerConfigOptions _global = new Values(codecs);
    public override AnalyzerConfigOptions GlobalOptions => _global;
    public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => new Values(null);
    public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => new Values(null);
    private sealed class Values(string? codecs) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value) { value = codecs ?? ""; return codecs is not null && key == "build_property.KordaEnabledNativeCodecs"; }
    }
}
