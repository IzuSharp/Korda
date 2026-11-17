using System.Text.Json;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

string root = Path.GetFullPath(args[0]), sdk = Path.GetFullPath(args[1]);
string output = Path.Combine(root, "src/Server.Application/bin/Release/net11.0");
using var baseline = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "src/Server.Application/public-api.expected.json")));
if (baseline.RootElement.GetProperty("design_commit").GetString() != "c62cdb8aee833b6ab1a03e29a3f59f81a1b483f9") throw new Exception("Design baseline changed.");
var symbols = baseline.RootElement.GetProperty("symbols").EnumerateObject().ToDictionary(value => value.Name, value => value.Value.GetString()!);
var files = Directory.GetFiles(Path.Combine(sdk, "packs/Microsoft.NETCore.App.Ref/11.0.0/ref/net11.0"), "*.dll").ToDictionary(path => Path.GetFileName(path)!, StringComparer.OrdinalIgnoreCase);
foreach (string file in Directory.GetFiles(Path.Combine(root, "tests/Server.Diagnostics/bin/Release/net11.0"), "*.dll")) files[Path.GetFileName(file)] = file;
var references = files.Values.Select(file => MetadataReference.CreateFromFile(file)).ToArray();
var compilation = CSharpCompilation.Create("B20Surface", references: references, options: new(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable, metadataImportOptions: MetadataImportOptions.All));
var product = (IAssemblySymbol)compilation.GetAssemblyOrModuleSymbol(references.Single(reference => Path.GetFileName(reference.FilePath) == "NativeWeave.Server.dll"))!;
IEnumerable<INamedTypeSymbol> Types(INamespaceSymbol space) => space.GetTypeMembers().Concat(space.GetNamespaceMembers().SelectMany(Types));
var declared = CSharpCompilation.Create("PinnedPublicTypes", [CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(args[2], "experiments/public-api/NativeWeave.Api.Design/Server.cs")))], references);
var declaredTypes = Types(declared.Assembly.GlobalNamespace).Where(type => type.DeclaredAccessibility == Accessibility.Public).Select(type => type.GetDocumentationCommentId()!).ToHashSet(StringComparer.Ordinal);
foreach (var type in Types(product.GlobalNamespace).Where(type => type.DeclaredAccessibility == Accessibility.Public))
    if (!declaredTypes.Contains(type.GetDocumentationCommentId()!)) throw new Exception("Accidental public diagnostic plumbing: " + type);
var format = new SymbolDisplayFormat(typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
    genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters | SymbolDisplayGenericsOptions.IncludeTypeConstraints | SymbolDisplayGenericsOptions.IncludeVariance,
    memberOptions: SymbolDisplayMemberOptions.IncludeAccessibility | SymbolDisplayMemberOptions.IncludeModifiers | SymbolDisplayMemberOptions.IncludeType | SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeConstantValue,
    parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName | SymbolDisplayParameterOptions.IncludeDefaultValue | SymbolDisplayParameterOptions.IncludeOptionalBrackets | SymbolDisplayParameterOptions.IncludeParamsRefOut,
    miscellaneousOptions: SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);
var context = product.GetTypeByMetadataName("NativeWeave.Server.RequestContext")!;
var actual = context.GetMembers().Where(member => member.DeclaredAccessibility == Accessibility.Public && member is not IMethodSymbol { MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet }).Prepend(context).ToArray();
var xml = XDocument.Load(Path.Combine(output, "NativeWeave.Server.xml")).Descendants("member").ToDictionary(node => node.Attribute("name")!.Value);
foreach (var member in actual)
{
    string id = member.GetDocumentationCommentId()!;
    string signature = member.ToDisplayString(format);
    if (member is INamedTypeSymbol type) signature += $" | kind={type.TypeKind};static={type.IsStatic};sealed={type.IsSealed};abstract={type.IsAbstract};base={type.BaseType?.ToDisplayString(format)};interfaces={string.Join(';', type.Interfaces.Select(value => value.ToDisplayString(format)))}";
    if (member is IPropertySymbol property) signature += $" | getter={property.GetMethod?.DeclaredAccessibility};setter={property.SetMethod?.DeclaredAccessibility};init={property.SetMethod?.IsInitOnly}";
    if (!symbols.TryGetValue(id, out var expected) || signature != expected) throw new Exception("B20 RequestContext signature drift: " + id);
    if (!xml.TryGetValue(id, out var doc) || string.IsNullOrWhiteSpace(doc.Element("summary")?.Value)) throw new Exception("Missing RequestContext XML: " + id);
}
if (actual.Length != symbols.Keys.Count(key => key == "T:NativeWeave.Server.RequestContext" || key.StartsWith("P:NativeWeave.Server.RequestContext.", StringComparison.Ordinal) || key.StartsWith("M:NativeWeave.Server.RequestContext.", StringComparison.Ordinal))) throw new Exception("Missing pinned RequestContext member.");
foreach (string file in new[] { "src/Server.Diagnostics/api-reference.md", "docs/guides/server-diagnostics.md", "samples/api/server-diagnostics/Program.cs" }) if (!File.Exists(Path.Combine(root, file))) throw new Exception("Missing B20 documentation/consumer.");
Console.WriteLine($"PASS B20 internal-only applicability; {actual.Length} exact RequestContext declarations/XML; no additional public types; public-only sample/reference contribution present.");
