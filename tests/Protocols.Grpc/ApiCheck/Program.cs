using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text.Json;
using System.Xml.Linq;

string root = Path.GetFullPath(args[0]), design = Path.GetFullPath(args[1]), sdk = Path.GetFullPath(args[2]);
string output = Path.Combine(root, "src/Protocols.Grpc/bin/Release/net11.0");
var files = Directory.GetFiles(Path.Combine(sdk, "packs/Microsoft.NETCore.App.Ref/11.0.0/ref/net11.0"), "*.dll").ToDictionary(x => Path.GetFileName(x)!, StringComparer.OrdinalIgnoreCase);
foreach (string path in Directory.GetFiles(output, "*.dll")) if (Path.GetFileName(path) != "NativeWeave.Server.Grpc.dll") files[Path.GetFileName(path)] = path;
var references = files.Values.Select(x => MetadataReference.CreateFromFile(x)).ToList();
var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable, metadataImportOptions: MetadataImportOptions.All);
var format = new SymbolDisplayFormat(typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
    genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters | SymbolDisplayGenericsOptions.IncludeTypeConstraints | SymbolDisplayGenericsOptions.IncludeVariance,
    memberOptions: SymbolDisplayMemberOptions.IncludeAccessibility | SymbolDisplayMemberOptions.IncludeModifiers | SymbolDisplayMemberOptions.IncludeType | SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeConstantValue,
    parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName | SymbolDisplayParameterOptions.IncludeDefaultValue | SymbolDisplayParameterOptions.IncludeOptionalBrackets | SymbolDisplayParameterOptions.IncludeParamsRefOut,
    miscellaneousOptions: SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);
using var policyJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(design, "docs/api/server-symbol-policies.json")));
var policies = policyJson.RootElement.GetProperty("types").EnumerateObject().Where(x => x.Value.GetProperty("tasks").EnumerateArray().Any(t => t.GetString() == "B18")).ToDictionary(x => x.Name, x => x.Value);
var expectedCompilation = CSharpCompilation.Create("PinnedServerDeclarations", [CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(design, "experiments/public-api/NativeWeave.Api.Design/Server.cs")))], references, options);
var productReferences = references.Append(MetadataReference.CreateFromFile(Path.Combine(output, "NativeWeave.Server.Grpc.dll"))).ToList();
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
    string metadataName = name + (name.EndsWith(".GrpcMethod", StringComparison.Ordinal) ? "`2" : name.EndsWith(".GrpcMarshaller", StringComparison.Ordinal) || name.EndsWith(".GrpcDecoder", StringComparison.Ordinal) || name.EndsWith(".GrpcEncoder", StringComparison.Ordinal) ? "`1" : "");
    var declared = expectedCompilation.GetTypeByMetadataName(metadataName) ?? throw new Exception("Missing design type: " + name);
    var implemented = productCompilation.GetTypeByMetadataName(metadataName) ?? throw new Exception("Missing product type: " + name);
    foreach (var s in PublicMembers(declared)) { expected.Add(s.GetDocumentationCommentId()!, Signature(s)); if (!s.IsImplicitlyDeclared) documented.Add(s.GetDocumentationCommentId()!); }
    foreach (var s in PublicMembers(implemented)) actual.Add(s.GetDocumentationCommentId()!, Signature(s));
}
var errors = expected.Where(x => !actual.TryGetValue(x.Key, out var got) || got != x.Value).Select(x => $"{x.Key}\n  design: {x.Value}\n  actual: {(actual.TryGetValue(x.Key, out var got) ? got : "<missing>")}")
    .Concat(actual.Keys.Except(expected.Keys).Select(x => "Unexpected product member: " + x)).ToArray();
if (errors.Length != 0) throw new Exception(string.Join("\n", errors));
string baselinePath = Path.Combine(root, "src/Protocols.Grpc/public-api.expected.json");
string baseline = JsonSerializer.Serialize(new { design_commit = "c62cdb8aee833b6ab1a03e29a3f59f81a1b483f9", symbols = expected }, new JsonSerializerOptions { WriteIndented = true }) + "\n";
if (args.Contains("--write")) File.WriteAllText(baselinePath, baseline);
else if (File.ReadAllText(baselinePath) != baseline) throw new Exception("Pinned API baseline drift.");

var docs = XDocument.Load(Path.Combine(output, "NativeWeave.Server.Grpc.xml")).Descendants("member").ToDictionary(x => x.Attribute("name")!.Value);
string? Remarks(XElement doc) => doc.Element("remarks")?.Value ?? (doc.Element("inheritdoc")?.Attribute("cref")?.Value is { } parent && docs.TryGetValue(parent, out var inherited) ? Remarks(inherited) : null);
var reference = new System.Text.StringBuilder("# Typed gRPC API reference\n\nGenerated from the actual compiled public API and XML; compared against the exact pinned design declarations. B18 owns the optional static gRPC facade; Tonic and the existing Rust Host own protocol and transport.\n\n");
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
string referencePath = Path.Combine(root, "src/Protocols.Grpc/api-reference.md");
if (args.Contains("--write")) File.WriteAllText(referencePath, reference.ToString());
else if (File.ReadAllText(referencePath) != reference.ToString()) throw new Exception("Generated reference is stale.");

Console.WriteLine($"PASS B18 {actual.Count} exact compiled declarations; public API, XML and generated reference match the pinned Design.");
