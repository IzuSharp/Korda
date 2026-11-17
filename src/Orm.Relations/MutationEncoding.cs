using NativeWeave.Data;
using NativeWeave.Query.Internal;

namespace NativeWeave.Database;

internal static class MutationEncoding
{
    internal static void CheckKey<TEntity>(Table<TEntity> table, EntityKey<TEntity> key) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(table); ArgumentNullException.ThrowIfNull(key);
        if (!ReferenceEquals(table.Identity, key.Table.Identity)) throw new ArgumentException("The complete key must belong to this exact table.", nameof(key));
        key.RequireComplete();
    }
    internal static byte[] Values<TEntity>(DataSource source, Table<TEntity> table, IReadOnlyList<ColumnValue[]> rows) where TEntity : class
    {
        if (rows.Count == 0) throw new ArgumentException("An admitted mutation needs at least one row.", nameof(rows));
        var first = rows[0];
        var descriptors = first.Select(v => new WireDescriptor(v.Column.Definition.Id, v.Column.Definition.Codec, source.BindCodec(v.Column.Definition.Codec))).ToArray();
        var parameters = checked(rows.Count * descriptors.Length);
        if (parameters > source.Limits.MaxParameters || parameters > (source.Limits.MaxQueryNodes - 1) / 2)
            throw new DatabaseException("Mutation parameter/node demand exceeds the finite source limits.", DatabaseErrorKind.ResourceLimit);
        var cells = new ReadOnlyMemory<byte>?[parameters]; var index = 0;
        foreach (var row in rows)
        {
            if (row.Length != first.Length) throw new ArgumentException("Rows have different exact insert shapes.", nameof(rows));
            for (var i = 0; i < row.Length; i++)
            {
                var cell = row[i]; var column = cell.Column;
                if (!ReferenceEquals(column.Identity, table.Identity) || column.Definition.Id != descriptors[i].Id)
                    throw new ArgumentException("Mutation columns must belong to the exact ordered table shape.", nameof(rows));
                if (cell.Bytes is { } value && column.Definition.Options.MaxLength is { } maximum && value.Length > maximum)
                    throw new ArgumentException("A mutation value exceeds its column byte capacity.", nameof(rows));
                cells[index++] = cell.Bytes is null ? (ReadOnlyMemory<byte>?)null : new ReadOnlyMemory<byte>(cell.Bytes);
            }
        }
        var bytes = DbWire.Encode(new(PayloadKind.MutationValues, source.Binding.SemanticProfile, Convert.FromHexString(table.MetadataHash), table.Identity.Id, 0, descriptors), checked((uint)rows.Count), cells);
        if (bytes.Length > source.Limits.MaxParameterBytes) throw new DatabaseException("Mutation bytes exceed the finite source limit.", DatabaseErrorKind.ResourceLimit);
        return bytes;
    }
    internal static byte[] Record<TEntity>(DataSource source, Table<TEntity> table, params ControlField[] fields) where TEntity : class
    {
        var bytes = ControlRecord.Encode(4, [new(1, 1, ControlRecord.U32(table.Identity.Id)), .. fields]);
        if (bytes.Length > source.Limits.MaxParameterBytes) throw new DatabaseException("Mutation envelope exceeds the finite source limit.", DatabaseErrorKind.ResourceLimit);
        return bytes;
    }
    internal static ControlField Bytes(ushort id, byte[] value) => new(id, 5, value);
    internal static unsafe nint Create(DataSession session, DatabaseOwner model, uint kind, byte[] payload)
    {
        BindingChecks.SameSource(session.Source.Binding, model);
        using var s = session.Owner.Borrow(DatabaseOwnerKind.Session); using var m = model.Borrow(DatabaseOwnerKind.Model);
        nint operation = 0; var error = BindingChecks.Error();
        fixed (byte* input = payload) HostFailure.Check(session.Source.Binding, session.Source.Binding.Calls.nw_db_mutation_create_v1(s.Pointer, m.Pointer, kind, BindingChecks.View(input, payload.Length), &operation, &error), error);
        return operation;
    }
}
