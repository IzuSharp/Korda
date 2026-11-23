using Korda.Data;

if (args.Contains("--require-aot") && System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
    throw new InvalidOperationException("Actual Native AOT mapping execution is required.");
int checks = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
var manual = new TableBuilder<Student>("students");
var id = manual.Column("id", Codecs.Int64, new() { IsPrimaryKey = true });
var name = manual.Column("Name", Codecs.String);
var email = manual.Column("email", Codecs.NullableReference(Codecs.String));
var version = manual.Column("version", Codecs.Int64, new() { IsConcurrencyToken = true });
var manualTable = manual.Build(row => new Student(row.Get(id), row.Get(name), row.Get(email), row.Get(version)), (row, item) => { row.Set(id, item.Id); row.Set(name, item.Name); row.Set(email, item.Email); row.Set(version, item.Version); });
Check(Students.Table.MetadataHash == manualTable.MetadataHash, "Generated canonical metadata differs from manual static descriptors");
Check(Students.Row.ShapeHash == manualTable.Row.ShapeHash, "Generated projection order/nullability differs");
Check(Students.Name.Name == "Name" && Students.Email.Name == "email", "Exact CLR casing and explicit overrides");
Check(Students.Id.Id == id.Id && Students.Version.Id == version.Id, "Stable model column identifiers");
var values = new object?[] { 7L, "Ada", null, 2L };
using var lease = new OracleLease<Student>(Students.Row, values);
var a = Students.Row.Read(lease); var b = manualTable.Row.Read(new OracleLease<Student>(manualTable.Row, values));
Check(a == b && a.Email is null && a.Id == 7, "Generated direct constructor parity and true SQL NULL");
var set = ClassModelMapping.Table;
using var classLease = new OracleLease<ClassModel>(set.Row, [11, "set"]);
var instance = set.Row.Read(classLease);
Check(instance.Code == 11 && instance.Label == "set", "Class property/init assignment path");
using var secondLease = new OracleLease<Second>(SecondMapping.Row, [Guid.Empty, (short?)null, DateTime.UnixEpoch]);
var second = SecondMapping.Row.Read(secondLease);
Check(second.Key == Guid.Empty && second.Optional is null && second.When.Kind == DateTimeKind.Utc, "Second entity supported codecs/nullable/UTC mapping");
checks += ExperienceChecks.Run();
Console.WriteLine($"B10 generated static DTO component: {checks} assertions; runtime dynamic code supported={System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported}. Native Host/Fluent composition is a separate acceptance row.");

[WeaveTable("students", DescriptorName = "Students")]
public sealed partial record Student([property: WeaveKey, WeaveColumn("id")] long Id, string Name, [property: WeaveColumn("email")] string? Email, [property: WeaveVersion, WeaveColumn("version")] long Version);
[WeaveTable("class_models")]
public sealed partial class ClassModel
{
    public int Code { get; init; }
    public string Label { get; set; } = "";
    [WeaveIgnore] public object? LocalOnly { get; set; }
}
[WeaveTable("second")]
public sealed partial record Second([property: WeaveKey] Guid Key, short? Optional, DateTime When);

internal sealed class OracleLease<T>(Projection<T> projection, object?[] values) : IRowLease, IDisposable
{
    private bool _disposed;
    public int Count => values.Length;
    public string Name(int ordinal) => projection.Fields[ordinal].Name;
    public CodecDescriptor Codec(int ordinal) => projection.Fields[ordinal].Descriptor;
    public ReadOnlyMemory<byte>? Value(int ordinal)
    {
        byte[]? bytes = values[ordinal] switch { null => null, long x => Codecs.Int64.Encode(x), int x => Codecs.Int32.Encode(x), short x => Codecs.Int16.Encode(x), string x => Codecs.String.Encode(x), Guid x => Codecs.Guid.Encode(x), DateTime x => Codecs.UtcDateTime.Encode(x), _ => throw new Exception("Unsupported oracle value") };
        return bytes is null ? (ReadOnlyMemory<byte>?)null : new ReadOnlyMemory<byte>(bytes);
    }
    public void AssertAlive() => ObjectDisposedException.ThrowIf(_disposed, this);
    public void Dispose() => _disposed = true;
}
