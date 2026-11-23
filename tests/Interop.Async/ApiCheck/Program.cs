using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Text.Json;
using System.Xml.Linq;

string root = Path.GetFullPath(args[0]), sdk = Path.GetFullPath(args[1]), dll = Path.GetFullPath(args[2]);
var references = Directory.GetFiles(Path.Combine(sdk, "packs/Microsoft.NETCore.App.Ref/11.0.0/ref/net11.0"), "*.dll").Select(p => MetadataReference.CreateFromFile(p)).ToList();
references.Add(MetadataReference.CreateFromFile(Path.Combine(Path.GetDirectoryName(dll)!, "Korda.Interop.Abi.dll")));
var product = MetadataReference.CreateFromFile(dll); references.Add(product);
var compilation = CSharpCompilation.Create("AsyncApiCheck", references: references, options: new(OutputKind.DynamicallyLinkedLibrary, metadataImportOptions: MetadataImportOptions.All));
var assembly = (IAssemblySymbol)compilation.GetAssemblyOrModuleSymbol(product)!;
var types = new List<INamedTypeSymbol>();
void Walk(INamespaceSymbol ns)
{
    foreach (var child in ns.GetNamespaceMembers()) Walk(child);
    if (ns.ToDisplayString() == "Korda.Interop.Async") types.AddRange(ns.GetTypeMembers());
}
Walk(assembly.GlobalNamespace);
if (types.Count != 6 || types.Any(t => t.DeclaredAccessibility != Accessibility.Internal)) throw new Exception("A07 must expose no public async plumbing.");
var owner = types.Single(t => t.Name == "AsyncOperation" && t.Arity == 0);
var generic = types.Single(t => t.Name == "AsyncOperation" && t.Arity == 1);
if (!owner.IsAbstract || !generic.IsSealed || generic.BaseType?.Name != "AsyncOperation") throw new Exception("Async identity/typed bridge shape changed.");
var start = generic.GetMembers("Start").OfType<IMethodSymbol>().Single();
if (start.IsGenericMethod || start.Parameters.Length != 0 || start.ReturnType is not INamedTypeSymbol task || task.Name != "Task" || task.Arity != 1 || task.TypeArguments[0] is not ITypeParameterSymbol)
    throw new Exception("The typed managed entry must return Task<TResult>.");
var entry = owner.GetMembers("OnTerminal").OfType<IMethodSymbol>().Single();
if (!entry.IsStatic || entry.IsGenericMethod || entry.DeclaredAccessibility != Accessibility.Private || entry.ReturnType.SpecialType != SpecialType.System_UInt32 ||
    entry.Parameters.Length != 3 || entry.Parameters[0].Type.SpecialType != SpecialType.System_UIntPtr || entry.Parameters[1].Type.SpecialType != SpecialType.System_UInt32 ||
    entry.Parameters[2].Type is not IPointerTypeSymbol pointer || pointer.PointedAtType.ToDisplayString() != "Korda.Interop.Abi.NwErrorInfoV1")
    throw new Exception("Compiled terminal signature differs from the exact v1 ABI.");
var uco = entry.GetAttributes().Single(a => a.AttributeClass?.Name == "UnmanagedCallersOnlyAttribute");
if (!uco.NamedArguments.Any(a => a.Key == "CallConvs" && a.Value.Values.Any(v => v.Value is INamedTypeSymbol t && t.Name == "CallConvCdecl")))
    throw new Exception("Cdecl terminal convention is missing.");
var xml = XDocument.Load(Path.ChangeExtension(dll, ".xml"));
var docs = xml.Descendants("member").ToDictionary(e => e.Attribute("name")!.Value);
foreach (var type in new[] { owner, generic })
    if (!docs.TryGetValue(type.GetDocumentationCommentId()!, out var contract) || contract.Element("summary") is null || contract.Element("remarks") is null)
        throw new Exception("Internal setup/retirement/typed ownership contract XML missing.");
var negative = CSharpCompilation.Create("UnrelatedConsumer", [CSharpSyntaxTree.ParseText("class C { Korda.Interop.Async.AsyncOperation<int>? operation; }")], references,
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
if (!negative.GetDiagnostics().Any(d => d.Id == "CS0122")) throw new Exception("Application consumer can access internal operation plumbing.");
using var baseline = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "src/Interop.Async/public-api.expected.json")));
if (baseline.RootElement.GetProperty("symbols").GetArrayLength() != 0) throw new Exception("Unexpected public symbol baseline.");
var doc = docs[owner.GetDocumentationCommentId()!];
string reference = "# Internal Task bridge reference\n\nGenerated from the compiled Korda.Core assembly and XML by `tests/Interop.Async/ApiCheck`.\n\nA07 adds **zero public types or members**. Exact domain Async methods return `Task<TResult>` and use their own typed native create/start/take ABI. The callback cookie, control leases, terminal latch and retirement coordinator remain internal.\n\n**Implementation summary:** " + string.Join(" ", doc.Element("summary")!.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) + "\n\nThe compiled static nongeneric UCO entry uses exact Cdecl `(nuint, uint, NwErrorInfoV1*) -> uint`. Six namespace types are internal. `AsyncOperation<TResult>.Start()` returns `Task<TResult>`; TResult never crosses the ABI. An unrelated consumer is rejected with CS0122. XML documents setup settlement, external cancellation drain, one-time typed result transfer and independent native retirement.\n";
string path = Path.Combine(root, "src/Interop.Async/api-reference.md");
if (args.Contains("--write")) File.WriteAllText(path, reference);
else if (File.ReadAllText(path) != reference) throw new Exception("Generated async reference is stale.");
Console.WriteLine("A07 compiled API check: public symbols=0; exact terminal UCO=passed; Task<TResult>=passed; unrelated consumer CS0122=passed; XML/reference=passed.");
