using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Korda.Data;

var output = Path.GetFullPath(args.Length == 0 ? "artifacts/B01/managed" : args[0]);
Directory.CreateDirectory(output);
if (args.Contains("--require-aot", StringComparer.Ordinal) && RuntimeFeature.IsDynamicCodeSupported)
    throw new InvalidOperationException("Actual Native AOT execution requires dynamic code to be unavailable.");
if (args.Contains("--allocation-probe", StringComparer.Ordinal))
{
    AllocationProbe.Run(output, args.Contains("--allocation-regression", StringComparer.Ordinal));
    return;
}
if (args.Contains("--batch-allocation-probe", StringComparer.Ordinal))
{
    BatchAllocationProbe.Run(output);
    return;
}
Console.WriteLine("Dynamic code supported: " + RuntimeFeature.IsDynamicCodeSupported);
var cases = new List<string>();
void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
void Reject<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
void Test(string name, Action action) { action(); cases.Add(name); Console.WriteLine("PASS " + name); }

var parentBuilder = new TableBuilder<Parent>("Parents");
var id = parentBuilder.Column("Id", Codecs.Int64, new() { IsPrimaryKey = true });
var name = parentBuilder.Column("Name", Codecs.String, new() { MaxLength = 128 });
var token = parentBuilder.Column("Version", Codecs.Int64, new() { IsConcurrencyToken = true });
var parent = parentBuilder.Build(r => new(r.Get(id), r.Get(name), r.Get(token)), (r, v) => { r.Set(id, v.Id); r.Set(name, v.Name); r.Set(token, v.Version); });
var childBuilder = new TableBuilder<Child>("Children");
var childId = childBuilder.Column("Id", Codecs.Int64, new() { IsPrimaryKey = true });
var foreign = childBuilder.Column("ParentId", Codecs.Nullable(Codecs.Int64));
var child = childBuilder.Build(r => new(r.Get(childId), r.Get(foreign)), (r, v) => { r.Set(childId, v.Id); r.Set(foreign, v.ParentId); });
var relation = Relation<Parent, Child>.CreateOptional(parent, id, child, foreign);

