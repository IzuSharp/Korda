using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using NativeWeave.Generators.Rust;

if (args.Length != 4) throw new ArgumentException("sidecar adapter inventory linked-symbols");
var sidecar = File.ReadAllBytes(args[0]); var adapter = File.ReadAllBytes(args[1]);
var inventory = File.ReadAllBytes(args[2]); var linked = File.ReadAllLines(args[3]);
var count = 0;
void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); count++; }
void Reject(Action action, string code)
{
    try { action(); throw new InvalidOperationException("Accepted invalid case: " + code); }
    catch (Exception e) when (e.Message.Contains(code, StringComparison.Ordinal)) { count++; }
}
byte[] Edit(byte[] input, Action<JsonNode> action) { var node = JsonNode.Parse(input)!; action(node); return Encoding.UTF8.GetBytes(node.ToJsonString()); }
var original = ExportEmitter.Emit(sidecar, adapter, inventory, linked);
Check(original == ExportEmitter.Emit(sidecar, adapter, inventory, linked.Reverse()), "deterministic reordered symbols");
Check(original.Header.Contains("NwFixtureOwner * * output", StringComparison.Ordinal), "pointer-to-owner transfer declaration");
Check(original.Assertions.Contains("unsafe extern \"C\" fn(*mut NwFixtureOwner)", StringComparison.Ordinal), "release exact function type");
Check(ExportEmitter.CType("const:mut:u8") == "uint8_t * const *", "nested pointer constness");
Reject(() => ExportEmitter.Emit(sidecar, adapter.Concat(new byte[] { 10 }).ToArray(), inventory, linked), "NWGEN001");
Reject(() => ExportEmitter.Emit(sidecar, adapter, inventory, linked.Skip(1)), "NWABI002");
Reject(() => ExportEmitter.Emit(sidecar, adapter, inventory, linked.Append("unexpected_export")), "NWABI002");
Reject(() => ExportEmitter.Emit(sidecar, adapter, inventory, linked.Append(linked[0])), "NWABI002");
Reject(() => ExportEmitter.Emit(sidecar, adapter, Edit(inventory, x => x["exports"]![0]!["generic"] = true), linked), "NWABI001");
Reject(() => ExportEmitter.Emit(sidecar, adapter, Edit(inventory, x => x["exports"]![0]!["return_type"] = "(u32, u32)"), linked), "NWABI001");
Reject(() => ExportEmitter.Emit(sidecar, adapter, Edit(inventory, x => x["exports"]![0]!["calling_convention"] = "C-unwind"), linked), "NWABI001");
Reject(() => ExportEmitter.Emit(sidecar, adapter, Edit(inventory, x => x["adapter_sha256"] = new string('a', 64)), linked), "NWGEN001");
Reject(() => ExportEmitter.Emit(Edit(sidecar, x => x["records"]![0]!["fields"]![1]!["offset"] = 12), adapter, inventory, linked), "NWABI002");
Reject(() => ExportEmitter.Emit(Edit(sidecar, x => x["exports"]![0]!["parameters"]![0]!["type"] = "bool"), adapter, inventory, linked), "NWABI001");
using (var cancellation = new CancellationTokenSource())
{
    cancellation.Cancel();
    try { ExportEmitter.Emit(sidecar, adapter, inventory, linked, cancellation.Token); throw new InvalidOperationException("Cancellation ignored"); }
    catch (OperationCanceledException) { count++; }
}
var assembly = typeof(ExportEmitter).Assembly.Location;
using (var stream = File.OpenRead(assembly))
using (var pe = new PEReader(stream))
{
    var metadata = pe.GetMetadataReader();
    Check(!metadata.TypeDefinitions.Any(x => (metadata.GetTypeDefinition(x).Attributes & TypeAttributes.VisibilityMask) == TypeAttributes.Public), "maintainer tool has no application public API");
}
var xml = XDocument.Load(Path.ChangeExtension(assembly, ".xml"));
Check(xml.Root!.Element("assembly")!.Element("name")!.Value == "NativeWeave.Generators.Rust", "compiled XML identity");
Console.WriteLine($"PASS A11 emitter: {count} assertions; deterministic C/Rust declarations, strict provenance, unsupported-shape and inventory negatives.");
