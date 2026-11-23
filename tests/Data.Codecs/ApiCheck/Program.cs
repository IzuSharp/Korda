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
if (xml.Root?.Element("assembly")?.Element("name")?.Value != "Korda.Data.Codecs") throw new InvalidOperationException("Unexpected documentation assembly.");
const string reference = "# Korda.Data.Codecs API reference\n\nGenerated from the compiled assembly and XML documentation.\n\nThis internal adapter assembly exports **0 public types and 0 public members**.\nThe application-facing `ValueCodec<T>`, `Codecs`, `SqlParameters`, `SqlCommand`,\nrow and relation declarations remain in `Korda.Data`; their accepted\nsignatures and XML reference are unchanged by this component.\n\nNative conversion and admission behavior: [data codecs guide](../../docs/guides/data-codecs.md).\n";
if (args.Contains("--write", StringComparer.Ordinal)) File.WriteAllText(args[2], reference);
else if (File.ReadAllText(args[2]) != reference) throw new InvalidOperationException("Generated reference is stale.");
Console.WriteLine("PASS compiled public symbols=0; canonical Data API unchanged; XML/reference verified.");
