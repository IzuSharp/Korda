using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text.Json;
using System.Xml.Linq;

string root = Path.GetFullPath(args[0]), design = Path.GetFullPath(args[1]), sdk = Path.GetFullPath(args[2]);
string output = Path.Combine(root, "src/Data.NativeHost/bin/Release/net11.0");
var files = Directory.GetFiles(Path.Combine(sdk, "packs/Microsoft.NETCore.App.Ref/11.0.0/ref/net11.0"), "*.dll").ToDictionary(x => Path.GetFileName(x)!, StringComparer.OrdinalIgnoreCase);
foreach (string path in Directory.GetFiles(output, "*.dll")) if (Path.GetFileName(path) != "Korda.Database.dll") files[Path.GetFileName(path)] = path;

var references = files.Values.Select(x => MetadataReference.CreateFromFile(x)).ToList();
var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable, metadataImportOptions: MetadataImportOptions.All);
var format = new SymbolDisplayFormat(typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
    genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters | SymbolDisplayGenericsOptions.IncludeTypeConstraints | SymbolDisplayGenericsOptions.IncludeVariance,
    memberOptions: SymbolDisplayMemberOptions.IncludeAccessibility | SymbolDisplayMemberOptions.IncludeModifiers | SymbolDisplayMemberOptions.IncludeType | SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeConstantValue,
    parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName | SymbolDisplayParameterOptions.IncludeDefaultValue | SymbolDisplayParameterOptions.IncludeOptionalBrackets | SymbolDisplayParameterOptions.IncludeParamsRefOut,
    miscellaneousOptions: SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);
using var policyJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(design, "docs/api/database-symbol-policies.json")).Replace("NativeWeave.", "Korda.", StringComparison.Ordinal));
var policies = policyJson.RootElement.GetProperty("types").EnumerateObject().Where(x => x.Name.StartsWith("Korda.Query.", StringComparison.Ordinal)).ToDictionary(x => x.Name, x => x.Value);
var expectedCompilation = CSharpCompilation.Create("PinnedDatabaseDeclarations", [CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(design, "experiments/public-api/NativeWeave.Api.Design/Database.cs")).Replace("NativeWeave.", "Korda.", StringComparison.Ordinal))], references, options);
var productReferences = references.Append(MetadataReference.CreateFromFile(Path.Combine(output, "Korda.Database.dll"))).ToList();
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
    string metadataName = Enumerable.Range(0, 4).Select(n => name + (n == 0 ? "" : "`" + n)).Single(n => expectedCompilation.GetTypeByMetadataName(n) is not null);
    var declared = expectedCompilation.GetTypeByMetadataName(metadataName) ?? throw new Exception("Missing design type: " + name);
    var implemented = productCompilation.GetTypeByMetadataName(metadataName) ?? throw new Exception("Missing product type: " + name);
    foreach (var s in PublicMembers(declared)) { expected.Add(s.GetDocumentationCommentId()!, Signature(s)); if (!s.IsImplicitlyDeclared) documented.Add(s.GetDocumentationCommentId()!); }
    foreach (var s in PublicMembers(implemented)) actual.Add(s.GetDocumentationCommentId()!, Signature(s));
}
const string sessionName = "Korda.Database.DataSession";
var declaredFrom = expectedCompilation.GetTypeByMetadataName(sessionName)!.GetMembers("From").Single();
var implementedFrom = productCompilation.GetTypeByMetadataName(sessionName)!.GetMembers("From").Single();
expected.Add(declaredFrom.GetDocumentationCommentId()!, Signature(declaredFrom));
actual.Add(implementedFrom.GetDocumentationCommentId()!, Signature(implementedFrom));
documented.Add(declaredFrom.GetDocumentationCommentId()!);
var errors = expected.Where(x => !actual.TryGetValue(x.Key, out var got) || got != x.Value).Select(x => $"{x.Key}\n  design: {x.Value}\n  actual: {(actual.TryGetValue(x.Key, out var got) ? got : "<missing>")}")
    .Concat(actual.Keys.Except(expected.Keys).Select(x => "Unexpected product member: " + x)).ToArray();
if (errors.Length != 0) throw new Exception(string.Join("\n", errors));
string baselinePath = Path.Combine(root, "src/Query.Fluent/public-api.expected.json");
string baseline = JsonSerializer.Serialize(new { design_commit = "c62cdb8aee833b6ab1a03e29a3f59f81a1b483f9", symbols = expected }, new JsonSerializerOptions { WriteIndented = true }) + "\n";
if (args.Contains("--write")) File.WriteAllText(baselinePath, baseline);
else if (File.ReadAllText(baselinePath) != baseline) throw new Exception("Pinned API baseline drift.");

