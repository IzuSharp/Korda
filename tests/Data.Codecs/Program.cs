using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using NativeWeave.Data;
using NativeWeave.Database.Internal;

var output = Path.GetFullPath(args.Length == 0 ? "artifacts/B07/managed" : args[0]);
Directory.CreateDirectory(output);
if (args.Contains("--require-aot", StringComparer.Ordinal) && RuntimeFeature.IsDynamicCodeSupported)
    throw new InvalidOperationException("Native AOT execution required.");
Console.WriteLine("Dynamic code supported: " + RuntimeFeature.IsDynamicCodeSupported);
void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); }
void Reject<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
var cases = new List<ScalarCase>();
void Add<T>(string name, T value, ValueCodec<T> codec)
{
    foreach (var profile in new uint[] { 1, 2 })
    {
        if (profile == 1 && codec.Id == 13 || profile == 2 && codec.Id == 14) continue;
        var command = SqlCommand.Create("SELECT @p0", new SqlParameters().Add("p0", value, codec));
        var snapshot = ParameterSnapshot.Create(command, profile, (id, config) => config is null ? id : 1024);
        var descriptor = snapshot.Expectation.Descriptors.Single();
        cases.Add(new(name + "_" + profile, profile, Convert.ToHexString(snapshot.Bytes), command.ParameterHash,
            descriptor.Codec.Id, descriptor.BoundCodecId, descriptor.Codec.Configuration, descriptor.Codec.Nullable,
            codec.Encode(value) is { } bytes ? Convert.ToHexString(bytes) : null));
    }
}
Add("i16_min", short.MinValue, Codecs.Int16); Add("i16_max", short.MaxValue, Codecs.Int16);
Add("i32_min", int.MinValue, Codecs.Int32); Add("i32_max", int.MaxValue, Codecs.Int32);
Add("i64_min", long.MinValue, Codecs.Int64); Add("i64_max", long.MaxValue, Codecs.Int64);
Add("false", false, Codecs.Boolean); Add("true", true, Codecs.Boolean);
Add("text_empty", "", Codecs.String); Add("text_unicode", "日本語 🦀 é", Codecs.String);
Add("bytes_empty", Array.Empty<byte>(), Codecs.Bytes); Add("bytes_binary", new byte[] { 0, 1, 255, 128 }, Codecs.Bytes);
Add("uuid_network", Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"), Codecs.Guid); Add("uuid_zero", Guid.Empty, Codecs.Guid);
Add("date_min", DateOnly.MinValue, Codecs.Date); Add("date_max", DateOnly.MaxValue, Codecs.Date); Add("date_epoch", new DateOnly(1970, 1, 1), Codecs.Date);
Add("time_min", TimeOnly.MinValue, Codecs.Time); Add("time_max", new TimeOnly(TimeOnly.MaxValue.Ticks - 9), Codecs.Time);
Add("instant_min", DateTimeOffset.MinValue, Codecs.UtcInstant); Add("instant_max", DateTimeOffset.MaxValue.AddTicks(-9), Codecs.UtcInstant);
Add("instant_offset", new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(8)), Codecs.UtcInstant);
Add("datetime_utc", new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), Codecs.UtcDateTime);
Add("double_min", double.MinValue, Codecs.Double); Add("double_max", double.MaxValue, Codecs.Double); Add("double_small", double.Epsilon, Codecs.Double);
Add("double_negative_zero", -0.0d, Codecs.Double); Add("double_negative", -1.25d, Codecs.Double);
Add("single_min", float.MinValue, Codecs.Single); Add("single_max", float.MaxValue, Codecs.Single); Add("single_small", float.Epsilon, Codecs.Single);
Add("single_negative_zero", -0.0f, Codecs.Single); Add("single_negative", -1.25f, Codecs.Single);
Add("numeric_max", decimal.MaxValue, Codecs.Decimal(29, 0)); Add("numeric_min", decimal.MinValue, Codecs.Decimal(29, 0));
Add("numeric_fraction", -123456.125m, Codecs.Decimal(12, 3)); Add("numeric_small", 0.0000000000000000000000000001m, Codecs.Decimal(28, 28));
Add("decimaltext_min", decimal.MinValue, Codecs.DecimalText); Add("decimaltext_max", decimal.MaxValue, Codecs.DecimalText);
Add("decimaltext_fraction", -123456.1250m, Codecs.DecimalText); Add("decimaltext_small", 0.0000000000000000000000000001m, Codecs.DecimalText);
Add("money_max", 92233720368547758.07m, Codecs.FixedScaleMoney(2)); Add("money_min", -92233720368547758.08m, Codecs.FixedScaleMoney(2));
Add("money_scale18", 0.000000000000000001m, Codecs.FixedScaleMoney(18));
Add("json_object", new Payload("Ada", 2), Codecs.Json(ProbeJsonContext.Default.Payload));
Add("json_null", (Payload)null!, Codecs.Json(ProbeJsonContext.Default.Payload));
Add("enum_min", (Tag)int.MinValue, Codecs.EnumInt32<Tag>()); Add("enum_undefined", (Tag)1234, Codecs.EnumInt32<Tag>());
Add("nullable_i16", (short?)null, Codecs.Nullable(Codecs.Int16)); Add("nullable_i32", (int?)null, Codecs.Nullable(Codecs.Int32));
Add("nullable_i64", (long?)null, Codecs.Nullable(Codecs.Int64)); Add("nullable_bool", (bool?)null, Codecs.Nullable(Codecs.Boolean));
Add("nullable_text", (string?)null, Codecs.NullableReference(Codecs.String)); Add("nullable_bytes", (byte[]?)null, Codecs.NullableReference(Codecs.Bytes));
Add("nullable_uuid", (Guid?)null, Codecs.Nullable(Codecs.Guid)); Add("nullable_date", (DateOnly?)null, Codecs.Nullable(Codecs.Date));
Add("nullable_time", (TimeOnly?)null, Codecs.Nullable(Codecs.Time)); Add("nullable_instant", (DateTimeOffset?)null, Codecs.Nullable(Codecs.UtcInstant));
Add("nullable_double", (double?)null, Codecs.Nullable(Codecs.Double)); Add("nullable_single", (float?)null, Codecs.Nullable(Codecs.Single));
Add("nullable_numeric", (decimal?)null, Codecs.Nullable(Codecs.Decimal(18, 2))); Add("nullable_decimaltext", (decimal?)null, Codecs.Nullable(Codecs.DecimalText));
Add("nullable_money", (decimal?)null, Codecs.Nullable(Codecs.FixedScaleMoney(2))); Add("nullable_json", (Payload?)null, Codecs.NullableReference(Codecs.Json(ProbeJsonContext.Default.Payload)));
Add("nullable_enum", (Tag?)null, Codecs.Nullable(Codecs.EnumInt32<Tag>()));

