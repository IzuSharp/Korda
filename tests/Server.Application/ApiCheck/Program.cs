using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NativeWeave.Server.Generator;
using System.Text.Json;
using System.Xml.Linq;

string root = Path.GetFullPath(args[0]), design = Path.GetFullPath(args[1]), sdk = Path.GetFullPath(args[2]);
string output = Path.Combine(root, "src/Server.Application/bin/Release/net11.0");
var files = Directory.GetFiles(Path.Combine(sdk, "packs/Microsoft.NETCore.App.Ref/11.0.0/ref/net11.0"), "*.dll").ToDictionary(x => Path.GetFileName(x)!, StringComparer.OrdinalIgnoreCase);
foreach (string path in Directory.GetFiles(Path.Combine(root, "tests/Server.Application/bin/Release/net11.0"), "*.dll")) if (Path.GetFileName(path) is not ("NativeWeave.Server.dll" or "NativeWeave.Server.Hosting.dll")) files[Path.GetFileName(path)] = path;
string hosted = Path.Combine(root, "src/Server.Application/Hosting/bin/Release/net11.0/NativeWeave.Server.Hosting.dll");
var references = files.Values.Select(x => MetadataReference.CreateFromFile(x)).ToList();
var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable, metadataImportOptions: MetadataImportOptions.All);
var format = new SymbolDisplayFormat(typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
    genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters | SymbolDisplayGenericsOptions.IncludeTypeConstraints | SymbolDisplayGenericsOptions.IncludeVariance,
    memberOptions: SymbolDisplayMemberOptions.IncludeAccessibility | SymbolDisplayMemberOptions.IncludeModifiers | SymbolDisplayMemberOptions.IncludeType | SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeConstantValue,
    parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName | SymbolDisplayParameterOptions.IncludeDefaultValue | SymbolDisplayParameterOptions.IncludeOptionalBrackets | SymbolDisplayParameterOptions.IncludeParamsRefOut,
    miscellaneousOptions: SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);
using var policyJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(design, "docs/api/server-symbol-policies.json")));
var policies = policyJson.RootElement.GetProperty("types").EnumerateObject().Where(x => x.Value.GetProperty("tasks").EnumerateArray().Any(t => t.GetString() == "B15")).ToDictionary(x => x.Name, x => x.Value);
var expectedCompilation = CSharpCompilation.Create("PinnedServerDeclarations", [CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(design, "experiments/public-api/NativeWeave.Api.Design/Server.cs")))], references, options);
var productReferences = references.Append(MetadataReference.CreateFromFile(Path.Combine(output, "NativeWeave.Server.dll"))).Append(MetadataReference.CreateFromFile(hosted)).ToList();
var productCompilation = CSharpCompilation.Create("ProductServerPublicApi", references: productReferences, options: options);
if (args.Contains("--probe-diagnostic-id"))
{
    var probe = CSharpCompilation.Create("DiagnosticProbe", [CSharpSyntaxTree.ParseText("class C { [System.Obsolete(\"Binding error\", true, DiagnosticId=\"NWAPI-S003\")] static void Invalid() {} static void Check() => Invalid(); }")], references, options);
    foreach (var diagnostic in probe.GetDiagnostics()) Console.WriteLine(diagnostic.Id + " " + diagnostic);
    return;
}
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
    string metadataName = name + (name.EndsWith(".JsonRequestHandler", StringComparison.Ordinal) ? "`2" : name.EndsWith(".JsonResultHandler", StringComparison.Ordinal) ? "`1" : "");
    var declared = expectedCompilation.GetTypeByMetadataName(metadataName) ?? throw new Exception("Missing design type: " + name);
    var implemented = productCompilation.GetTypeByMetadataName(metadataName) ?? throw new Exception("Missing product type: " + name);
    foreach (var s in PublicMembers(declared)) { expected.Add(s.GetDocumentationCommentId()!, Signature(s)); if (!s.IsImplicitlyDeclared) documented.Add(s.GetDocumentationCommentId()!); }
    foreach (var s in PublicMembers(implemented)) actual.Add(s.GetDocumentationCommentId()!, Signature(s));
}
var errors = expected.Where(x => !actual.TryGetValue(x.Key, out var got) || got != x.Value).Select(x => $"{x.Key}\n  design: {x.Value}\n  actual: {(actual.TryGetValue(x.Key, out var got) ? got : "<missing>")}")
    .Concat(actual.Keys.Except(expected.Keys).Select(x => "Unexpected product member: " + x)).ToArray();