var docs = XDocument.Load(Path.Combine(output, "Korda.Database.xml")).Descendants("member").ToDictionary(x => x.Attribute("name")!.Value);
string? Remarks(XElement doc) => doc.Element("remarks")?.Value ?? (doc.Element("inheritdoc")?.Attribute("cref")?.Value is { } parent && docs.TryGetValue(parent, out var inherited) ? Remarks(inherited) : null);
var reference = new System.Text.StringBuilder("# Fluent query API reference\n\nGenerated from the actual compiled public API and XML; compared against the exact pinned design declarations. The public namespace is composed into Korda.Database. All expression and SQL operations use B09/B11 native owners; B01/B12 own static row contracts.\n\n");
foreach (var pair in actual.Where(x => documented.Contains(x.Key)))
{
    if (!docs.TryGetValue(pair.Key, out var doc) || doc.Element("summary") is null || string.IsNullOrWhiteSpace(Remarks(doc))) throw new Exception("Missing summary/resolvable remarks: " + pair.Key);
    var symbol = DocumentationCommentId.GetFirstSymbolForDeclarationId(pair.Key, productCompilation)!;
    if (symbol is INamedTypeSymbol type) foreach (var p in type.TypeParameters) if (!doc.Elements("typeparam").Any(x => x.Attribute("name")?.Value == p.Name)) throw new Exception("Missing typeparam " + pair.Key + ":" + p.Name);
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
string referencePath = Path.Combine(root, "src/Query.Fluent/api-reference.md");
if (args.Contains("--write")) File.WriteAllText(referencePath, reference.ToString());
else if (File.ReadAllText(referencePath) != reference.ToString()) throw new Exception("Generated reference is stale.");


var prefix = "#nullable enable\nusing System; using Korda.Data; using Korda.Database; using Korda.Query;\n";
var fixture = "class Person {} class Child {} class Consumer { static void Case(Query<Person> query, Query<Child> children, Column<Person,long> id, Column<Person,long?> optional, Column<Person,string> name, Column<Child,long> childId, JoinedTable<Child> inner, LeftJoinedTable<Child> left) { BODY } }";
var compileOptions = options.WithGeneralDiagnosticOption(ReportDiagnostic.Error);
var cases = new (string Name, string Body, bool Valid)[] {
    ("valid-parent", "query.WhereEqual(id, 7L); query.Column(id).AsNullable().Equal(query.Column(optional));", true),
    ("valid-alias", "query.Column(inner, childId); query.NullableValue(left, childId);", true),
    ("wrong-entity", "query.WhereEqual(childId, 7L);", false),
    ("wrong-value", "query.WhereEqual(id, \"text\");", false),
    ("wrong-alias-entity", "query.Column(inner, id);", false),
    ("left-as-inner", "query.Column(left, childId);", false),
    ("nullable-to-nonnullable", "SqlExpression<long> value = query.Column(optional);", false),
    ("repeated-lift", "query.Column(optional).AsNullable();", false),
    ("reversed-lift", "query.Column(id).AsNullableReference();", false),
    ("non-numeric-arithmetic", "query.Column(name).Add(1);", false),
    ("wrong-arithmetic-width", "query.Column(id).Add(1.0);", false),
    ("nullable-factory-target", "query.Select(query.Column(optional), query.Column(id), (long a, long b) => a + b);", false),
    ("nullable-reference-projection", "SqlExpression<string> value = query.Column(name).AsNullableReference();", false),
};
foreach (var test in cases)
{
    var compilation = CSharpCompilation.Create("FluentNegative", [CSharpSyntaxTree.ParseText(prefix + fixture.Replace("BODY", test.Body, StringComparison.Ordinal))], productReferences, compileOptions);
    var diagnostics = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
    if (test.Valid == (diagnostics.Length != 0)) throw new Exception(test.Name + ": unexpected compilation result: " + string.Join(";", diagnostics.Select(d => d.ToString())));
    if (!test.Valid && !diagnostics.Any(d => d.Location.IsInSource)) throw new Exception("Diagnostic lacks application source location: " + test.Name);
}
Console.WriteLine($"B13: {actual.Count} exact public symbols with complete XML/reference; {cases.Length} valid/negative typed consumers checked against actual product.");
