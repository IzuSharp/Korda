using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Xml.Linq;

string root = Path.GetFullPath(args[0]), sdk = Path.GetFullPath(args[1]), dll = Path.GetFullPath(args[2]);
var references = Directory.GetFiles(Path.Combine(sdk, "packs/Microsoft.NETCore.App.Ref/11.0.0/ref/net11.0"), "*.dll").Select(p => MetadataReference.CreateFromFile(p)).ToList();
var product = MetadataReference.CreateFromFile(dll); references.Add(product);
var compilation = CSharpCompilation.Create("LifetimeApiCheck", references: references, options: new(OutputKind.DynamicallyLinkedLibrary, metadataImportOptions: MetadataImportOptions.All));
var assembly = (IAssemblySymbol)compilation.GetAssemblyOrModuleSymbol(product)!;
var types = new List<INamedTypeSymbol>();
void Walk(INamespaceSymbol ns)
{
    foreach (var child in ns.GetNamespaceMembers()) Walk(child);
    if (ns.ToDisplayString() == "Korda.Interop.Lifetime") types.AddRange(ns.GetTypeMembers());
}
Walk(assembly.GlobalNamespace);
if (types.Count != 2 || types.Any(t => t.DeclaredAccessibility != Accessibility.Internal)) throw new Exception("A03 must expose no public ownership plumbing.");
var owner = types.Single(t => t.Name == "NativeResourceOwner");
if (!owner.IsSealed) throw new Exception("Resource owner must have reference identity and sealed lifecycle.");
var xml = XDocument.Load(Path.ChangeExtension(dll, ".xml"));
var docs = xml.Descendants("member").ToDictionary(e => e.Attribute("name")!.Value);
if (!docs.TryGetValue(owner.GetDocumentationCommentId()!, out var doc) || doc.Element("summary") is null) throw new Exception("Internal owner contract XML missing.");
string reference = "# Internal resource ownership reference\n\nGenerated from the compiled Korda.Core assembly and XML by `tests/Interop.Lifetime/ApiCheck`.\n\nA03 adds **zero public types or members**. Its sealed internal NativeResourceOwner supplies logical admission, SafeHandle leasing, nonblocking closure and a separate retirement observation. No public copyable owning struct, raw-pointer factory or new NuGet package is introduced.\n\n**Implementation summary:** " + string.Join(" ", doc.Element("summary")!.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) + "\n\nPublic immutable buffer members are owned and checked by A04. This internal component has no application API compatibility promise; generated adapters compile directly against its shared implementation.\n";
string path = Path.Combine(root, "src/Interop.Lifetime/api-reference.md");
if (args.Contains("--write")) File.WriteAllText(path, reference);
else if (File.ReadAllText(path) != reference) throw new Exception("Generated internal reference is stale.");
Console.WriteLine("A03 compiled API check: public symbols=0; sealed internal ownership=passed; XML/reference=passed.");
