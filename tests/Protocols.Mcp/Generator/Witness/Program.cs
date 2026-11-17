using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ExternalModels;
using LocalModels;
using NativeWeave.Server.Mcp;

int assertions = 0;
var proof = Reflection.Proof(Assembly.GetExecutingAssembly()) ?? throw new InvalidOperationException("Compiled layout helper missing.");
bool Covered(Type type) => proof.Declared(type) >= 0;
void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); assertions++; }
void Field(Type type, string name, int width)
{
    Check(Covered(type), "marker absent for " + type);
    var fields = Reflection.Fields(type);
    Check(fields.Length <= proof.Declared(type), "declared-field proof understated: " + type);
    Check(proof.Reflection(type) >= fields.Length, "reflection-cache proof understated: " + type);
    var field = fields.SingleOrDefault(field => field.Name == name);
    Check(field is not null, "field absent: " + type + "." + name);
    Check(RuntimeHelpers.SizeOf(field!.FieldType.TypeHandle) == width, "wrong closed field width: " + field);
}

var privateInfo = Container.Metadata;
Field(privateInfo.Type, "_hidden", 32768);
Field(privateInfo.Type, "<Ignored>k__BackingField", 32768);
Field(privateInfo.Type.BaseType!, "_hiddenBase", 32768);
Check(JsonSerializer.Deserialize("{\"Number\":7}"u8, privateInfo) is not null, "private source-generated context deserialization");
Check(!Covered(typeof(Unrelated)), "ignored reference heap type must not acquire coverage");

// The only analyzer roots for this separate precompiled DTO/context assembly
// are the concrete application AddTool call below. The library has no analyzer.
JsonTypeInfo external = ExternalMetadata.Root;
Field(external.Type, "_hidden", 32768);
Field(external.Type, "<Ignored>k__BackingField", 32768);
Field(external.Type.BaseType!, "_hiddenBase", 32768);
Field(external.Properties.Single(static item => item.Name == "Items").PropertyType.GetElementType()!, "_hidden", 32768);
Field(external.Properties.Single(static item => item.Name == "Included").PropertyType, "_hidden", 32768);
Field(external.Type, "Changed", IntPtr.Size);
Check(JsonSerializer.Deserialize("{\"Items\":[{\"Value\":9}]}"u8, external) is ExternalRoot { Items.Length: 1 }, "external static context deserialization");
Check(Reflection.Proof(typeof(ExternalRoot).Assembly) is null, "external assembly has no analyzer-generated helper");
Check(!Covered(ExternalMetadata.Unmarked.Type), "external unregistered generic-helper DTO has no false proof");

JsonTypeInfo padded = LocalContext.Default.ClassPadding;
Check(Covered(padded.Type), "class explicit size marker");
Check(padded.Type.StructLayoutAttribute?.Size == 524288, "StructLayout size survives NativeAOT");
Check(RuntimeHelpers.SizeOf(padded.Type.TypeHandle) == IntPtr.Size, "reference SizeOf is a slot, not an instance bound");
JsonTypeInfo value = LocalContext.Default.StructRoot;
Check(RuntimeHelpers.SizeOf(value.Type.TypeHandle) >= 32768, "non-nullable erased inline width");
Check(RuntimeHelpers.SizeOf(LocalContext.Default.ValueContainer.Properties[0].PropertyType.TypeHandle) >= 32768, "nullable erased inline width");
Field(value.Properties.Single(static item => item.Name == "Child").PropertyType, "_hidden", 32768);
Check(JsonSerializer.Deserialize("{\"Child\":{\"Number\":1}}"u8, value) is StructRoot { Child.Number: 1 }, "non-nullable struct metadata remains supported");
Field(LocalContext.Default.PrimaryDto.Type, "<number>P", sizeof(int));
Field(LocalContext.Default.PrimaryDto.Type, "Changed", IntPtr.Size);
Check(proof.Reflection(typeof(ExampleKind)) >= 3, "enum cache proof covers constants and underlying field");
Field(LocalContext.Default.RecordDto.Type, "<Text>k__BackingField", IntPtr.Size);
Check(JsonSerializer.Deserialize("{\"Text\":\"record\"}"u8, LocalContext.Default.RecordDto) == new RecordDto("record"), "record static metadata and fieldless IEquatable proof");
Console.WriteLine($"MCP layout preservation: {assertions} assertions passed; dynamic={RuntimeFeature.IsDynamicCodeSupported}; helper=bounded.");

internal static class Application
{
    // This is an actual product API call over a precompiled external context.
    // Registration is not executed by this field-preservation-only witness.
    internal static void Register(McpRegistry registry) => registry.AddTool("external", "external context", ExternalMetadata.Root, ExternalMetadata.Root,
        static (ExternalRoot value, McpCallContext _) => Task.FromResult(McpToolResult<ExternalRoot>.Success(value)));
}

internal static class Reflection
{
    [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "The exact generated DynamicDependency annotations and coverage markers are the subject of this NativeAOT witness; this suppression itself preserves no fields.")]
    internal static FieldInfo[] Fields(Type type) => type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "This exact fixed-name generated helper is explicitly rooted by its reachable module initializer; testing that preservation is the purpose of this NativeAOT witness.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "The generator roots its two exact nonpublic helper methods; this suppression itself preserves no methods.")]
    internal static (Func<Type, int> Declared, Func<Type, int> Reflection)? Proof(Assembly assembly)
    {
        var type = assembly.GetType("NativeWeave.Server.Mcp.Generated.LayoutPreservation", false);
        if (type is null) return null;
        var declared = type.GetMethod("DeclaredInstanceFieldCount", BindingFlags.Static | BindingFlags.NonPublic)?.CreateDelegate<Func<Type, int>>();
        var reflection = type.GetMethod("ReflectionFieldCount", BindingFlags.Static | BindingFlags.NonPublic)?.CreateDelegate<Func<Type, int>>();
        return declared is null || reflection is null ? null : (declared, reflection);
    }
}
