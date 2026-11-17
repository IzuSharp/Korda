using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace NativeWeave.Server.Mcp;

// Metadata inspection and BCL schema export run only during registration. This
// is not an argument parser or a managed MCP protocol implementation.
internal static class StaticSchema
{
    private static long _scratch;
    internal static long ScratchBytes => Interlocked.Read(ref _scratch);
    internal static byte[] Create<T>(string name, JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        if (typeInfo.Kind != JsonTypeInfoKind.Object || Nullable.GetUnderlyingType(typeInfo.Type) is not null || typeInfo.OriginatingResolver is not JsonSerializerContext context)
            throw McpValidation.Schema(name, typeInfo.Type, "provide source-generated JsonTypeInfo with an object root; nullable value, scalar, array, dictionary and open roots are unsupported");
        var walk = new MetadataWalk(name, context); walk.Visit(typeInfo, 1);
        // The admitted metadata graph bounds the exporter object/list/path graph.
        // Reserve before calling it; the checked layout/allocation witness belongs
        // to tests/Protocols.Mcp and is separate from retained schema bytes.
        long scratch = checked(65_536L + 4096L * walk.Types + 4096L * walk.Properties + 16L * walk.Names);
        if (Interlocked.Add(ref _scratch, scratch) > 4 * 1024 * 1024)
        { Interlocked.Add(ref _scratch, -scratch); throw new ArgumentException("NWAPI-S004: concurrent startup schema scratch exceeds 4 MiB."); }
        try
        {
            typeInfo.MakeReadOnly();
            var schema = typeInfo.GetJsonSchemaAsNode(new JsonSchemaExporterOptions
            {
                TransformSchemaNode = (context, node) =>
                {
                    if (context.Path.Length == 0)
                    {
                        if (node is not JsonObject obj || !ObjectOnly(obj["type"])) throw McpValidation.Schema(name, typeInfo.Type, "exported root is not object or object/null");
                        obj["type"] = "object";
                    }
                    return node;
                }
            });
            using var buffer = new BoundedJsonBuffer(65_536);
            using (var writer = new Utf8JsonWriter(buffer)) schema.WriteTo(writer);
            return buffer.WrittenMemory.ToArray();
        }
        finally { Interlocked.Add(ref _scratch, -scratch); }
    }
    private static bool ObjectOnly(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var type) && type == "object"
        || node is JsonArray array && array.Count == 2 && array.Any(v => v?.GetValue<string>() == "object") && array.Any(v => v?.GetValue<string>() == "null");
    private sealed class MetadataWalk(string name, JsonSerializerContext context)
    {
        private readonly HashSet<Type> _seen = [];
        private readonly HashSet<Type> _path = [];
        internal int Types => _seen.Count;
        internal int Properties;
        internal int Names;
        private JsonTypeInfo Resolve(Type type) => context.GetTypeInfo(type) ?? throw McpValidation.Schema(name, type, "nested metadata is absent from the explicit static context");
        internal void Visit(JsonTypeInfo info, int depth)
        {
            if (depth > 16 || _path.Contains(info.Type)) throw McpValidation.Schema(name, info.Type, "recursive references or metadata depth beyond16");
            if (!_seen.Add(info.Type)) return;
            if (_seen.Count > 64) throw McpValidation.Schema(name, info.Type, "more than64 distinct static types");
            if (info.PolymorphismOptions is not null || info.Converter.GetType().Assembly != typeof(JsonSerializer).Assembly)
                throw McpValidation.Schema(name, info.Type, "polymorphism or a custom converter is unsupported");
            _path.Add(info.Type);
            try
            {
                if (info.Kind == JsonTypeInfoKind.Object)
                {
                    foreach (var property in info.Properties)
                    {
                        if (property.Get is null && property.Set is null) continue;
                        Properties++; Names = checked(Names + McpValidation.Utf8.GetByteCount(property.Name));
                        if (Properties > 256 || Names > 16_384 || property.IsExtensionData || property.CustomConverter is not null)
                            throw McpValidation.Schema(name, info.Type, "properties, names, extension data or custom property codec exceed the static subset");
                        if (property.AssociatedParameter is { HasDefaultValue: true, DefaultValue: string text } && McpValidation.Utf8.GetByteCount(text) > 16_384)
                            throw McpValidation.Schema(name, info.Type, "constructor default exceeds the finite schema string limit");
                        Visit(Resolve(property.PropertyType), depth + 1);
                    }
                }
                else if (info.Kind == JsonTypeInfoKind.Enumerable || Nullable.GetUnderlyingType(info.Type) is not null)
                {
                    if (info.ElementType is not { } element) throw McpValidation.Schema(name, info.Type, "collection lacks static element metadata");
                    Visit(Resolve(element), depth + 1);
                }
                else if (info.Kind == JsonTypeInfoKind.Dictionary || info.Type == typeof(object) || info.Type == typeof(JsonElement) || info.Type == typeof(JsonDocument) || typeof(JsonNode).IsAssignableFrom(info.Type))
                    throw McpValidation.Schema(name, info.Type, "open/dictionary/raw JSON nodes are unsupported");
            }
            finally { _path.Remove(info.Type); }
        }
    }
}
