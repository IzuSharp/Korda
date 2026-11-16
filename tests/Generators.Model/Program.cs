using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NativeWeave.Generators.Model;

if (args.Length >= 3 && args[0] == "--emit")
{
    var description = BindingReader.Read(File.ReadAllBytes(args[1]), args[1]);
    File.WriteAllBytes(args[2], description.GetCanonicalBytes()); Console.WriteLine(description.Digest); return;
}
if (args.Length < 4) throw new ArgumentException("Tests <sidecar> <parsed-inventory> <linked-symbols> <native-fixture> [--require-aot]");
if (args.Length > 4 && args[4] == "--omission")
{
    using var omissionJson = JsonDocument.Parse(File.ReadAllBytes(args[5]));
    var omission = SourceInventory.Read(File.ReadAllBytes(args[5]), omissionJson.RootElement.GetProperty("adapter_sha256").GetString()!);
    if (omission.Length != 1 || omission[0].Symbol != "unsupported_tuple" || !omission[0].ParameterTypes[0].Contains('(')) throw new InvalidOperationException("Parser dropped unsupported source declaration.");
    try { BindingDescription.VerifyExtractionCompleteness(omission, []); throw new InvalidOperationException("Silently omitted tuple was accepted."); }
    catch (BindingValidationException e) when (e.Code == "NWABI002") { Console.WriteLine("PASS actual csbindgen success/tuple omission rejected: " + e.Message); }
}
var assertions = 0;
void Check(bool condition, string reason) { assertions++; if (!condition) throw new InvalidOperationException(reason); }
var bytes = File.ReadAllBytes(args[0]);
var model = BindingReader.Read(bytes, args[0]);
var node = JsonNode.Parse(bytes)!.AsObject();
var digest = node["provenance"]!["adapter_sha256"]!.GetValue<string>();
var parsed = SourceInventory.Read(File.ReadAllBytes(args[1]), digest);
var linked = File.ReadAllLines(args[2]).Where(x => x.Length != 0).ToArray();
model.VerifyInventories(parsed, model.Symbols, linked);
Check(model.Symbols.Length == 2 && model.Records.Length == 1 && model.ManagedSymbols.Length == 30, "Complete narrow inventory");
var returned = model.GetCanonicalBytes(); returned[0] ^= 1;
Check(model.GetCanonicalBytes()[0] == (byte)'{', "Canonical output cannot mutate model");
Check(model.Digest == Convert.ToHexStringLower(SHA256.HashData(model.GetCanonicalBytes())), "Canonical digest");
model.VerifyParity(BindingReader.Read(bytes, "elsewhere/model.json")); Check(true, "Locations excluded from parity");

