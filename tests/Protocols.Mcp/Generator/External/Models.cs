using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

// This assembly deliberately references neither MCP nor its compiler analyzer.
#pragma warning disable CS0169, CS0649
namespace ExternalModels;

[StructLayout(LayoutKind.Sequential, Size = 32768)]
public struct ExternalPadding { private byte _storage; }

public class ExternalBase<T> { private T? _hiddenBase; }
public sealed class ExternalRoot : ExternalBase<ExternalPadding>
{
    private ExternalPadding _hidden;
    [JsonIgnore] public ExternalPadding Ignored { get; set; }
    public ExternalChild[] Items { get; set; } = [];
    [JsonInclude] internal IncludedChild? Included { get; set; }
    public event Action? Changed;
    public void Raise() => Changed?.Invoke();
}
public sealed class ExternalChild { private ExternalPadding _hidden; public int Value { get; set; } }
internal sealed class IncludedChild { private ExternalPadding _hidden; public int Number { get; set; } }
public sealed class UnmarkedRoot { private ExternalPadding _hidden; public int Number { get; set; } }

[JsonSerializable(typeof(ExternalRoot))]
[JsonSerializable(typeof(UnmarkedRoot))]
internal partial class ExternalContext : JsonSerializerContext;
public static class ExternalMetadata
{
    public static JsonTypeInfo<ExternalRoot> Root => ExternalContext.Default.ExternalRoot;
    public static JsonTypeInfo<UnmarkedRoot> Unmarked => ExternalContext.Default.UnmarkedRoot;
}
