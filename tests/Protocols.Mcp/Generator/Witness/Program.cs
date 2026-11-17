using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ExternalModels;
using LocalModels;
using NativeWeave.Server.Mcp;

int assertions = 0;
var markers = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
    .Where(static item => item.Key == "NativeWeave.Mcp.Layout.v1").Select(static item => item.Value).ToHashSet(StringComparer.Ordinal);
bool Covered(Type type) => markers.Contains(type.Assembly.GetName().Name + "|" + (type.IsGenericType ? type.GetGenericTypeDefinition() : type).FullName);
void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); assertions++; }
void Field(Type type, string name, int width)
{
    Check(Covered(type), "marker absent for " + type);
    var field = Reflection.Fields(type).SingleOrDefault(field => field.Name == name);
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
JsonTypeInfo external = ExternalContext.Default.ExternalRoot;
Field(external.Type, "_hidden", 32768);
Field(external.Type, "<Ignored>k__BackingField", 32768);
Field(external.Type.BaseType!, "_hiddenBase", 32768);
Field(external.Properties.Single(static item => item.Name == "Items").PropertyType.GetElementType()!, "_hidden", 32768);
Check(JsonSerializer.Deserialize("{\"Items\":[{\"Value\":9}]}"u8, external) is ExternalRoot { Items.Length: 1 }, "external static context deserialization");
Check(!typeof(ExternalContext).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Any(static item => item.Key == "NativeWeave.Mcp.Layout.v1"), "external assembly has no analyzer-generated markers");

JsonTypeInfo padded = LocalContext.Default.ClassPadding;
Check(Covered(padded.Type), "class explicit size marker");
Check(padded.Type.StructLayoutAttribute?.Size == 524288, "StructLayout size survives NativeAOT");
Check(RuntimeHelpers.SizeOf(padded.Type.TypeHandle) == IntPtr.Size, "reference SizeOf is a slot, not an instance bound");
JsonTypeInfo value = LocalContext.Default.StructRoot;
Check(RuntimeHelpers.SizeOf(value.Type.TypeHandle) >= 32768, "non-nullable erased inline width");
Check(RuntimeHelpers.SizeOf(LocalContext.Default.ValueContainer.Properties[0].PropertyType.TypeHandle) >= 32768, "nullable erased inline width");
Field(value.Properties.Single(static item => item.Name == "Child").PropertyType, "_hidden", 32768);
Check(JsonSerializer.Deserialize("{\"Child\":{\"Number\":1}}"u8, value) is StructRoot { Child.Number: 1 }, "non-nullable struct metadata remains supported");
Console.WriteLine($"MCP layout preservation: {assertions} assertions passed; dynamic={RuntimeFeature.IsDynamicCodeSupported}; markers={markers.Count}.");

internal static class Application
{
    // This is an actual product API call over a precompiled external context.
    // Registration is not executed by this field-preservation-only witness.
    internal static void Register(McpRegistry registry) => registry.AddTool("external", "external context", ExternalContext.Default.ExternalRoot, ExternalContext.Default.ExternalRoot,
        static (ExternalRoot value, McpCallContext _) => Task.FromResult(McpToolResult<ExternalRoot>.Success(value)));
}

internal static class Reflection
{
    [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "The exact generated DynamicDependency annotations and coverage markers are the subject of this NativeAOT witness; this suppression itself preserves no fields.")]
    internal static FieldInfo[] Fields(Type type) => type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
}
