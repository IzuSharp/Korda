using Korda.Data;

internal static class ExperienceChecks
{
    internal static int Run()
    {
        int count = 0;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); count++; }
        void Reject<T>(Action action) where T : Exception
        {
            try { action(); }
            catch (T) { count++; return; }
            throw new InvalidOperationException("Expected " + typeof(T).Name);
        }
        Check(ConventionOrderMapping.Table.Name == nameof(ConventionOrder), "Convention table name changed casing or pluralized.");
        Check(ConventionOrderMapping.Table.Key(ConventionOrderMapping.Id, 7).Values.Length == 1, "Convention key was not generated.");
        Reject<ArgumentException>(() => LegacyKeylessMapping.Table.Key(LegacyKeylessMapping.Id, 7));
        Check(ExperienceOrderMapping.Id.Codec.Id == Codecs.Int64.Id, "Custom ID introduced a different native scalar identity.");
        Check(ExperienceOrderMapping.Total.Codec.Descriptor == ExperiencePolicies.Money.Descriptor, "Shared money policy was not used.");
        Check(ExperienceOrderMapping.Discount.Codec.Descriptor == Codecs.Nullable(ExperiencePolicies.Money).Descriptor, "Nullable policy was not wrapped.");
        Check(ExperienceOrderMapping.Tax.Codec.Descriptor == ExperiencePolicies.Numeric.Descriptor, "Property codec override was ignored.");
        Check(ExperienceOrderMapping.Details.Codec.Id == 16, "Static JSON policy was not used.");
        var details = new ExperienceDetails("owned JSON");
        Check(ExperienceOrderMapping.Details.Codec.Decode(ExperienceOrderMapping.Details.Codec.Encode(details)) == details, "Static JSON conversion did not round trip.");
        var fields = ExperienceRows.Row.Fields;
        Check(fields.Select(x => x.Name).SequenceEqual(["order_id", "customer_name", "Total"]), "Projection alias/constructor order differs.");
        using var lease = new EncodedLease(fields, [Codecs.Int64.Encode(17), null, ExperiencePolicies.Money.Encode(12.50m)]);
        var row = ExperienceRows.Row.Read(lease);
        Check(row.Id == new ExperienceOrderId(17) && row.Customer is null && row.Total == 12.50m, "Explicit constructor parameter or nullable DTO mapping differs.");
        var wrong = fields.ToArray(); wrong[0] = ResultField.Create("id", ExperiencePolicies.OrderId);
        using var wrongLease = new EncodedLease(wrong, [Codecs.Int64.Encode(17), null, ExperiencePolicies.Money.Encode(12.50m)]);
        Reject<DatabaseException>(() => ExperienceRows.Row.Read(wrongLease));
        var text = Codecs.Convert<TextId, string>(Codecs.String, static value => value.Value, static value => new(value));
        var encoded = text.EncodeBounded(new("é"), 2);
        Check(text.Decode(encoded) == new TextId("é"), "Converted text codec changed UTF-8 representation.");
        Reject<DatabaseException>(() => text.EncodeBounded(new("é"), 1));
        var binary = Codecs.Convert<BinaryId, byte[]>(Codecs.Bytes, static value => value.Value, static value => new(value));
        byte[] input = [1, 2, 3];
        var bytes = binary.EncodeBounded(new(input), 3)!;
        input[0] = 9;
        Check(bytes[0] == 1 && binary.Decode(bytes).Value[0] == 1, "Converted binary codec retained a mutable input buffer.");
        Reject<DatabaseException>(() => binary.EncodeBounded(new(input), 2));
        Reject<ArgumentException>(() => Codecs.Convert<long?, long?>(Codecs.Nullable(Codecs.Int64), static value => value, static value => value));
        Check(Codecs.Nullable(ExperiencePolicies.OrderId).Encode(null) is null, "Nullable custom ID did not preserve SQL NULL.");
        Reject<ArgumentException>(() => ExperiencePolicies.Money.Encode(1.001m));
        Console.WriteLine($"DB-API-01 generated mappings: {count} convention, custom ID, money, JSON, nullable DTO and bounded encoding assertions.");
        return count;
    }

    private readonly record struct TextId(string Value);
    private readonly record struct BinaryId(byte[] Value);
    private sealed class EncodedLease(ResultField[] fields, byte[]?[] values) : IRowLease, IDisposable
    {
        private bool _disposed;
        public int Count => fields.Length;
        public string Name(int ordinal) => fields[ordinal].Name;
        public CodecDescriptor Codec(int ordinal) => fields[ordinal].Descriptor;
        public ReadOnlyMemory<byte>? Value(int ordinal) => values[ordinal] is { } value ? new ReadOnlyMemory<byte>(value) : (ReadOnlyMemory<byte>?)null;
        public void AssertAlive() => ObjectDisposedException.ThrowIf(_disposed, this);
        public void Dispose() => _disposed = true;
    }
}