void Reject(string code, Action action, string label)
{
    try { action(); throw new InvalidOperationException("Accepted " + label); }
    catch (BindingValidationException e)
    {
        Check(e.Code == code, label + " diagnostic was " + e.Code);
        Check(e.Location.Path.Length > 0 && e.Location.Line > 0 && e.Location.Byte > 0 && e.Message.Contains(e.Location.Path, StringComparison.Ordinal), "Location-bearing diagnostic");
    }
}
void Bad(string code, Action<JsonObject> mutation, string label)
{
    var changed = node.DeepClone().AsObject(); mutation(changed);
    Reject(code, () => BindingReader.Read(Encoding.UTF8.GetBytes(changed.ToJsonString()), "negative.json"), label);
}
Bad("NWABI002", x => x["schema_version"] = 2, "unknown schema");
Bad("NWABI002", x => x["schema_version"] = "1", "wrong value kind");
Bad("NWABI002", x => x["required_future_semantics"] = true, "unknown required field");
Bad("NWABI002", x => x.Remove("owners"), "missing field");
Bad("NWABI002", x => x["module"]!["pointer_width_bits"] = 32, "wrong target pointer width");
Bad("NWABI002", x => x["module"]!["target"] = "aarch64-unknown-linux-gnu", "target mismatch");
Bad("NWGEN001", x => x["provenance"]!["adapter_sha256"] = "latest", "unpinned source");
Bad("NWGEN001", x => x["provenance"]!["upstream"]![0]!["commit"] = "main", "moving upstream");
Bad("NWGEN001", x => x["provenance"]!["upstream"]![0]!["repository"] = "https://secret@example.test/x", "credential in provenance");
Bad("NWGEN001", x => x["capabilities"]!.AsArray().Add((JsonNode?)JsonValue.Create("model.extent.v1")), "duplicate capability");
Bad("NWABI002", x => x["exports"]![0]!["capability"] = "unknown.v1", "unknown capability");
Bad("NWGEN001", x => x["exports"]!.AsArray().Add(x["exports"]![0]!.DeepClone()), "duplicate symbol");
Bad("NWABI002", x => x["exports"]![0]!["calling_convention"] = "stdcall", "calling convention drift");
Bad("NWABI001", x => x["exports"]![0]!["parameters"]![0]!["type"] = "bool", "bool ambiguity");
Bad("NWABI001", x => x["exports"]![0]!["parameters"]![0]!["type"] = "char", "char ambiguity");
Bad("NWABI001", x => x["exports"]![0]!["parameters"]![0]!["type"] = "Option<u32>", "generic ABI type");
Bad("NWABI001", x => x["records"]![0]!["fields"]![0]!["type"] = "String", "implicit string");
Bad("NWABI002", x => x["records"]![0]!["fields"]![1]!["offset"] = 0, "overlapping layout");
Bad("NWABI002", x => x["records"]![0]!["size"] = 40, "tail layout drift");
Bad("NWABI002", x => x["records"]![0]!["alignment"] = 4, "alignment drift");
Bad("NWOWN001", x => x["exports"]![0]!["parameters"]![2]!["maximum_bytes"] = 0, "unbounded pointer");
Bad("NWOWN001", x => x["exports"]![0]!["parameters"]![2]!["borrow_end"] = "owner", "escaping borrow");
Bad("NWOWN001", x => x["exports"]![0]!["parameters"]![2]!["owner"] = "missing", "unknown owner");
Bad("NWABI001", x => x["exports"]![0]!["parameters"]![0]!["nullable"] = true, "nullable scalar");
Bad("NWGEN001", x => x["managed_symbols"]!.AsArray().Add(x["managed_symbols"]![0]!.DeepClone()), "managed identity collision");
Reject("NWABI002", () => BindingReader.Read("{\n\"schema_version\":1,\"schema_version\":1}"u8, "duplicate.json"), "duplicate JSON key");
Reject("NWABI002", () => BindingReader.Read("{\n\"schema_version\":}"u8, "malformed.json"), "malformed sidecar");
Reject("NWABI002", () => BindingReader.Read(new byte[1024 * 1024 + 1], "oversize.json"), "input budget");
Reject("NWABI002", () => model.VerifyInventories(parsed, model.Symbols.Take(1), linked), "successful extractor silently omitted an export");
Reject("NWABI002", () => model.VerifyInventories(parsed, model.Symbols.Append("nw_extra"), linked), "extra generated symbol");
Reject("NWABI002", () => model.VerifyInventories(parsed, model.Symbols, linked.Take(1)), "missing linked export");
Reject("NWABI002", () => model.VerifyInventories(parsed.Add(parsed[0]), model.Symbols, linked), "duplicate parsed symbol");
var tuple = parsed.SetItem(0, parsed[0] with { ParameterTypes = ["(u32,u32)", "u32", "*mut NwAbiInfoV1"] });
Reject("NWABI001", () => model.VerifyInventories(tuple, model.Symbols, linked), "Rust tuple omission countercase");
Reject("NWABI001", () => model.VerifyInventories(parsed.SetItem(0, parsed[0] with { Generic = true }), model.Symbols, linked), "Rust generic export");
Reject("NWABI002", () => model.VerifyInventories(parsed.SetItem(0, parsed[0] with { ReturnType = "u64" }), model.Symbols, linked), "signature drift");
Reject("NWGEN001", () => SourceInventory.Read(File.ReadAllBytes(args[1]), new string('0', 64)), "stale parser inventory");
Reject("NWABI002", () => SourceInventory.Read("{}"u8, digest), "malformed inventory");
Reject("NWABI002", () => SourceInventory.Read("{bad}"u8, digest), "invalid inventory JSON");
var changedModel = node.DeepClone(); changedModel["module"]!["abi_minor"] = 1;
Reject("NWGEN001", () => model.VerifyParity(BindingReader.Read(Encoding.UTF8.GetBytes(changedModel.ToJsonString()), "changed.json")), "descriptor parity");
Check(BindingReader.Read(Encoding.UTF8.GetBytes(changedModel.ToJsonString()), "changed.json").Digest != model.Digest, "One semantic edit changes digest");
var qualified = node.DeepClone(); qualified["managed_symbols"] = new JsonArray("T:One.Widget", "T:Two.Widget", "T:One.Outer+Widget", "T:One.Box{System.Int32}");
Check(BindingReader.Read(Encoding.UTF8.GetBytes(qualified.ToJsonString()), "qualified.json").ManagedSymbols.Length == 4, "Fully qualified identities preserve namespace and closed-generic distinctions");
using (var canceled = new CancellationTokenSource())
{
    canceled.Cancel();
    try { BindingReader.Read(bytes, "canceled.json", canceled.Token); throw new InvalidOperationException("Ignored cancellation"); }
    catch (OperationCanceledException) { Check(true, "Canceled pass publishes no partial model"); }
}

