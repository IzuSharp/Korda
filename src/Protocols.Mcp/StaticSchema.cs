using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Collections;
using System.Reflection;
using System.Diagnostics.CodeAnalysis;

namespace NativeWeave.Server.Mcp;

// Metadata inspection and BCL schema export run only during registration. This
// is not an argument parser or a managed MCP protocol implementation.
internal static class StaticSchema
{
    private static long _scratch;
    internal static long ScratchBytes => Interlocked.Read(ref _scratch);
    internal static long LastScratchBytes { get; private set; }
    internal static byte[] Create<T>(string name, JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        if (typeInfo.Kind != JsonTypeInfoKind.Object || Nullable.GetUnderlyingType(typeInfo.Type) is not null || typeInfo.OriginatingResolver is not JsonSerializerContext context)
            throw McpValidation.Schema(name, typeInfo.Type, "provide source-generated JsonTypeInfo with an object root; nullable value, scalar, array, dictionary and open roots are unsupported");
        var walk = new MetadataWalk(name, context); walk.Visit(typeInfo, 1);
        // The admitted metadata graph bounds the exporter object/list/path graph.
        // Reserve before calling it; the checked layout/allocation witness belongs
        // to tests/Protocols.Mcp and is separate from retained schema bytes.
        long scratch = checked(65_536L + 4096L * walk.Types + 4096L * walk.Properties + 16L * (walk.Names + walk.Constants) + 1024L * walk.Attributes);
        LastScratchBytes = scratch;
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
        internal int Constants;
        internal int Attributes;
        private readonly HashSet<MemberInfo> _attributes = [];
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
                for (Type? type = info.Type; type is not null && type != typeof(object); type = type.BaseType) CheckAttributes(type);
                if (info.Kind == JsonTypeInfoKind.Object)
                {
                    foreach (var property in info.Properties)
                    {
                        if (property.Get is null && property.Set is null) continue;
                        Properties++; Names = checked(Names + McpValidation.Utf8.GetByteCount(property.Name));
                        if (Properties > 256 || Names > 16_384 || property.IsExtensionData || property.CustomConverter is not null)
                            throw McpValidation.Schema(name, info.Type, "properties, names, extension data or custom property codec exceed the static subset");
                        if (property.AttributeProvider is not null and not MemberInfo) throw McpValidation.Schema(name, info.Type, "custom attribute providers can execute unbounded exporter callbacks");
                        if (property.AttributeProvider is MemberInfo member) CheckAttributes(member);
                        if (property.AssociatedParameter is { HasDefaultValue: true, DefaultValue: string text }) AddConstant(text);
                        Visit(Resolve(property.PropertyType), depth + 1);
                    }
                }
                else if (info.Kind == JsonTypeInfoKind.Enumerable || Nullable.GetUnderlyingType(info.Type) is not null)
                {
                    if (Nullable.GetUnderlyingType(info.Type) is { } nullable) { Visit(Resolve(nullable), depth + 1); return; }
                    if (!info.Type.IsArray && !(info.Type.IsGenericType && info.Type.GetGenericTypeDefinition() == typeof(List<>))) throw McpValidation.Schema(name, info.Type, "the finite array subset requires an array or List<T>");
                    if (info.ElementType is not { } element) throw McpValidation.Schema(name, info.Type, "collection lacks static element metadata");
                    Visit(Resolve(element), depth + 1);
                }
                else if (info.Kind == JsonTypeInfoKind.Dictionary || info.Type == typeof(object) || info.Type == typeof(JsonElement) || info.Type == typeof(JsonDocument) || typeof(JsonNode).IsAssignableFrom(info.Type))
                    throw McpValidation.Schema(name, info.Type, "open/dictionary/raw JSON nodes are unsupported");
                else if (!Scalar(info.Type)) throw McpValidation.Schema(name, info.Type, "scalar codec is outside the bounded BCL subset");
                else if (info.Type.IsEnum)
                {
                    var fields = EnumFields(info.Type);
                    if (fields.Length > 256) throw McpValidation.Schema(name, info.Type, "enum fields exceed256 before schema export");
                    foreach (var field in fields) { AddConstant(field.Name); CheckAttributes(field); }
                }
            }
            finally { _path.Remove(info.Type); }
        }
        [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "Only admitted enum types with an instantiated source-generated BCL EnumConverter<T> reach this helper. That pinned converter already resolves these exact public static fields and JsonStringEnumMemberName attributes; they are required by the static enum codec itself. JIT and Native AOT enum/renamed-field fixtures verify this invariant.")]
        private static FieldInfo[] EnumFields(Type type)
        {
            if (!type.IsEnum) throw new InvalidOperationException("Enum-only metadata inspection.");
            return type.GetFields(BindingFlags.Public | BindingFlags.Static);
        }
        private void AddConstant(string value)
        { Constants = checked(Constants + McpValidation.Utf8.GetByteCount(value)); if (Constants > 65_536) throw McpValidation.Schema(name, typeof(string), "schema/attribute constant strings exceed64KiB before export"); }
        private void CheckAttributes(MemberInfo member)
        {
            if (!_attributes.Add(member)) return;
            foreach (var attribute in member.GetCustomAttributesData())
            {
                if (++Attributes > 1024 || attribute.AttributeType.Assembly != typeof(object).Assembly && attribute.AttributeType.Assembly != typeof(JsonSerializer).Assembly)
                    throw McpValidation.Schema(name, member.DeclaringType ?? member as Type ?? typeof(object), "custom attribute constructors are not bounded exporter metadata");
                foreach (var argument in attribute.ConstructorArguments) Constant(argument);
                foreach (var argument in attribute.NamedArguments) Constant(argument.TypedValue);
            }
        }
        private void Constant(CustomAttributeTypedArgument argument)
        {
            if (argument.Value is string text) AddConstant(text);
            else if (argument.Value is IReadOnlyCollection<CustomAttributeTypedArgument> array)
            { if (array.Count > 256) throw McpValidation.Schema(name, typeof(Array), "attribute array limit"); foreach (var element in array) Constant(element); }
        }
    }
    private static bool Scalar(Type type) => type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) || type == typeof(Guid) || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(DateOnly) || type == typeof(TimeOnly) || type == typeof(TimeSpan) || type == typeof(Uri) || type == typeof(Version) || type == typeof(byte[]);
    internal static void CheckOutput<T>(T value, JsonTypeInfo<T> info)
    {
        int nodes = 0, strings = 0; var context = (JsonSerializerContext)info.OriginatingResolver!;
        void Visit(object? value, JsonTypeInfo metadata, int depth)
        {
            if (++nodes > 4096 || depth > 32) throw new JsonException("Finite output graph limit.");
            if (value is null) return;
            if (value is string text) { strings = checked(strings + McpValidation.Utf8.GetByteCount(text)); if (strings > MaximumOutputStrings) throw new JsonException("Output string limit."); return; }
            if (value is byte[] binary) { strings = checked(strings + checked((binary.Length + 2) / 3 * 4)); if (strings > MaximumOutputStrings) throw new JsonException("Output base64 limit."); return; }
            if (Nullable.GetUnderlyingType(metadata.Type) is { } nullable) { Visit(value, context.GetTypeInfo(nullable)!, depth); return; }
            if (metadata.Kind == JsonTypeInfoKind.Object)
            {
                foreach (var property in metadata.Properties) if (property.Get is { } get) { strings = checked(strings + McpValidation.Utf8.GetByteCount(property.Name)); if (strings > MaximumOutputStrings) throw new JsonException("Output property limit."); Visit(get(value), context.GetTypeInfo(property.PropertyType)!, depth + 1); }
            }
            else if (metadata.Kind == JsonTypeInfoKind.Enumerable)
            {
                var items = (IList)value; if (items.Count > 4096 - nodes) throw new JsonException("Output array limit."); var element = context.GetTypeInfo(metadata.ElementType!)!;
                for (int index = 0; index < items.Count; index++) Visit(items[index], element, depth + 1);
            }
        }
        Visit(value, info, 1);
    }
    private const int MaximumOutputStrings = 262_144;
}
