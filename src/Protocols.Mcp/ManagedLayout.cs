using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace NativeWeave.Server.Mcp;

// This is startup storage accounting over the explicit static DTO closure. It
// does not instantiate DTOs, run constructors, or provide a reflection codec.
internal sealed class ManagedLayout(string name, JsonSerializerContext context)
{
    internal const string Annotation = "x-nativeweave-managed-peak-v1";
    private const string ProofType = "NativeWeave.Server.Mcp.Generated.LayoutPreservation";
    private readonly Dictionary<Type, long> _classes = [];
    private readonly Dictionary<Type, long> _boxing = [];
    private readonly Dictionary<Assembly, Proof?> _proofs = [];
    private long _maximumStorage;
    private long _maximumValue;
    internal int Fields { get; private set; }
    internal int ReflectionFields { get; private set; }
    internal int LayoutTypes => _classes.Count;
    internal long NodeBytes => Math.Max(4096, checked(4 * _maximumStorage));

    internal void Observe(JsonTypeInfo info)
    {
        if (info.Type.IsValueType)
        {
            // Public .NET11 managed sizeof includes every hidden/ignored field,
            // nullable payload and explicit value-type layout/padding in AOT too.
            long value = RuntimeHelpers.SizeOf(info.Type.TypeHandle);
            _maximumValue = Math.Max(_maximumValue, value);
            _maximumStorage = Math.Max(_maximumStorage, checked(value + 32));
        }
        else if (info.Kind == JsonTypeInfoKind.Object)
            _maximumStorage = Math.Max(_maximumStorage, ClassStorage(info.Type));
    }

    internal long ReplyBytes(JsonTypeInfo root) => checked(2 * 1024 * 1024L + BoxingPath(root) + 2 * _maximumValue + 128);

    internal void AdmitEnumFields(Type type)
    {
        var proof = FieldProof(type);
        if (proof.Reflection > 4096 - ReflectionFields)
            throw McpValidation.Schema(name, type, "enum reflection-cache metadata exceeds its finite prewalk bound");
        ReflectionFields += proof.Reflection;
    }

    private long BoxingPath(JsonTypeInfo info)
    {
        if (_boxing.TryGetValue(info.Type, out long cached)) return cached;
        long own = info.Type.IsValueType ? checked(RuntimeHelpers.SizeOf(info.Type.TypeHandle) + 32L) : 0;
        long child = 0;
        if (Nullable.GetUnderlyingType(info.Type) is { } nullable) child = BoxingPath(Resolve(nullable));
        else if (info.Kind == JsonTypeInfoKind.Object)
        {
            foreach (var property in info.Properties)
                if (property.Get is not null) child = Math.Max(child, BoxingPath(Resolve(property.PropertyType)));
        }
        else if (info.Kind == JsonTypeInfoKind.Enumerable) child = BoxingPath(Resolve(info.ElementType!));
        return _boxing[info.Type] = checked(own + child);
    }

    private JsonTypeInfo Resolve(Type type) => context.GetTypeInfo(type)
        ?? throw McpValidation.Schema(name, type, "nested layout metadata is absent from the explicit static context");

    private long ClassStorage(Type type)
    {
        if (_classes.TryGetValue(type, out long cached)) return cached;
        if (_classes.Count >= 64) throw McpValidation.Schema(name, type, "class/base layout metadata exceeds64 types");
        var proof = FieldProof(type);
        if (proof.Declared > 1024 - Fields || proof.Reflection > 4096 - ReflectionFields)
            throw McpValidation.Schema(name, type, "complete layout/reflection-cache metadata exceeds its finite prewalk bound");
        Fields += proof.Declared; ReflectionFields += proof.Reflection;
        _classes.Add(type, 0);
        long bytes = type.BaseType is { } parent && parent != typeof(object) ? ClassStorage(parent) : 128;
        var fields = InstanceFields(type);
        if (fields.Length > proof.Declared) throw McpValidation.Schema(name, type, "compiled instance field count exceeds its preserved static proof");
        long explicitEnd = 0;
        foreach (var field in fields)
        {
            long width = field.FieldType.IsPointer || field.FieldType.IsFunctionPointer ? IntPtr.Size : RuntimeHelpers.SizeOf(field.FieldType.TypeHandle);
            // Each field plus at most one field-width alignment gap is bounded
            // by twice its exact inline size. References are pointer-sized; the
            // reachable serializer-created objects are charged per JSON node.
            bytes = checked(bytes + 2 * width);
            if (field.GetCustomAttribute<FieldOffsetAttribute>() is { } offset)
            {
                if (offset.Value < 0) throw McpValidation.Schema(name, type, "negative instance field offset");
                explicitEnd = Math.Max(explicitEnd, checked(offset.Value + width));
            }
        }
        // Explicit layouts may place a field far beyond offset zero even with
        // Size=0; declared Size can add padding without a corresponding field.
        long declared = type.StructLayoutAttribute?.Size ?? 0;
        bytes = checked(bytes + 2 * Math.Max(declared, explicitEnd));
        return _classes[type] = bytes;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "FieldProof verifies the MCP compiler's per-type preservation/count proof before this call. Its reachable ModuleInitializer DynamicDependency roots every public/nonpublic instance field for this exact DTO/base definition. Missing proof fails registration; actual Native AOT private/base/ignored/explicit-layout witnesses exercise the retained metadata.")]
    private static FieldInfo[] InstanceFields(Type type) => type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

    private (int Declared, int Reflection) FieldProof(Type type)
    {
        var proof = Find(context.GetType().Assembly, type);
        if (proof.Declared < 0) proof = Find(type.Assembly, type);
        if (proof.Declared < 0 && Assembly.GetEntryAssembly() is { } entry) proof = Find(entry, type);
        if (proof.Declared >= 0 && proof.Reflection >= proof.Declared) return proof;
        throw McpValidation.Schema(name, type, "complete CLR instance layout is not preserved; include the NativeWeave MCP compiler analyzer in the static-context project or register concrete tools in the entry project");
    }

    private (int Declared, int Reflection) Find(Assembly assembly, Type type)
    {
        if (!_proofs.TryGetValue(assembly, out var proof))
        {
            proof = Lookup(assembly); _proofs.Add(assembly, proof);
        }
        return proof is null ? (-1, -1) : (proof.Declared(type), proof.Reflection(type));
    }

    private sealed record Proof(Func<Type, int> Declared, Func<Type, int> Reflection);

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Only the one fixed compiler-generated helper type is queried; its reachable ModuleInitializer explicitly preserves that type and its exact lookup method. No assembly/type enumeration or runtime codec discovery occurs.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "The MCP generator roots this exact helper method and signature through DynamicDependency; an absent helper fails closed before any instance-field array is requested.")]
    private static Proof? Lookup(Assembly assembly)
    {
        var type = assembly.GetType(ProofType, false, false);
        if (type is null) return null;
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var declared = type.GetMethod("DeclaredInstanceFieldCount", flags)?.CreateDelegate<Func<Type, int>>();
        var reflection = type.GetMethod("ReflectionFieldCount", flags)?.CreateDelegate<Func<Type, int>>();
        return declared is null || reflection is null ? null : new(declared, reflection);
    }
}