// Exercise optional owner/operation semantics without creating a runtime protocol.
var asyncNode = node.DeepClone().AsObject();
JsonObject Param(string name, string type, string borrow = "call", string owner = "operation", string direction = "in", bool copy = false) =>
    new()
    {
        ["name"] = name,
        ["type"] = type,
        ["direction"] = direction,
        ["nullable"] = false,
        ["borrow_end"] = borrow,
        ["owner"] = owner,
        ["mutable"] = type.StartsWith("mut:", StringComparison.Ordinal),
        ["copy_before_accept"] = copy,
        ["maximum_bytes"] = 64
    };
JsonObject Export(string symbol, JsonArray parameters, bool release = false) => new()
{
    ["symbol"] = symbol,
    ["calling_convention"] = "C",
    ["return"] = release ? "void" : "u32",
    ["capability"] = "model.extent.v1",
    ["error"] = release ? "cleanup-no-fail" : "status-v1",
    ["thread"] = "serialized",
    ["reentrancy"] = "forbidden",
    ["targets"] = new JsonArray("linux-x64"),
    ["parameters"] = parameters,
};
asyncNode["owners"]!.AsArray().Add((JsonNode?)new JsonObject { ["kind"] = "operation", ["module"] = "nativeweave.model.fixture", ["release"] = "nw_release", ["clone"] = "", ["parent"] = "", ["release_executor"] = "runtime", ["close_progress"] = "runtime-drives" });
asyncNode["exports"]!.AsArray().Add((JsonNode?)Export("nw_create", new JsonArray(Param("output", "mut:mut:operation", "transfer-success", direction: "out"))));
foreach (var s in new[] { "nw_start", "nw_cancel", "nw_take", "nw_retire", "nw_release" })
    asyncNode["exports"]!.AsArray().Add((JsonNode?)Export(s, new JsonArray(Param("operation", "mut:operation")), s == "nw_release"));
asyncNode["exports"]!.AsArray().First(x => x!["symbol"]!.GetValue<string>() == "nw_start")!["parameters"]!.AsArray()
    .Add((JsonNode?)Param("input", "const:u8", owner: "", copy: true));
asyncNode["operations"]!.AsArray().Add((JsonNode?)new JsonObject { ["name"] = "query", ["owner"] = "operation", ["create"] = "nw_create", ["start"] = "nw_start", ["request_cancel"] = "nw_cancel", ["take_result"] = "nw_take", ["try_retire"] = "nw_retire", ["release"] = "nw_release", ["input_end"] = "copied-before-accept", ["state_profile"] = "D06-v1" });
BindingDescription AsyncRead(JsonNode n) => BindingReader.Read(Encoding.UTF8.GetBytes(n.ToJsonString()), "async.json");
Check(AsyncRead(asyncNode).Operations.Length == 1, "Explicit copied-input D06 profile accepted");
var unsafePin = asyncNode.DeepClone(); unsafePin["exports"]!.AsArray().First(x => x!["symbol"]!.GetValue<string>() == "nw_start")!["parameters"]![1]!["copy_before_accept"] = false;
Reject("NWASYNC001", () => AsyncRead(unsafePin), "async call-only pin");
var wrongDestructor = asyncNode.DeepClone(); wrongDestructor["owners"]![0]!["release"] = "nw_take";
Reject("NWOWN001", () => AsyncRead(wrongDestructor), "fallible/wrong destructor");
var wrongModule = asyncNode.DeepClone(); wrongModule["owners"]![0]!["module"] = "other.module";
Reject("NWOWN001", () => AsyncRead(wrongModule), "cross-module free");
var cyclic = asyncNode.DeepClone(); cyclic["owners"]![0]!["parent"] = "operation";
Reject("NWOWN001", () => AsyncRead(cyclic), "cyclic parent");
var wrongStep = asyncNode.DeepClone(); wrongStep["operations"]![0]!["try_retire"] = "nw_take";
Reject("NWASYNC001", () => AsyncRead(wrongStep), "take is not retirement");

