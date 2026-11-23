using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

if (args.Length < 4) throw new ArgumentException("ApiCheck <assembly> <xml> <signatures.json> <reference.md> [--write]");
var assembly = Assembly.LoadFrom(Path.GetFullPath(args[0]));
if (assembly.GetExportedTypes().Length != 0) throw new InvalidOperationException("Internal binding leaked public CLR types.");
using var pe = new PEReader(File.OpenRead(args[0]));
var metadata = pe.GetMetadataReader();
var blobs = metadata.FieldDefinitions.Select(h => metadata.GetFieldDefinition(h)).Where(f => metadata.GetString(f.Name).StartsWith("nw_db_", StringComparison.Ordinal)).ToDictionary(f => metadata.GetString(f.Name), f => metadata.GetBlobBytes(f.Signature), StringComparer.Ordinal);
var calls = assembly.GetType("Korda.Query.Internal.NativeCalls", true)!;
var xml = XDocument.Load(args[1]);
if (xml.Root?.Element("assembly")?.Element("name")?.Value != "Korda.Query.Binding") throw new InvalidOperationException("XML assembly differs.");
var members = xml.Descendants("member").ToDictionary(e => e.Attribute("name")!.Value, StringComparer.Ordinal);
using var inventory = JsonDocument.Parse(File.ReadAllText(args[2]));
var signatures = inventory.RootElement.GetProperty("signatures").EnumerateArray().Select(e => e.GetString()!).ToArray();
var aliases = new Dictionary<string, string>(StringComparer.Ordinal)
{
    ["nw_status"] = "System.UInt32",
    ["void"] = "System.Void",
    ["nw_db_ref"] = "System.IntPtr",
    ["nw_db_ref*"] = "System.IntPtr*",
    ["nw_db_buffer*"] = "System.IntPtr",
    ["nw_db_buffer**"] = "System.IntPtr*",
    ["nw_db_token"] = "System.UInt64",
    ["nw_db_token*"] = "System.UInt64*",
    ["uint32_t"] = "System.UInt32",
    ["uint32_t*"] = "System.UInt32*",
    ["uint64_t"] = "System.UInt64",
    ["int64_t*"] = "System.Int64*",
    ["nw_bytes_view_v1"] = "Korda.Interop.Abi.NwBytesViewV1",
    ["nw_bytes_view_v1*"] = "Korda.Interop.Abi.NwBytesViewV1*",
    ["nw_error_info_v1*"] = "Korda.Interop.Abi.NwErrorInfoV1*",
    ["const nw_operation_callbacks_v1*"] = "Korda.Interop.Abi.NwOperationCallbacksV1*"
};
foreach (var signature in signatures)
{
    var match = Regex.Match(signature, @"^(\w+) NW_CDECL (\w+)\((.*)\);$");
    var result = aliases[match.Groups[1].Value]; var name = match.Groups[2].Value;
    var parameters = match.Groups[3].Value.Split(',').Select(p => aliases[Regex.Replace(p.Trim(), @"\s+\w+$", "")]).ToArray();
    var field = calls.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new InvalidOperationException("Missing exact native call " + name);
    var type = field.FieldType;
    if (!type.IsFunctionPointer || !type.IsUnmanagedFunctionPointer || type.GetFunctionPointerReturnType().FullName != result || !type.GetFunctionPointerParameterTypes().Select(t => t.FullName).SequenceEqual(parameters))
        throw new InvalidOperationException("Compiled signature differs for " + name);
    // CLI encodes explicit Cdecl as 0x01 in this function-pointer signature;
    // reflection's custom convention list is empty for that direct encoding.
    var signatureBlob = blobs[name];
    if (signatureBlob.Length < 3 || signatureBlob[0] != 0x06 || signatureBlob[1] != 0x1b || signatureBlob[2] != 0x01) throw new InvalidOperationException("Compiled Cdecl signature header differs for " + name);
    if (!members.TryGetValue("F:Korda.Query.Internal.NativeCalls." + name, out var member) || string.IsNullOrWhiteSpace(member.Element("summary")?.Value)) throw new InvalidOperationException("Missing exact call XML " + name);
}
if (calls.GetFields(BindingFlags.Instance | BindingFlags.NonPublic).Count(f => f.FieldType.IsFunctionPointer) != signatures.Length) throw new InvalidOperationException("Extra native call descriptor.");
var reference = "# Korda.Query.Binding API reference\n\nGenerated from compiled internal function-pointer declarations and XML documentation.\n\nThis assembly exports **0 public types and 0 public members**. The application-facing\nTable, Column, Relation and Projection contracts remain in `Korda.Data`.\nThe public Query/DataSession facade belongs to B13/B04; this component adds no\napplication facade or managed SQL tree.\n\nThe compiled inventory below contains **42 exact Cdecl declarations**. Shared\nrecords are the canonical A01 layouts. An inventory entry describes a domain\noperation; it does not claim that B09 exports the B04/B05/B06/B11 implementation.\n\n```c\n" + string.Join('\n', signatures) + "\n```\n\nOwnership, operation costs and downstream obligations: [query binding guide](../../docs/guides/query-binding.md).\n";
if (args.Contains("--write", StringComparer.Ordinal)) File.WriteAllText(args[3], reference); else if (File.ReadAllText(args[3]) != reference) throw new InvalidOperationException("Generated API reference is stale.");
Console.WriteLine($"PASS compiled public symbols=0; exact Cdecl calls={signatures.Length}; XML/reference complete.");
