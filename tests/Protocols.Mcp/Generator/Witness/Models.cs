using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

#pragma warning disable CS0169, CS0649
namespace LocalModels;

[StructLayout(LayoutKind.Sequential, Size = 32768)]
internal struct Padding { private byte _storage; }

internal sealed class Unrelated { private Padding _unreachable; }

internal partial class Container
{
    private class PrivateBase<T> { private T? _hiddenBase; }
    private sealed class PrivateDto : PrivateBase<Padding>
    {
        private Padding _hidden;
        [JsonIgnore] public Padding Ignored { get; set; }
        [JsonIgnore] public Unrelated? Unreachable { get; set; }
        public int Number { get; set; }
    }
    [JsonSerializable(typeof(PrivateDto))]
    private partial class Context : JsonSerializerContext;
    internal static JsonTypeInfo Metadata => Context.Default.GetTypeInfo(typeof(PrivateDto))!;
}

[StructLayout(LayoutKind.Sequential, Size = 524288)]
internal sealed class ClassPadding { public int Number { get; set; } }

internal struct StructRoot { private Padding _hidden; public StructChild? Child { get; set; } }
internal sealed class StructChild { private Padding _hidden; public int Number { get; set; } }
internal sealed class ValueContainer { public StructRoot? Value { get; set; } }
internal sealed class PrimaryDto(int number)
{
    public int Number => number;
    public event Action? Changed;
    public void Raise() => Changed?.Invoke();
}
internal enum ExampleKind { First, Second }
internal sealed class EnumHolder { public ExampleKind Kind { get; set; } }
internal sealed record RecordDto(string Text);

[JsonSerializable(typeof(ClassPadding))]
[JsonSerializable(typeof(StructRoot))]
[JsonSerializable(typeof(ValueContainer))]
[JsonSerializable(typeof(PrimaryDto))]
[JsonSerializable(typeof(EnumHolder))]
[JsonSerializable(typeof(RecordDto))]
internal partial class LocalContext : JsonSerializerContext;