var registrationCalls = 0;
uint Bind(uint id, string? configuration) { registrationCalls++; return configuration is null ? id : 1024; }
var oversized = SqlCommand.Create("SELECT @p", new SqlParameters().Add("p", "too large", Codecs.String));
Reject<ArgumentException>(() => ParameterSnapshot.Create(oversized, 1, Bind, maxFieldBytes: 2));
Check(registrationCalls == 0, "Limit failure reached native binding.");
Reject<DatabaseCapabilityException>(() => ParameterSnapshot.Create(SqlCommand.Create("SELECT @p", new SqlParameters().Add("p", 1m, Codecs.Decimal(18, 2))), 1, Bind));
Check(registrationCalls == 0, "Unsupported profile reached binding.");
Reject<ArgumentException>(() => ParameterSnapshot.Create(SqlCommand.Create("SELECT @p", new SqlParameters().Add("p", "a\0b", Codecs.String)), 2, Bind));
Check(registrationCalls == 0, "Invalid PG text reached binding.");
Reject<ArgumentException>(() => new SqlParameters().Add("p", "\ud800", Codecs.String));
Reject<ArgumentException>(() => new SqlParameters().Add("p", new TimeOnly(1), Codecs.Time));
Reject<ArgumentException>(() => new SqlParameters().Add("p", DateTimeOffset.UnixEpoch.AddTicks(1), Codecs.UtcInstant));
Reject<ArgumentException>(() => new SqlParameters().Add("p", DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Unspecified), Codecs.UtcDateTime));
Reject<ArgumentException>(() => new SqlParameters().Add("p", 1.001m, Codecs.Decimal(10, 2)));
Reject<OverflowException>(() => new SqlParameters().Add("p", 1000m, Codecs.Decimal(3, 0)));
Reject<OverflowException>(() => new SqlParameters().Add("p", decimal.MaxValue, Codecs.FixedScaleMoney(2)));
Reject<ArgumentOutOfRangeException>(() => new SqlParameters().Add("p", double.NaN, Codecs.Double));
Reject<OverflowException>(() => new SqlParameters().Add("p", BigTag.TooLarge, Codecs.EnumInt32<BigTag>()));
var configured = SqlCommand.Create("SELECT @a,@b", new SqlParameters().Add("a", 1m, Codecs.Decimal(18, 2)).Add("b", 1m, Codecs.Decimal(18, 3)));
Reject<ArgumentException>(() => ParameterSnapshot.Create(configured, 2, (_, _) => 1024));
var counter = 1024u;
var distinct = ParameterSnapshot.Create(configured, 2, (_, _) => counter++);
Check(distinct.Expectation.Descriptors[0].BoundCodecId != distinct.Expectation.Descriptors[1].BoundCodecId, "Configured IDs aliased.");
Check(!typeof(ParameterSnapshot).IsPublic, "Internal adapter leaked a public root type.");

