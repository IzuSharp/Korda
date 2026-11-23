using Korda.Data;
using Korda.Interop.Abi;
using Korda.Query.Internal;

namespace Korda.Database;

internal static class SqlSubmission
{
    internal static void Validate(SqlCommand command, uint resultKind)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (resultKind == 0 && command.Kind == SqlCommandKind.Query || resultKind != 0 && command.Kind != SqlCommandKind.Query)
            throw new ArgumentException("The command kind does not match the selected result operation.", nameof(command));
    }
    internal static unsafe DatabaseOwner Create(DataSession session, SqlCommand command, uint resultKind, uint projection)
    {
        var source = session.Source;
        var definitions = new byte[command.Parameters.Length][]; var descriptors = new WireDescriptor[command.Parameters.Length];
        var cells = new ReadOnlyMemory<byte>?[command.Parameters.Length];
        for (var i = 0; i < command.Parameters.Length; i++)
        {
            var parameter = command.Parameters[i]; var bound = source.BindCodec(parameter.Codec);
            definitions[i] = ControlRecord.Encode(21, new(1, 1, ControlRecord.U32(parameter.Id)), new(2, 4, HostEncoding.Utf8.GetBytes(parameter.Name)), new(3, 1, ControlRecord.U32(bound)), new(4, 1, ControlRecord.U32(parameter.Codec.Nullable ? 1u : 0u)));
            descriptors[i] = new(parameter.Id, parameter.Codec, bound); cells[i] = parameter.Bytes is null ? (ReadOnlyMemory<byte>?)null : new ReadOnlyMemory<byte>(parameter.Bytes);
        }
        var parameters = DbWire.Encode(new(PayloadKind.Parameters, source.Binding.SemanticProfile, Convert.FromHexString(command.ParameterHash), 0, 0, descriptors) { ExactRows = 1 }, 1, cells);
        if (parameters.Length > source.Limits.MaxParameterBytes || descriptors.Length > source.Limits.MaxParameters) throw new DatabaseException("Parameter demand exceeds the source budget.", DatabaseErrorKind.ResourceLimit);
        var options = ControlRecord.Encode(20, new(1, 1, ControlRecord.U32((uint)command.Kind)), new(2, 1, ControlRecord.U32(resultKind)), HostEncoding.Duration(3, command.Timeout ?? source.Limits.CommandTimeout), new(4, 1, ControlRecord.U32(projection)), new(5, 5, HostEncoding.Sequence(definitions)), new(6, 1, ControlRecord.U32(command.Segments is null ? 0u : 1u)));
        var template = command.EncodeTemplate(); using var lease = session.Owner.Borrow(DatabaseOwnerKind.Session);
        nint result = 0; var error = BindingChecks.Error(); fixed (byte* sql = template, values = parameters, config = options)
            HostFailure.Check(source.Binding, source.Binding.Calls.nw_db_sql_create_v1(lease.Pointer, BindingChecks.View(sql, template.Length), BindingChecks.View(values, parameters.Length), BindingChecks.View(config, options.Length), &result, &error), error);
        return new(source.Binding, DatabaseOwnerKind.Plan, result);
    }
}