if (errors.Length != 0) throw new Exception(string.Join("\n", errors));
string baselinePath = Path.Combine(root, "src/Server.Application/public-api.expected.json");
string baseline = JsonSerializer.Serialize(new { design_commit = "c62cdb8aee833b6ab1a03e29a3f59f81a1b483f9", symbols = expected }, new JsonSerializerOptions { WriteIndented = true }) + "\n";
if (args.Contains("--write")) File.WriteAllText(baselinePath, baseline);
else if (File.ReadAllText(baselinePath) != baseline) throw new Exception("Pinned API baseline drift.");

var docs = XDocument.Load(Path.Combine(output, "NativeWeave.Server.xml")).Descendants("member").Concat(XDocument.Load(Path.ChangeExtension(hosted, ".xml")).Descendants("member")).ToDictionary(x => x.Attribute("name")!.Value);
if (args.Contains("--enrich"))
{
    foreach (string file in Directory.GetFiles(Path.Combine(root, "src/Server.Application"), "*.cs").Append(Path.Combine(root, "src/Server.Application/Hosting/NativeServerServiceCollectionExtensions.cs")))
    {
        var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file));
        var rewriter = new XmlEnricher(policies);
        File.WriteAllText(file, rewriter.Visit(tree.GetRoot())!.ToFullString());
    }
    Console.WriteLine("B15 XML source enrichment written; rebuild before checking compiled documentation."); return;
}
string? Remarks(XElement doc) => doc.Element("remarks")?.Value ?? (doc.Element("inheritdoc")?.Attribute("cref")?.Value is { } parent && docs.TryGetValue(parent, out var inherited) ? Remarks(inherited) : null);
var reference = new System.Text.StringBuilder("# Server application API reference\n\nGenerated from the actual compiled public API and XML; compared against the exact pinned design declarations. B15 owns application lifecycle, routes, typed binding and results; B17 owns native transport.\n\n");
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
string referencePath = Path.Combine(root, "src/Server.Application/api-reference.md");
if (args.Contains("--write")) File.WriteAllText(referencePath, reference.ToString());
else if (File.ReadAllText(referencePath) != reference.ToString()) throw new Exception("Generated reference is stale.");

var prefix = "#nullable enable\nusing System; using System.Threading; using System.Threading.Tasks; using NativeWeave.Server;\n";
var positive = prefix + "[ServerEndpoints] public static partial class E { [ServerRoute(\"GET\",\"/{id}\")] static ServerResult Read([RouteParameter] int id,[QueryParameter] int? page,[HeaderParameter(\"X-Test\")] string? value,[QueryParameter] int[] tags,CancellationToken token) => Results.Text(id.ToString()); }";
CSharpCompilation Input(string source) => CSharpCompilation.Create("GeneratedConsumer", [CSharpSyntaxTree.ParseText(source)], productReferences, options);
string Generate(CSharpCompilation input, out ImmutableArray<Diagnostic> diagnostics)
{
    GeneratorDriver driver = CSharpGeneratorDriver.Create(new EndpointGenerator());
    driver = driver.RunGeneratorsAndUpdateCompilation(input, out var result, out diagnostics);
    diagnostics = diagnostics.AddRange(result.GetDiagnostics().Where(x => x.Severity == DiagnosticSeverity.Error));
    return string.Join("\n", driver.GetRunResult().GeneratedTrees.Select(x => x.ToString()));
}
string emitted = Generate(Input(positive), out var positiveDiagnostics);
if (positiveDiagnostics.Any(x => x.Severity == DiagnosticSeverity.Error)) throw new Exception(string.Join("\n", positiveDiagnostics));
if (emitted != Generate(Input(positive), out _) || emitted.Contains("object[]", StringComparison.Ordinal) || emitted.Contains("DynamicInvoke", StringComparison.Ordinal)) throw new Exception("Generator nondeterminism or dynamic argument binding.");
var numeric = Generate(Input(prefix + "[ServerEndpoints] public static partial class E { [ServerRoute(\"GET\",\"/defaults\")] static ServerResult Defaults([QueryParameter] decimal precise = 1.2345678901234567890123456789m, [QueryParameter] float single = float.NaN, [QueryParameter] double negative = double.NegativeInfinity, [QueryParameter] double positive = double.PositiveInfinity, [QueryParameter] ulong large = ulong.MaxValue) => Results.NoContent(); }"), out var numericDiagnostics);
if (numericDiagnostics.Any(x => x.Severity == DiagnosticSeverity.Error) || !numeric.Contains("1.2345678901234567890123456789M", StringComparison.Ordinal) || !numeric.Contains("global::System.Single.NaN", StringComparison.Ordinal) || !numeric.Contains("18446744073709551615UL", StringComparison.Ordinal)) throw new Exception("Typed numeric defaults changed or failed compilation: " + string.Join("\n", numericDiagnostics));
var negatives = new (string Id, string Method)[] {
    ("NWAPI-S003", "static ServerResult F(int value)=>Results.NoContent();"),
    ("NWAPI-S002", "static ServerResult F(ref int value)=>Results.NoContent();"),
    ("NWAPI-S002", "static async void F(RequestContext context){await Task.Yield();}"),
    ("NWAPI-S002", "static Task F()=>Task.CompletedTask;"),
    ("NWAPI-S001", "static Missing F([BodyParameter] Missing body)=>body;"),
    ("NWAPI-S003", "static ServerResult F([QueryParameter,RouteParameter] int value)=>Results.NoContent();"),
    ("NWAPI-S003", "static ServerResult F(RequestContext first,RequestContext second)=>Results.NoContent();") };
