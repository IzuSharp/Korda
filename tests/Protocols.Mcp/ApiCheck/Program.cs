using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text.Json;
using System.Xml.Linq;

string root = Path.GetFullPath(args[0]), design = Path.GetFullPath(args[1]), sdk = Path.GetFullPath(args[2]);
string output = Path.Combine(root, "src/Protocols.Mcp/bin/Release/net11.0");
var files = Directory.GetFiles(Path.Combine(sdk, "packs/Microsoft.NETCore.App.Ref/11.0.0/ref/net11.0"), "*.dll").ToDictionary(x => Path.GetFileName(x)!, StringComparer.OrdinalIgnoreCase);
foreach (string path in Directory.GetFiles(Path.Combine(root, "tests/Protocols.Mcp/bin/Release/net11.0"), "*.dll")) if (Path.GetFileName(path) is not "NativeWeave.Server.Mcp.dll") files[Path.GetFileName(path)] = path;
var references = files.Values.Select(x => MetadataReference.CreateFromFile(x)).ToList();
var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable, metadataImportOptions: MetadataImportOptions.All);
var format = new SymbolDisplayFormat(typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
    genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters | SymbolDisplayGenericsOptions.IncludeTypeConstraints | SymbolDisplayGenericsOptions.IncludeVariance,
    memberOptions: SymbolDisplayMemberOptions.IncludeAccessibility | SymbolDisplayMemberOptions.IncludeModifiers | SymbolDisplayMemberOptions.IncludeType | SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeConstantValue,
    parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName | SymbolDisplayParameterOptions.IncludeDefaultValue | SymbolDisplayParameterOptions.IncludeOptionalBrackets | SymbolDisplayParameterOptions.IncludeParamsRefOut,
    miscellaneousOptions: SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);
using var policyJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(design, "docs/api/server-symbol-policies.json")));
var policies = policyJson.RootElement.GetProperty("types").EnumerateObject().Where(x => x.Value.GetProperty("tasks").EnumerateArray().Any(t => t.GetString() == "B19")).ToDictionary(x => x.Name, x => x.Value);
var expectedCompilation = CSharpCompilation.Create("PinnedServerDeclarations", [CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(design, "experiments/public-api/NativeWeave.Api.Design/Server.cs")))], references, options);
var productReferences = references.Append(MetadataReference.CreateFromFile(Path.Combine(output, "NativeWeave.Server.Mcp.dll"))).ToList();
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
    string metadataName = name + (name.EndsWith(".McpToolResult", StringComparison.Ordinal) ? "`1" : "");
    var declared = expectedCompilation.GetTypeByMetadataName(metadataName) ?? throw new Exception("Missing design type: " + name);
    var implemented = productCompilation.GetTypeByMetadataName(metadataName) ?? throw new Exception("Missing product type: " + name);
    foreach (var s in PublicMembers(declared)) { expected.Add(s.GetDocumentationCommentId()!, Signature(s)); if (!s.IsImplicitlyDeclared) documented.Add(s.GetDocumentationCommentId()!); }
    foreach (var s in PublicMembers(implemented)) actual.Add(s.GetDocumentationCommentId()!, Signature(s));
}
var errors = expected.Where(x => !actual.TryGetValue(x.Key, out var got) || got != x.Value).Select(x => $"{x.Key}\n  design: {x.Value}\n  actual: {(actual.TryGetValue(x.Key, out var got) ? got : "<missing>")}")
    .Concat(actual.Keys.Except(expected.Keys).Select(x => "Unexpected product member: " + x)).ToArray();
if (errors.Length != 0) throw new Exception(string.Join("\n", errors));
string baselinePath = Path.Combine(root, "src/Protocols.Mcp/public-api.expected.json");
string baseline = JsonSerializer.Serialize(new { design_commit = "c62cdb8aee833b6ab1a03e29a3f59f81a1b483f9", symbols = expected }, new JsonSerializerOptions { WriteIndented = true }) + "\n";
if (args.Contains("--write")) File.WriteAllText(baselinePath, baseline);
else if (File.ReadAllText(baselinePath) != baseline) throw new Exception("Pinned API baseline drift.");

var docs = XDocument.Load(Path.Combine(output, "NativeWeave.Server.Mcp.xml")).Descendants("member").ToDictionary(x => x.Attribute("name")!.Value);
if (args.Contains("--enrich"))
{
    foreach (string file in Directory.GetFiles(Path.Combine(root, "src/Protocols.Mcp"), "*.cs"))
    {
        var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file));
        var rewriter = new XmlEnricher(policies);
        File.WriteAllText(file, rewriter.Visit(tree.GetRoot())!.ToFullString());
    }
    Console.WriteLine("B19 XML source enrichment written; rebuild before checking compiled documentation."); return;
}
string? Remarks(XElement doc) => doc.Element("remarks")?.Value ?? (doc.Element("inheritdoc")?.Attribute("cref")?.Value is { } parent && docs.TryGetValue(parent, out var inherited) ? Remarks(inherited) : null);
var reference = new System.Text.StringBuilder("# MCP API reference\n\nGenerated from the actual compiled public API and XML; compared against the exact pinned design declarations. B19 owns the finite typed MCP facade; rmcp owns protocol dispatch on the existing B17 Host.\n\n");
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
string referencePath = Path.Combine(root, "src/Protocols.Mcp/api-reference.md");
if (args.Contains("--write")) File.WriteAllText(referencePath, reference.ToString());
else if (File.ReadAllText(referencePath) != reference.ToString()) throw new Exception("Generated reference is stale.");

var consumerPrefix = "using System; using System.Threading.Tasks; using System.Text.Json.Serialization.Metadata; using NativeWeave.Server; using NativeWeave.Server.Mcp; sealed record In(int Value); sealed record Out(int Value); static class Consumer { ";
var consumerFixtures = new (string Name, string Code, bool Valid)[]
{
    ("exact-static-tool", "static void Build(ServerRoutes routes, JsonTypeInfo<In> input, JsonTypeInfo<Out> output) { var r = new McpRegistry().AddTool(\"tool\", \"typed\", input, output, static (v,c) => Task.FromResult(McpToolResult<Out>.Success(new(v.Value)))); routes.MapMcp(\"/mcp\", r); }", true),
    ("wrong-result-type", "static void Build(JsonTypeInfo<In> input, JsonTypeInfo<Out> output) { new McpRegistry().AddTool(\"tool\", \"typed\", input, output, static (v,c) => Task.FromResult(McpToolResult<In>.Success(v))); }", false),
    ("missing-static-json", "static void Build() { new McpRegistry().AddTool<In,Out>(\"tool\", \"typed\", static (v,c) => Task.FromResult(McpToolResult<Out>.Success(new(v.Value)))); }", false),
    ("no-open-call-context", "static void Build() { _ = new McpCallContext(); }", false),
    ("no-concrete-result-constructor", "static void Build() { _ = new McpToolResult<Out>(); }", false),
};
foreach (var fixture in consumerFixtures)
{
    var compilation = CSharpCompilation.Create("McpConsumer_" + fixture.Name.Replace('-', '_'), [CSharpSyntaxTree.ParseText(consumerPrefix + fixture.Code + " }")], productReferences, options);
    var failures = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
    if ((failures.Length == 0) != fixture.Valid) throw new Exception("Consumer compile mismatch " + fixture.Name + ": " + string.Join("\n", failures.Select(d => d.ToString())));
}
Console.WriteLine($"B19 compile fixtures: {consumerFixtures.Length} exact static consumer cases passed.");

Console.WriteLine($"B19 API: {actual.Count} exact symbols; XML/reference/parameters/nullability/defaults checked.");
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