Test("canonical_model_projection_and_generated_manual_identity", () =>
{
    var other = new TableBuilder<Parent>("Parents");
    var oid = other.Column("Id", Codecs.Int64, new() { IsPrimaryKey = true });
    var oname = other.Column("Name", Codecs.String, new() { MaxLength = 128 });
    var over = other.Column("Version", Codecs.Int64, new() { IsConcurrencyToken = true });
    var equivalent = other.Build(r => new(r.Get(oid), r.Get(oname), r.Get(over)), (r, v) => { r.Set(oid, v.Id); r.Set(oname, v.Name); r.Set(over, v.Version); });
    Check(parent.CanonicalBytes.AsSpan().SequenceEqual(equivalent.CanonicalBytes), "Equivalent handwritten/generated-shape mappings differ.");
    Check(parent.Row.ShapeHash == equivalent.Row.ShapeHash, "Shape includes managed delegate identity.");
    Check(parent.TryGetColumn("Name", Codecs.String, out var found) && ReferenceEquals(name, found), "Exact store name is missing.");
    Check(!parent.TryGetColumn("name", Codecs.String, out _) && !parent.TryGetColumn("Name", Codecs.Int32, out _), "Name or type silently converted.");
    Reject<ArgumentException>(() => parent.Key(oid, 1));
    Reject<InvalidOperationException>(() => parentBuilder.Column("Late", Codecs.Int32));
    Reject<InvalidOperationException>(() => parentBuilder.Build(_ => new(0, "", 0), (_, _) => { }));
});
Test("canonical_unicode_escaping_and_invalid_utf16", () =>
{
    Check(Canonical.Quote("a\n\t\0\u2028é🦀/\\\"") == "\"a\\u000a\\u0009\\u0000\u2028é🦀/\\\\\\\"\"", "Canonical Unicode escaping differs.");
    Reject<ArgumentException>(() => new TableBuilder<Parent>("\ud800"));
    Reject<ArgumentException>(() => new TableBuilder<Parent>(""));
    var b = new TableBuilder<Parent>("Unicode🦀\n"); b.Column("é\u2028", Codecs.String);
    var t = b.Build(_ => new(0, "", 0), (_, _) => { });
    File.WriteAllBytes(Path.Combine(output, "unicode-model.json"), t.CanonicalBytes);
});
Test("configured_codec_and_nullable_key_metadata", () =>
{
    var b = new TableBuilder<Parent>("p");
    Reject<ArgumentException>(() => b.Column("id", Codecs.Nullable(Codecs.Int64), new() { IsPrimaryKey = true }));
    b.Column("amount", Codecs.Decimal(18, 2));
    Reject<ArgumentException>(() => b.Column("amount", Codecs.Decimal(18, 3)));
    Reject<ArgumentOutOfRangeException>(() => b.Column("empty", Codecs.String, new() { MaxLength = 0 }));
    Reject<ArgumentOutOfRangeException>(() => Codecs.Decimal(0, 0));
    Reject<ArgumentOutOfRangeException>(() => Codecs.Decimal(10, 11));
    Reject<ArgumentOutOfRangeException>(() => Codecs.FixedScaleMoney(19));
    var t = b.Build(_ => new(0, "", 0), (_, _) => { });
    Check(t.TryGetColumn("amount", Codecs.Decimal(18, 2), out _) && !t.TryGetColumn("amount", Codecs.Decimal(18, 3), out _), "Configured codec identities aliased.");
    File.WriteAllBytes(Path.Combine(output, "configured-model.json"), t.CanonicalBytes);
});
Test("composite_key_and_relation_immutability", () =>
{
    var p = new TableBuilder<Composite>("parent"); var a = p.Column("Tenant", Codecs.Int32, new() { IsPrimaryKey = true }); var b = p.Column("Id", Codecs.Int64, new() { IsPrimaryKey = true });
    var pt = p.Build(r => new(r.Get(a), r.Get(b)), (r, v) => { r.Set(a, v.Tenant); r.Set(b, v.Id); });
    var c = new TableBuilder<Composite>("child"); var ca = c.Column("Tenant", Codecs.Int32); var cb = c.Column("Parent", Codecs.Int64);
    var ct = c.Build(r => new(r.Get(ca), r.Get(cb)), (r, v) => { r.Set(ca, v.Tenant); r.Set(cb, v.Id); });
    Reject<ArgumentException>(() => pt.Key(b, 1L));
    var prefix = pt.Key(a, 2); Reject<ArgumentException>(prefix.RequireComplete); var complete = prefix.And(b, 4L); complete.RequireComplete();
    Check(prefix.Values.Length == 1 && complete.Values.Length == 2, "Key changed in place.");
    Reject<ArgumentException>(() => complete.And(b, 4L));
    var oldHash = pt.MetadataHash; var rp = Relation<Composite, Composite>.Create(pt, a, ct, ca);
    Reject<ArgumentException>(() => rp.CanonicalBytes()); var rc = rp.And(b, cb); Check(rc.CanonicalBytes().Length > 0 && pt.MetadataHash == oldHash, "Relation mutated table metadata.");
    Reject<ArgumentException>(() => Relation<Composite, Composite>.Create(pt, b, ct, cb));
    Reject<ArgumentException>(() => Relation<Parent, Child>.Create(parent, id, child, childId).And(id, childId));
});
Test("relation_nullable_configuration_collation_negatives", () =>
{
    var p = new TableBuilder<Price>("price"); var pk = p.Column("id", Codecs.Decimal(18, 2), new() { IsPrimaryKey = true }); var pt = p.Build(_ => new(0), (_, _) => { });
    var c = new TableBuilder<Price>("cost"); var ck = c.Column("id", Codecs.Decimal(18, 3)); var ct = c.Build(_ => new(0), (_, _) => { });
    Reject<ArgumentException>(() => Relation<Price, Price>.Create(pt, pk, ct, ck));
    var rp = new TableBuilder<Named>("named"); var rk = rp.Column("id", Codecs.String, new() { IsPrimaryKey = true, Collation = "C" }); var rt = rp.Build(_ => new(""), (_, _) => { });
    var cp = new TableBuilder<Named>("other"); var fk = cp.Column("id", Codecs.NullableReference(Codecs.String), new() { Collation = "different" }); var cpt = cp.Build(_ => new(""), (_, _) => { });
    Reject<ArgumentException>(() => Relation<Named, Named>.CreateOptionalReference(rt, rk, cpt, fk));
});
Test("row_shape_before_nullable_decode_and_lease_retirement", () =>
{
    var lease = new FixtureLease(parent.Row.Fields, [Codecs.Int64.Encode(7), Codecs.String.Encode("Ada"), Codecs.Int64.Encode(1)]);
    Check(parent.Row.Read(lease) == new Parent(7, "Ada", 1), "Typed row materialization failed.");
    lease.Dispose(); Reject<ObjectDisposedException>(() => parent.Row.Read(lease));
    var invoked = false; var projection = Projection<string?>.Create([ResultField.Create("missing", Codecs.NullableReference(Codecs.String))], r => { invoked = true; return r.Read(0, Codecs.NullableReference(Codecs.String)); });
    Reject<DatabaseException>(() => projection.Read(new FixtureLease([], []))); Check(!invoked, "A missing nullable field reached the decoder.");
    Reject<DatabaseException>(() => projection.Read(new FixtureLease([ResultField.Create("wrong", Codecs.NullableReference(Codecs.String))], [null])));
    Reject<ArgumentException>(() => Projection<int>.Create([ResultField.Create("x", Codecs.Int32), ResultField.Create("x", Codecs.Int32)], _ => 0));
    var safe = Projection<int>.Create([ResultField.Create("x", Codecs.Int32)], r => { try { _ = r.Get(default(ResultColumn<int>)); } catch (ArgumentException) { return r.Read(0, Codecs.Int32); } throw new InvalidOperationException("Default token accepted."); });
    Check(safe.Read(new FixtureLease(safe.Fields, [Codecs.Int32.Encode(42)])) == 42, "Default result token did not reject.");
});
Test("row_writer_and_changes_snapshot_atomicity", () =>
{
    var values = RowSnapshot.Capture(parent, new Parent(1, "A", 2)); Check(values.Length == 3, "Writer omitted a field.");
    var changes = new Changes<Parent>().Set(name, "before").Set(token, 2L); var snap = changes.Snapshot(parent); changes.Set(name, "after").Unset(token);
    Check(Codecs.String.Decode(snap.Single(x => x.Column.Definition.Id == name.Id).Bytes) == "before", "Submitted changes changed in place.");
    Reject<ArgumentException>(() => changes.Set(id, 2)); Reject<ArgumentException>(() => new Changes<Parent>().Snapshot(parent));
    Reject<ArgumentException>(() => new ConcurrencyCheck<Parent>().Expect(name, "x"));
    var check = new ConcurrencyCheck<Parent>().Expect(token, 1L); Check(check.Snapshot(parent).Length == 1, "Expected token missing."); Reject<ArgumentException>(() => check.Expect(token, 2L));
    var b = new TableBuilder<Named>("badwriter"); var col = b.Column("value", Codecs.String); var t = b.Build(_ => new(""), (r, _) => { r.Set(col, "a"); r.Set(col, "b"); });
    Reject<ArgumentException>(() => RowSnapshot.Capture(t, new Named("x")));
    var generated = new TableBuilder<Named>("generated"); var g = generated.Column("generated", Codecs.String, new() { IsGenerated = true });
    var gt = generated.Build(_ => new(""), (r, _) => r.Set(g, null!));
    Check(RowSnapshot.Capture(gt, new Named("x")).Length == 0, "Generated value was encoded instead of omitted.");
    Reject<ArgumentException>(() => new Changes<Parent>().Set(name, new string('x', 129)));
});
Test("typed_sql_snapshot_and_interpolation_boundaries", () =>
{
    byte[] bytes = [1, 2]; var parameters = new SqlParameters().Add("bytes", bytes, Codecs.Bytes).Add("nullable", (long?)null, Codecs.Nullable(Codecs.Int64));
    bytes[0] = 9; var command = SqlCommand.Create("SELECT @bytes, @nullable", parameters); parameters.Add("later", 3, Codecs.Int32);
    Check(command.Parameters.Length == 2 && command.Parameters[0].Bytes![0] == 1 && command.Parameters[1].Bytes is null, "Command or mutable value was not snapshotted.");
    Reject<ArgumentException>(() => parameters.Add("bytes", 1, Codecs.Int32)); Reject<ArgumentException>(() => new SqlParameters().Add("@x", 1, Codecs.Int32));
    Reject<ArgumentException>(() => new SqlParameters().Add("x-y", 1, Codecs.Int32));
    var interpolated = SqlCommand.Parameterized(FormattableStringFactory.Create("SELECT {{literal}}, {0}, {0}", 42));
    Check(interpolated.Parameters.Length == 1 && interpolated.Segments!.Count(x => x.Kind == 1) == 2, "Repeated interpolation lost its value identity.");
    Reject<ArgumentException>(() => SqlCommand.Parameterized(FormattableStringFactory.Create("SELECT {0:N}", 1)));
    Reject<ArgumentException>(() => SqlCommand.Parameterized(FormattableStringFactory.Create("SELECT {0,3}", 1)));
    Reject<ArgumentException>(() => SqlCommand.Parameterized(FormattableStringFactory.Create("SELECT {0}", [null])));
    Reject<ArgumentException>(() => SqlCommand.Parameterized(FormattableStringFactory.Create("SELECT 1", 2)));
    Check(SqlCommand.Create("x", timeout: TimeSpan.Zero).Timeout == TimeSpan.Zero, "Explicit zero timeout was not preserved.");
    Check(SqlCommand.Parameterized(FormattableStringFactory.Create("SELECT {0}", 1), timeout: TimeSpan.Zero).Timeout == TimeSpan.Zero, "Interpolated zero timeout was not preserved.");
    Reject<ArgumentOutOfRangeException>(() => SqlCommand.Create("x", timeout: TimeSpan.FromTicks(-1)));
    File.WriteAllBytes(Path.Combine(output, "template.bin"), interpolated.EncodeTemplate());
});
Test("scalar_exactness_and_null_distinctions", () =>
{
    Reject<ArgumentException>(() => Codecs.UtcDateTime.Encode(new DateTime(2020, 1, 1)));
    Reject<ArgumentException>(() => Codecs.Time.Encode(new TimeOnly(1)));
    Reject<ArgumentException>(() => Codecs.UtcInstant.Encode(DateTimeOffset.UnixEpoch.AddTicks(1)));
    Reject<ArgumentOutOfRangeException>(() => Codecs.Double.Encode(double.NaN));
    Reject<ArgumentException>(() => Codecs.Decimal(18, 2).Encode(1.001m));
    Reject<OverflowException>(() => Codecs.Decimal(2, 0).Encode(100m));
    Reject<OverflowException>(() => Codecs.FixedScaleMoney(18).Encode(1000m));
    Reject<OverflowException>(() => Codecs.EnumInt32<Wide>().Encode(Wide.TooWide));
    Check(Codecs.EnumInt32<Small>().Decode(Codecs.Int32.Encode(42)) == (Small)42, "Undefined enum value was lost.");
    Check(Codecs.Guid.Encode(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"))!.AsSpan().SequenceEqual(Convert.FromHexString("00112233445566778899aabbccddeeff")), "UUID mixed endian.");
    Check(Codecs.DecimalText.Encode(1.2300m)!.AsSpan().SequenceEqual(Codecs.DecimalText.Encode(1.23m)), "Decimal bytes are not canonical.");
    Check(Codecs.NullableReference(Codecs.String).Encode(null) is null && Codecs.String.Encode("") is { Length: 0 }, "NULL and empty collapsed.");
    Check(Validation.Utf8.GetString(Codecs.Json(TestJson.Default.Payload).Encode(null!)!) == "null", "JSON null collapsed into SQL NULL.");
    Reject<DatabaseException>(() => Codecs.Boolean.Decode(new byte[] { 2 }));
});
Test("rfc8259_numeric_range_and_container_depth", () =>
{
    var json = Codecs.Json(TestJson.Default.Payload).Descriptor;
    foreach (var text in new[] { "1e400", "{\"x\":1e400}", "{\"$serde_json::private::Number\":\"ordinary-text\"}", new string('[', 64) + "0" + new string(']', 64), new string('[', 64) + new string(']', 64) })
        DbWire.ValidateScalar(json, Validation.Utf8.GetBytes(text));
    foreach (var text in new[] { new string('[', 65) + new string(']', 65), "{\"x\":01}", "[1,]", "NaN" })
        Reject<JsonException>(() => DbWire.ValidateScalar(json, Validation.Utf8.GetBytes(text)));
    Reject<ArgumentException>(() => DbWire.ValidateScalar(json, new byte[] { 0xef, 0xbb, 0xbf, (byte)'0' }));
});
Test("strict_utf8_validation_preserves_rejections", () =>
{
    foreach (var text in new[] { "", "ASCII\0", "订单 / café / 😀", "\ufeffvalue", "\ud7ff\ue000\uffff" })
        DbWire.ValidateScalar(Codecs.String.Descriptor, Validation.Utf8.GetBytes(text));
    byte[][] malformed = [[0x80], [0xc0, 0x80], [0xc2], [0xe2, 0x82], [0xed, 0xa0, 0x80], [0xf4, 0x90, 0x80, 0x80], [0xff]];
    foreach (var bytes in malformed)
    {
        Reject<System.Text.DecoderFallbackException>(() => DbWire.ValidateScalar(Codecs.String.Descriptor, bytes));
        var json = new byte[bytes.Length + 2]; json[0] = (byte)'"'; json[^1] = (byte)'"'; bytes.CopyTo(json, 1);
        Reject<System.Text.DecoderFallbackException>(() => DbWire.ValidateScalar(Codecs.Json(TestJson.Default.Payload).Descriptor, json));
    }
});
Test("decimal_decode_preserves_canonical_96_bit_contract", () =>
{
    var random = new Random(0xDB01);
    for (var scale = 0; scale <= 28; scale++)
        foreach (var negative in new[] { false, true })
            for (var sample = 0; sample < 24; sample++)
            {
                var raw = new byte[16]; random.NextBytes(raw.AsSpan(0, 12));
                if (sample == 0) raw.AsSpan(0, 12).Clear();
                if (sample == 1) raw.AsSpan(0, 12).Fill(255);
                if (sample == 2) { raw.AsSpan(0, 12).Clear(); raw[0] = 10; }
                if (sample == 3) { raw.AsSpan(0, 12).Clear(); raw[0] = 1; }
                raw[12] = (byte)scale; raw[13] = negative ? (byte)1 : (byte)0;
                var value = new decimal(BinaryPrimitives.ReadInt32LittleEndian(raw), BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(4)),
                    BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(8)), negative, (byte)scale);
                // The existing encoder remains an independent BigInteger-based oracle.
                var canonical = DecimalBytes.Encode(value);
                Check(DecimalBytes.Decode(canonical) == value, "Canonical coefficient, sign or scale changed.");
                if (raw.AsSpan().SequenceEqual(canonical)) Check(DecimalBytes.Decode(raw) == value, "Canonical input was rejected.");
                else Reject<ArgumentException>(() => DecimalBytes.Decode(raw));
            }
    foreach (var length in new[] { 0, 15, 17 }) Reject<ArgumentException>(() => DecimalBytes.Decode(new byte[length]));
    foreach (var pair in new[] { (12, 29), (12, 255), (13, 2), (14, 1), (15, 1) })
    {
        var raw = DecimalBytes.Encode(1m); raw[pair.Item1] = (byte)pair.Item2;
        Reject<ArgumentException>(() => DecimalBytes.Decode(raw));
    }
});
Test("scalar_projection_context_preserves_shape_checks", () =>
{
    Reject<ArgumentException>(() => ResultField.Create("", Codecs.Int64));
    var scalar = Projection<long>.Scalar(Codecs.Int64);
    Reject<ArgumentException>(() => Projection<long>.Create(scalar.Fields, _ => 1));
    Check(scalar.Read(new FixtureLease([ResultField.Create("provider-expression", Codecs.Int64)], [Codecs.Int64.Encode(42)])) == 42, "Scalar sentinel did not ignore provider name.");
    Reject<DatabaseException>(() => scalar.Read(new FixtureLease([], [])));
    Reject<DatabaseException>(() => scalar.Read(new FixtureLease([ResultField.Create("x", Codecs.Int32)], [Codecs.Int32.Encode(42)])));
    Reject<DatabaseException>(() => scalar.Read(new FixtureLease([ResultField.Create("x", Codecs.Nullable(Codecs.Int64))], [null])));
    File.WriteAllBytes(Path.Combine(output, "scalar-projection.json"), scalar.CanonicalBytes);
});

