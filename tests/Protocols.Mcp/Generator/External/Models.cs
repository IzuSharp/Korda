using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

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
}
public sealed class ExternalChild { private ExternalPadding _hidden; public int Value { get; set; } }

[JsonSerializable(typeof(ExternalRoot))]
public partial class ExternalContext : JsonSerializerContext;
