using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Text.Json;
using System.Xml.Linq;

string root = Path.GetFullPath(args[0]), sdk = Path.GetFullPath(args[1]), dll = Path.GetFullPath(args[2]);
var references = Directory.GetFiles(Path.Combine(sdk, "packs/Microsoft.NETCore.App.Ref/11.0.0/ref/net11.0"), "*.dll").Select(p => MetadataReference.CreateFromFile(p)).ToList();
var product = MetadataReference.CreateFromFile(dll); references.Add(product);
var compilation = CSharpCompilation.Create("CallbacksApiCheck", references: references, options: new(OutputKind.DynamicallyLinkedLibrary, metadataImportOptions: MetadataImportOptions.All));
var assembly = (IAssemblySymbol)compilation.GetAssemblyOrModuleSymbol(product)!;
var types = new List<INamedTypeSymbol>();
void Walk(INamespaceSymbol ns)
{
    foreach (var child in ns.GetNamespaceMembers()) Walk(child);
    if (ns.ToDisplayString() == "Korda.Interop.Callbacks") types.AddRange(ns.GetTypeMembers());
}
Walk(assembly.GlobalNamespace);
if (types.Count != 8 || types.Any(t => t.DeclaredAccessibility != Accessibility.Internal)) throw new Exception("A06 must expose no public callback plumbing.");
var owner = types.Single(t => t.Name == "CallbackRegistration");
if (!owner.IsSealed) throw new Exception("Registration must preserve sealed reference identity.");
var entry = owner.GetMembers("OnEvent").OfType<IMethodSymbol>().Single();
if (!entry.IsStatic || entry.IsGenericMethod || entry.DeclaredAccessibility != Accessibility.Private || entry.ReturnType.SpecialType != SpecialType.System_UInt32 ||
    entry.Parameters.Length != 4 || entry.Parameters[0].Type.SpecialType != SpecialType.System_UIntPtr ||
    entry.Parameters[1].Type.SpecialType != SpecialType.System_UInt32 || entry.Parameters[2].Type is not IPointerTypeSymbol pointer ||
    pointer.PointedAtType.SpecialType != SpecialType.System_Void || entry.Parameters[3].Type.SpecialType != SpecialType.System_UInt64)
    throw new Exception("Compiled callback signature differs from the exact v1 ABI.");
var uco = entry.GetAttributes().Single(a => a.AttributeClass?.Name == "UnmanagedCallersOnlyAttribute");
if (!uco.NamedArguments.Any(a => a.Key == "CallConvs" && a.Value.Values.Any(v => v.Value is INamedTypeSymbol t && t.Name == "CallConvCdecl")))
    throw new Exception("Cdecl callback convention is missing.");
var xml = XDocument.Load(Path.ChangeExtension(dll, ".xml"));
var docs = xml.Descendants("member").ToDictionary(e => e.Attribute("name")!.Value);
if (!docs.TryGetValue(owner.GetDocumentationCommentId()!, out var doc) || doc.Element("summary") is null || doc.Element("remarks") is null)
    throw new Exception("Internal registration contract XML missing.");
var negative = CSharpCompilation.Create("UnrelatedConsumer", [CSharpSyntaxTree.ParseText("class C { object M() => new Korda.Interop.Callbacks.CallbackCoordinator(1); }")], references,
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
if (!negative.GetDiagnostics().Any(d => d.Id == "CS0122")) throw new Exception("Application consumer can access internal callback plumbing.");
using var baseline = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "src/Interop.Callbacks/public-api.expected.json")));
if (baseline.RootElement.GetProperty("symbols").GetArrayLength() != 0) throw new Exception("Unexpected public symbol baseline.");
string reference = "# Internal callback registration reference\n\nGenerated from the compiled Korda.Core assembly and XML by `tests/Interop.Callbacks/ApiCheck`.\n\nA06 adds **zero public types or members**. Application handlers use their domain's typed managed API; the callback table, strong root, native controls and dispatcher remain internal.\n\n**Implementation summary:** " + string.Join(" ", doc.Element("summary")!.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) + "\n\nThe compiled static nongeneric UCO entry uses exact Cdecl `(nuint, uint, void*, ulong) -> uint`. Its eight namespace types are internal; an unrelated consumer is rejected with CS0122. The registration XML records two-sided retirement, ownership and bounded dispatcher semantics. Existing public Core signatures remain unchanged.\n";
string path = Path.Combine(root, "src/Interop.Callbacks/api-reference.md");
if (args.Contains("--write")) File.WriteAllText(path, reference);
else if (File.ReadAllText(path) != reference) throw new Exception("Generated callback reference is stale.");
Console.WriteLine("A06 compiled API check: public symbols=0; exact UCO=passed; unrelated consumer CS0122=passed; XML/reference=passed.");