var codecs = new CodecDescriptor[] { Codecs.Int16.Descriptor, Codecs.Int32.Descriptor, Codecs.Int64.Descriptor, Codecs.Boolean.Descriptor, Codecs.String.Descriptor, Codecs.Bytes.Descriptor, Codecs.Guid.Descriptor, Codecs.Date.Descriptor, Codecs.Time.Descriptor, Codecs.UtcInstant.Descriptor, Codecs.Double.Descriptor, Codecs.Single.Descriptor, Codecs.Decimal(18, 2).Descriptor, Codecs.DecimalText.Descriptor, Codecs.FixedScaleMoney(2).Descriptor, Codecs.Json(TestJson.Default.Payload).Descriptor, Codecs.EnumInt32<Small>().Descriptor, Codecs.NullableReference(Codecs.String).Descriptor, Codecs.Bytes.Descriptor };
ReadOnlyMemory<byte>?[] cells = [Codecs.Int16.Encode(-7), Codecs.Int32.Encode(42), Codecs.Int64.Encode(long.MaxValue), Codecs.Boolean.Encode(true), Codecs.String.Encode("e\u0301 🦀"), Codecs.Bytes.Encode([0, 1, 255]), Codecs.Guid.Encode(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff")), Codecs.Date.Encode(new DateOnly(1970, 1, 1)), Codecs.Time.Encode(new TimeOnly(1234560)), Codecs.UtcInstant.Encode(DateTimeOffset.UnixEpoch), Codecs.Double.Encode(-0d), Codecs.Single.Encode(1.5f), Codecs.Decimal(18, 2).Encode(12.34m), Codecs.DecimalText.Encode(-1.2300m), Codecs.FixedScaleMoney(2).Encode(1234.56m), Codecs.Json(TestJson.Default.Payload).Encode(new Payload(1)), Codecs.EnumInt32<Small>().Encode((Small)42), null, Codecs.Bytes.Encode([])];
var descriptors = codecs.Select((codec, i) => new WireDescriptor((uint)i + 1, codec, codec.Configuration is null ? codec.Id : (uint)(1024 + i))).ToArray();
var expectation = new WireExpectation(PayloadKind.Parameters, 2, new byte[32], 0, 0, descriptors);
var wire = DbWire.Encode(expectation, 1, cells);
Test("db_wire_all_scalar_golden_and_malformed_inputs", () =>
{
    var parsed = DbWire.Parse(wire, expectation); Check(parsed.Cell(0, 17) is null && parsed.Cell(0, 18) is { Length: 0 }, "Wire NULL/empty collapsed.");
    void Mutation(Action<byte[]> mutate)
    {
        var copy = (byte[])wire.Clone(); mutate(copy);
        Reject<ArgumentException>(() => DbWire.Parse(copy, expectation));
        Reject<ArgumentException>(() => DbWire.ParseOwned(copy, expectation));
    }
    Mutation(b => b[4] = 2); Mutation(b => b[12] = 1); Mutation(b => b[76] = 9); Mutation(b => b[68] = 1);
    Mutation(b => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(56), uint.MaxValue));
    Mutation(b => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(88), ulong.MaxValue));
    Mutation(b => b[108] = 1); Mutation(b => b[100] = 99);
    var co = 96 + descriptors.Length * 16;
    Mutation(b => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(co + 8), 0));
    Mutation(b => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(co + 16), ulong.MaxValue));
    Mutation(b => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(co + 24 + 8), BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(co + 8))));
    Mutation(b => b[co + 2] = 1); Mutation(b => b[co + 17 * 24] = 6);
    Mutation(b => { var offset = (int)BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(co + 3 * 24 + 8)); b[offset] = 2; });
    Mutation(b => { var offset = (int)BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(co + 4 * 24 + 8)); b[offset] = 255; });
    Reject<ArgumentException>(() => DbWire.Parse(wire, expectation with { MetadataHash = Enumerable.Repeat((byte)1, 32).ToArray() }));
    Reject<ArgumentException>(() => DbWire.Parse(wire.AsSpan(0, 95), expectation));
});
Test("wire_owned_storage_is_explicit_and_default_parse_still_snapshots", () =>
{
    var input = (byte[])wire.Clone();
    var copied = DbWire.Parse(input, expectation);
    var retained = DbWire.ParseOwned(input, expectation);
    Check(!ReferenceEquals(copied.Bytes, input) && ReferenceEquals(retained.Bytes, input), "Wire storage ownership changed.");
    var first = retained.Cell(0, 0)!.Value;
    Check(first.Span.SequenceEqual(copied.Cell(0, 0)!.Value.Span), "Owned parse changed decoded bytes.");
    Array.Clear(input);
    Check(Codecs.Int16.Decode(copied.Cell(0, 0)) == -7, "The ordinary parser no longer snapshots mutable caller input.");
});
File.WriteAllBytes(Path.Combine(output, "parent.json"), parent.CanonicalBytes);
File.WriteAllBytes(Path.Combine(output, "child.json"), child.CanonicalBytes);
File.WriteAllBytes(Path.Combine(output, "projection.json"), parent.Row.CanonicalBytes);
File.WriteAllBytes(Path.Combine(output, "relation.json"), relation.CanonicalBytes());
File.WriteAllBytes(Path.Combine(output, "relation.bin"), ControlRecord.Relation(relation));
File.WriteAllBytes(Path.Combine(output, "values.bin"), wire);
var key = parent.Key(id, 123); key.RequireComplete();
var keyExpectation = DbWire.KeyExpectation(key, 1, [3]);
Reject<ArgumentException>(() => DbWire.Encode(keyExpectation, 2, [Codecs.Int64.Encode(1), Codecs.Int64.Encode(2)]));
var multirow = DbWire.Encode(keyExpectation with { ExactRows = null }, 2, [Codecs.Int64.Encode(1), Codecs.Int64.Encode(2)]);
Reject<ArgumentException>(() => DbWire.Parse(multirow, keyExpectation));
Check(DbWire.Parse(multirow, keyExpectation with { ExactRows = null }).Rows == 2, "Generic mutation kind4 lost its multiple-row domain.");
var generatedOnly = keyExpectation with { Descriptors = [] };
Check(DbWire.Parse(DbWire.Encode(generatedOnly, 1, []), generatedOnly).Rows == 1, "Generated-only insert must preserve the empty kind4 descriptor shape.");
File.WriteAllBytes(Path.Combine(output, "key.bin"), DbWire.Encode(keyExpectation, 1, [Codecs.Int64.Encode(123)]));
File.WriteAllText(Path.Combine(output, "managed-results.json"), "{\"platform\":\"linux-x64\",\"cases\":[" + string.Join(',', cases.Select(Canonical.Quote)) + "],\"status\":\"passed\"}\n");
Console.WriteLine($"PASS {cases.Count} managed cases; no native library was loaded.");

internal sealed record Parent(long Id, string Name, long Version);
internal sealed record Child(long Id, long? ParentId);
internal sealed record Composite(int Tenant, long Id);
internal sealed record Price(decimal Amount);
internal sealed record Named(string Name);
internal enum Wide : ulong { TooWide = ulong.MaxValue }
internal enum Small { Zero = 0 }
internal sealed record Payload([property: JsonPropertyName("v")] int Value);
[JsonSerializable(typeof(Payload))]
internal partial class TestJson : JsonSerializerContext { }
internal sealed class FixtureLease(ResultField[] fields, byte[]?[] values) : IRowLease, IDisposable
{
    private bool _disposed;
    public int Count => fields.Length;
    public string Name(int ordinal) => fields[ordinal].Name;
    public CodecDescriptor Codec(int ordinal) => fields[ordinal].Descriptor;
    public ReadOnlyMemory<byte>? Value(int ordinal) => values[ordinal] is { } v ? new ReadOnlyMemory<byte>(v) : (ReadOnlyMemory<byte>?)null;
    public void AssertAlive() { ObjectDisposedException.ThrowIf(_disposed, this); }
    public void Dispose() => _disposed = true;
}
