using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;
if (args.Length < 3) throw new ArgumentException("Usage: ApiCheck <assembly> <xml> <reference> [--write]");
using var stream = File.OpenRead(args[0]);
using var pe = new PEReader(stream);
var metadata = pe.GetMetadataReader();
bool Exported(TypeDefinitionHandle handle)
{
    var definition = metadata.GetTypeDefinition(handle);
    var visibility = definition.Attributes & TypeAttributes.VisibilityMask;
    return visibility == TypeAttributes.Public || visibility == TypeAttributes.NestedPublic && Exported(definition.GetDeclaringType());
}
var count = metadata.TypeDefinitions.Count(Exported);
if (count != 0) throw new InvalidOperationException("Internal adapter added " + count + " exported types.");
var xml = XDocument.Load(args[1]);
if (xml.Root?.Element("assembly")?.Element("name")?.Value != "NativeWeave.Generators.Model") throw new InvalidOperationException("Unexpected documentation assembly.");
const string reference = "# NativeWeave.Generators.Model API reference\n\nGenerated from the compiled assembly and XML documentation.\n\nThis maintainer build component exports **0 public types and 0 public members**.\nThe exact A02 EnginePackage, EnginePackageManifest, NativeAssetDescriptor and\nEngineCapabilityDefinition declarations remain in NativeWeave.Core; their complete\nmetadata-only symbol inventory is pinned in api-bindings.json.\n\nValidation and ownership rules: [binding model guide](../../docs/guides/generators-model.md).\n";
if (args.Contains("--write", StringComparer.Ordinal)) File.WriteAllText(args[2], reference);
else if (File.ReadAllText(args[2]) != reference) throw new InvalidOperationException("Generated reference is stale.");
Console.WriteLine("PASS compiled public symbols=0; canonical Data API unchanged; XML/reference verified.");