foreach (var test in negatives)
{
    Generate(Input(prefix + "public sealed class Missing {} [ServerEndpoints] public static partial class E { [ServerRoute(\"POST\",\"/\")] " + test.Method + " }"), out var diagnostics);
    if (!diagnostics.Any(x => x.Id == test.Id)) throw new Exception("Missing generator diagnostic " + test.Id + ": " + string.Join("\n", diagnostics));
}
Console.WriteLine($"B15 API: {actual.Count} exact symbols; XML/reference/parameters/nullability/defaults checked; deterministic generated positive and {negatives.Length} negative consumers passed.");

internal sealed class XmlEnricher(Dictionary<string, JsonElement> policies) : CSharpSyntaxRewriter
{
    public override SyntaxNode? Visit(SyntaxNode? node)
    {
        var original = node;
        node = base.Visit(node);
        if (node is MemberDeclarationSyntax member && IsPublic(member))
        {
            string? name = TypeName((MemberDeclarationSyntax)original!);
            if (name is not null && policies.TryGetValue(name, out var policy))
            {
                string leading = member.GetLeadingTrivia().ToFullString();
                var lines = new List<string>();
                if (!leading.Contains("<remarks>", StringComparison.Ordinal) && !leading.Contains("<inheritdoc", StringComparison.Ordinal))
                {
                    if (member is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax)
                        lines.Add("<remarks>" + Escape(policy.GetProperty("capability").GetString()!.Replace("Initial Windows/Linux x64 package", "Linux x64 verified package; other RID acceptance is separate", StringComparison.Ordinal)) + " " + Escape(policy.GetProperty("cost_profile").GetString()!) + " This policy applies to members unless their documentation states a narrower rule. Configuration is single-threaded before freeze; borrowed request members expire after dispatch cleanup, and asynchronous operations retain native owners until retirement.</remarks>");
                    else lines.Add("<inheritdoc cref=\"T:" + name + "\" path=\"/member/remarks\" />");
                }
                ParameterListSyntax? parameters = member switch { BaseMethodDeclarationSyntax m => m.ParameterList, DelegateDeclarationSyntax d => d.ParameterList, RecordDeclarationSyntax r => r.ParameterList, _ => null };
                if (parameters is not null) foreach (var p in parameters.Parameters)
                    if (!leading.Contains("<param name=\"" + p.Identifier.ValueText + "\"", StringComparison.Ordinal)) lines.Add("<param name=\"" + p.Identifier.ValueText + "\">" + Parameter(p) + "</param>");
                TypeParameterListSyntax? generic = member switch { MethodDeclarationSyntax m => m.TypeParameterList, TypeDeclarationSyntax t => t.TypeParameterList, DelegateDeclarationSyntax d => d.TypeParameterList, _ => null };
                if (generic is not null) foreach (var p in generic.Parameters)
                    if (!leading.Contains("<typeparam name=\"" + p.Identifier.ValueText + "\"", StringComparison.Ordinal)) lines.Add("<typeparam name=\"" + p.Identifier.ValueText + "\">The statically known " + (p.Identifier.ValueText.Contains("Request", StringComparison.Ordinal) ? "request" : "result") + " type; JSON paths require explicit or registered static metadata.</typeparam>");
                if (member is MethodDeclarationSyntax { ReturnType: var returns } && returns.ToString() != "void" && !leading.Contains("<returns>", StringComparison.Ordinal))
                    lines.Add("<returns>" + (returns.ToString().StartsWith("Task", StringComparison.Ordinal) || returns.ToString() == "ValueTask" ? "An awaitable operation; caller completion is distinct from safe native retirement as specified by the declaring type." : returns.ToString().Contains("Builder", StringComparison.Ordinal) ? "The configured fluent builder; configuration is frozen at application startup." : "The typed result described in the summary, retaining the ownership policy of its declaring type.") + "</returns>");
                if (lines.Count != 0)
                {
                    string indent = new string(' ', member.GetLocation().GetLineSpan().StartLinePosition.Character);
                    leading += string.Concat(lines.Select(x => "/// " + x + "\n" + indent));
                    member = member.WithLeadingTrivia(SyntaxFactory.ParseLeadingTrivia(leading)); node = member;
                }
            }
        }
        return node;
    }
    private static bool IsPublic(MemberDeclarationSyntax member) => member switch { BaseTypeDeclarationSyntax t => t.Modifiers.Any(SyntaxKind.PublicKeyword), BaseMethodDeclarationSyntax m => m.Modifiers.Any(SyntaxKind.PublicKeyword) || m.Parent is InterfaceDeclarationSyntax, BasePropertyDeclarationSyntax p => p.Modifiers.Any(SyntaxKind.PublicKeyword), DelegateDeclarationSyntax d => d.Modifiers.Any(SyntaxKind.PublicKeyword), EnumMemberDeclarationSyntax => true, _ => false };
    private static string? TypeName(MemberDeclarationSyntax member)
    {
        var type = member as BaseTypeDeclarationSyntax ?? member.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault();
        string? id = type?.Identifier.ValueText ?? (member as DelegateDeclarationSyntax)?.Identifier.ValueText;
        var ns = member.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();
        return id is null || ns is null ? null : ns + "." + id;
    }
    private static string Escape(string text) => System.Security.SecurityElement.Escape(text)!;
    private static string Parameter(ParameterSyntax p) => p.Identifier.ValueText switch
    {
        "cancellationToken" => "Caller cancellation intent; cancellation never releases native owners before their retirement join.",
        "handler" => "A strongly typed application callback invoked on managed dispatch outside native locks; await its asynchronous work.",
        "typeInfo" or "requestTypeInfo" or "responseTypeInfo" => "Static JSON metadata; nullable registration arguments resolve from UseJson contexts at route freeze, without reflection fallback.",
        "pattern" or "prefix" => "A validated native route pattern or group prefix, registered before startup.",
        "timeout" => "The finite checked wait or request duration specified by this operation.",
        "methods" => "Explicit HTTP method tokens copied into the immutable route configuration.",
        "statusCode" => "A final HTTP status in 200 through 599; bodyless status restrictions apply.",
        "configure" => "Optional synchronous configuration callback run before the options snapshot is frozen.",
        "callback" => "An awaited lifecycle callback registered before the corresponding bounded list freezes.",
        "context" => "The current borrowed request context; it cannot escape awaited dispatch and cleanup.",
        "producer" => "An asynchronous response producer, invoked once after execution admission and suppressed for HEAD.",
        "source" => "The source stream; successful result execution admission transfers disposal responsibility.",
        "bytes" or "data" => "The payload or finite byte limit described in the summary; application-owned input is not an unbounded framework allocation.",
        "provider" => "An explicit scope-capable service provider; ownership transfers only when disposeWithServer is true.",
        "disposeWithServer" => "Whether application disposal also disposes the supplied provider; false retains caller ownership.",
        _ => "The " + p.Identifier.ValueText + " value described by this operation" + (p.Default is null ? "." : "; omitted calls use " + Escape(p.Default.Value.ToString()) + ".")
    };
}