var callbackNode = asyncNode.DeepClone();
var cookie = new JsonObject { ["name"] = "cookie", ["type"] = "usize", ["direction"] = "in", ["nullable"] = false, ["borrow_end"] = "none", ["owner"] = "", ["mutable"] = false, ["copy_before_accept"] = false, ["maximum_bytes"] = 0 };
var callbackExport = Export("nw_notify", new JsonArray(cookie)); callbackExport["reentrancy"] = "notification-only";
callbackNode["exports"]!.AsArray().Add((JsonNode?)callbackExport);
callbackNode["callbacks"]!.AsArray().Add((JsonNode?)new JsonObject { ["symbol"] = "nw_notify", ["cookie_index"] = 0, ["notification_owner"] = "operation", ["reentrancy"] = "notification-only", ["close"] = "nw_cancel", ["drain"] = "nw_retire", ["profile"] = "D05-close-drain-v1" });
Check(AsyncRead(callbackNode).Exports.Any(x => x.Symbol == "nw_notify"), "Blittable notification callback with retirement authority");
var badCookie = callbackNode.DeepClone(); badCookie["callbacks"]![0]!["cookie_index"] = 1;
Reject("NWABI001", () => AsyncRead(badCookie), "callback cookie mismatch");
var missingDrain = callbackNode.DeepClone(); missingDrain["callbacks"]![0]!["drain"] = "nw_missing";
Reject("NWABI002", () => AsyncRead(missingDrain), "callback missing drain");
var reentrant = callbackNode.DeepClone(); reentrant["callbacks"]![0]!["reentrancy"] = "user-code";
Reject("NWASYNC001", () => AsyncRead(reentrant), "callback user-code reentrancy");

NativeLibrary.SetDllImportResolver(typeof(Probe).Assembly, (name, _, _) => name == "nativeweave_model_fixture" ? NativeLibrary.Load(Path.GetFullPath(args[3])) : 0);
unsafe
{
    ulong* storage = stackalloc ulong[4];
    Check(Probe.Abi(1, 32, (byte*)storage) == 0, "Actual A01 bootstrap through static import");
    var wire = new ReadOnlySpan<byte>(storage, 32);
    Check(BinaryPrimitives.ReadUInt32LittleEndian(wire) == model.Records[0].Size, "Model and actual native layout size");
    Check(BinaryPrimitives.ReadUInt32LittleEndian(wire[4..]) == 1 && BinaryPrimitives.ReadUInt32LittleEndian(wire[12..]) == 64, "Native ABI identity");
    Check(BinaryPrimitives.ReadUInt64LittleEndian(wire[24..]) == 0, "Reserved bytes remain zero");
}
foreach (ulong capacity in new ulong[] { 0, 1, 7, 4096, ulong.MaxValue })
    foreach (ulong offset in new ulong[] { 0, 1, 4096, ulong.MaxValue })
        foreach (ulong length in new ulong[] { 0, 1, 4096, ulong.MaxValue })
            Check(Probe.Extent(offset, length, capacity) == (offset <= capacity && length <= capacity - offset ? 0u : 3u), "Checked native extent");
if (args.Contains("--require-aot", StringComparer.Ordinal)) Check(!RuntimeFeature.IsDynamicCodeSupported, "Actual NativeAOT, not JIT relabeling");
Console.WriteLine($"PASS A09 assertions={assertions}; symbols={model.Symbols.Length}; digest={model.Digest}; native_calls=81; dynamic_code={RuntimeFeature.IsDynamicCodeSupported}");

internal static partial class Probe
{
    [LibraryImport("nativeweave_model_fixture", EntryPoint = "nw_model_get_abi_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial uint Abi(uint major, uint capacity, byte* output);
    [LibraryImport("nativeweave_model_fixture", EntryPoint = "nw_model_validate_extent_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint Extent(ulong offset, ulong length, ulong capacity);
}