var pb = new TableBuilder<Parent>("codec_parents");
var pk1 = pb.Column("tenant", Codecs.Int32, new() { IsPrimaryKey = true });
var pk2 = pb.Column("id", Codecs.Guid, new() { IsPrimaryKey = true });
var parent = pb.Build(r => new(r.Get(pk1), r.Get(pk2)), (w, v) => { w.Set(pk1, v.Tenant); w.Set(pk2, v.Id); });
var cb = new TableBuilder<Child>("codec_children");
var fk1 = cb.Column("tenant", Codecs.Nullable(Codecs.Int32));
var fk2 = cb.Column("parent_id", Codecs.Nullable(Codecs.Guid));
var child = cb.Build(r => new(r.Get(fk1), r.Get(fk2)), (w, v) => { w.Set(fk1, v.Tenant); w.Set(fk2, v.ParentId); });
var relation = Relation<Parent, Child>.CreateOptional(parent, pk1, child, fk1).AndOptional(pk2, fk2);
var key = parent.Key(pk1, 42).And(pk2, Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"));
File.WriteAllBytes(Path.Combine(output, "parent.json"), parent.CanonicalBytes);
File.WriteAllBytes(Path.Combine(output, "child.json"), child.CanonicalBytes);
File.WriteAllBytes(Path.Combine(output, "relation.bin"), ControlRecord.Relation(relation));
foreach (var profile in new uint[] { 1, 2 })
{
    var expected = DbWire.KeyExpectation(key, profile, [2, 7]);
    var wire = DbWire.Encode(expected, 1, key.Values.Select(v => (ReadOnlyMemory<byte>?)v.Bytes).ToArray());
    File.WriteAllBytes(Path.Combine(output, "key-" + profile + ".bin"), wire);
}
File.WriteAllText(Path.Combine(output, "cases.json"), JsonSerializer.Serialize(cases.ToArray(), ProbeJsonContext.Default.ScalarCaseArray));
var resultPath = Path.Combine(output, "results.json");
if (File.Exists(resultPath))
{
    var results = JsonSerializer.Deserialize(File.ReadAllText(resultPath), ProbeJsonContext.Default.ScalarResultArray)!;
    Check(results.Length == cases.Count, "Provider results are incomplete.");
    foreach (var expected in cases)
    {
        var result = results.Single(r => r.Name == expected.Name);
        if (expected.LogicalId == 16 && expected.ExpectedHex is not null)
        {
            var a = JsonSerializer.Deserialize(Convert.FromHexString(expected.ExpectedHex), ProbeJsonContext.Default.Payload);
            var b = JsonSerializer.Deserialize(Convert.FromHexString(result.Hex!), ProbeJsonContext.Default.Payload);
            Check(a == b, "JSON semantic roundtrip: " + expected.Name);
        }
        else if (expected.Profile == 1 && expected.Name.Contains("negative_zero", StringComparison.Ordinal))
        {
            var bytes = Convert.FromHexString(result.Hex!);
            Check(expected.LogicalId == 11 ? BinaryPrimitives.ReadDoubleLittleEndian(bytes) == 0 : BinaryPrimitives.ReadSingleLittleEndian(bytes) == 0, "Signed-zero numeric semantics.");
        }
        else Check(expected.ExpectedHex == result.Hex, "C# value/wire/native/wire roundtrip: " + expected.Name);
    }
    Console.WriteLine("PASS provider result verification: " + results.Length);
}
Console.WriteLine("PASS managed codec vectors, configured IDs, pre-effect negatives, composite optional relation, internal API: " + cases.Count);

internal enum Tag : int { One = 1 }
internal enum BigTag : ulong { TooLarge = ulong.MaxValue }
internal sealed record Payload(string Name, int Count);
internal sealed record Parent(int Tenant, Guid Id);
internal sealed record Child(int? Tenant, Guid? ParentId);
internal sealed record ScalarCase(string Name, uint Profile, string Wire, string Hash, uint LogicalId, uint BoundId, string? Configuration, bool Nullable, string? ExpectedHex);
internal sealed record ScalarResult(string Name, string? Hex);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(ScalarCase[]))]
[JsonSerializable(typeof(ScalarResult[]))]
[JsonSerializable(typeof(Payload))]
internal partial class ProbeJsonContext : JsonSerializerContext;
