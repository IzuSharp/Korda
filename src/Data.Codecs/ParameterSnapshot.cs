using NativeWeave.Data;

namespace NativeWeave.Database.Internal;

// Internal adapter only. Application-facing Codecs/ValueCodec/SqlParameters are
// defined exactly once in B01; this assembly adds no public CLR contract.
internal sealed record ParameterSnapshot(byte[] Bytes, WireExpectation Expectation)
{
    internal static ParameterSnapshot Create(SqlCommand command, uint semanticProfile,
        Func<uint, string?, uint> bindCodec, int maxParameters = 4096,
        int maxParameterBytes = 4 * 1024 * 1024, int maxFieldBytes = 4 * 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(bindCodec);
        if (semanticProfile is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(semanticProfile));
        if (maxParameters is < 1 or > DbWire.MaxDescriptors || maxParameterBytes is < 1 or > DbWire.MaxEncodedBytes
            || maxFieldBytes < 1 || maxFieldBytes > maxParameterBytes)
            throw new ArgumentOutOfRangeException(nameof(maxParameters), "Codec capacities must be finite and within the db-wire profile.");
        if (command.Parameters.Length > maxParameters) throw new ArgumentException("Parameter count exceeds the admitted limit.", nameof(command));
        // Complete local validation precedes native codec registration. No partial
        // parameter owner can become executable if a later field is invalid.
        long length = checked(WireSchema.HeaderBytes + command.Parameters.Length * (WireSchema.DescriptorBytes + WireSchema.CellBytes));
        foreach (var parameter in command.Parameters)
        {
            CodecRegistry.Validate(parameter.Codec);
            if (semanticProfile == 1 && parameter.Codec.Id == 13 || semanticProfile == 2 && parameter.Codec.Id == 14)
                throw new DatabaseCapabilityException(parameter.Codec.Name, semanticProfile == 1 ? "SQLite" : "PostgreSQL");
            if (parameter.Bytes is { } bytes)
            {
                DbWire.ValidateScalar(parameter.Codec, bytes);
                if (bytes.Length > maxFieldBytes) throw new ArgumentException("Parameter field exceeds its admitted byte limit.", nameof(command));
                if (semanticProfile == 2 && parameter.Codec.Id == 5 && bytes.Contains((byte)0))
                    throw new ArgumentException("PostgreSQL text cannot contain NUL.", nameof(command));
                length = checked(length + bytes.Length);
            }
        }
        if (length > maxParameterBytes) throw new ArgumentException("Parameter envelope exceeds its admitted byte limit.", nameof(command));
        var boundIdentities = new Dictionary<uint, CodecDescriptor>();
        var descriptors = new WireDescriptor[command.Parameters.Length];
        var cells = new ReadOnlyMemory<byte>?[command.Parameters.Length];
        for (var i = 0; i < command.Parameters.Length; i++)
        {
            var p = command.Parameters[i];
            var bound = bindCodec(p.Codec.Id, p.Codec.Configuration);
            if (p.Codec.Configuration is null ? bound != p.Codec.Id : bound < 1024)
                throw new ArgumentException("The source returned an invalid bound codec identity.", nameof(bindCodec));
            if (boundIdentities.TryGetValue(bound, out var existing) && !existing.SameScalar(p.Codec))
                throw new ArgumentException("The source aliased distinct configured codec identities.", nameof(bindCodec));
            boundIdentities[bound] = p.Codec;
            descriptors[i] = new(p.Id, p.Codec, bound);
            if (p.Bytes is { } encoded) cells[i] = new ReadOnlyMemory<byte>(encoded);
            else cells[i] = default(ReadOnlyMemory<byte>?);
        }
        var expected = new WireExpectation(PayloadKind.Parameters, semanticProfile, Convert.FromHexString(command.ParameterHash), 0, 0, descriptors) { ExactRows = 1 };
        return new(DbWire.Encode(expected, 1, cells), expected);
    }
}
