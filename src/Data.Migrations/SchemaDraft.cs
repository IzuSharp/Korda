using Korda.Data;

namespace Korda.Database;

public sealed partial class DatabaseAdministration
{
    /// <summary>Compiles an explicit snapshot difference to a reviewable native SQL draft without opening a connection.</summary>
    /// <param name="previous">The versioned previously applied model snapshot; use Empty for a new database.</param>
    /// <param name="current">The desired explicitly declared model snapshot.</param>
    /// <param name="cancellationToken">Requests cancellation while bounded native compilation retires.</param>
    /// <returns>A detached draft; no SQL is executed and no migration history is read.</returns>
    /// <exception cref="ArgumentNullException">A model is null.</exception>
    /// <exception cref="ArgumentException">A model or difference is unsupported, ambiguous or exceeds source limits.</exception>
    /// <exception cref="DatabaseCapabilityException">The loaded native module lacks schema draft support.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is confirmed after native retirement.</exception>
    /// <remarks>Supports table creation, nullable column addition and explicitly approved drops. Type, key,
    /// nullability and other existing-column alterations reject with the offending table/column; use reviewed
    /// explicit SQL for backfills, conversions, defaults and provider-specific rebuilds. Source creation never calls this method.</remarks>
    public Task<MigrationDraft> DraftMigrationAsync(DatabaseModel previous, DatabaseModel current, CancellationToken cancellationToken = default)
    {
        _source.CheckOpen(); ArgumentNullException.ThrowIfNull(previous); ArgumentNullException.ThrowIfNull(current);
        _source.Capabilities.Require("db.schema.draft.v1");
        var payload = ControlRecord.Encode(5,
            new(1, 1, ControlRecord.U32((uint)_source.Provider)),
            new(7, 5, previous.Encode(_source.Limits)), new(8, 5, current.Encode(_source.Limits)));
        if (payload.LongLength > _source.Limits.MaxParameterBytes) throw new ArgumentException("Schema difference input exceeds the source byte limit.");
        return Start(4, payload, bytes => AdministrationEncoding.Draft(bytes, _source.Provider, _source.Limits), cancellationToken);
    }
}

internal static partial class AdministrationEncoding
{
    internal static MigrationDraft Draft(byte[] bytes, DatabaseProvider provider, DatabaseLimits limits)
    {
        var envelope = Result(bytes, 4, limits); long rows = 0;
        if (envelope.TryGetValue(6, out var rejected)) throw new ArgumentException(Text(rejected, limits));
        var changes = new List<MigrationChange>(); var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in Sequence(envelope[2], limits, ref rows))
        {
            var fields = Record(record, 21, (1, 4, true), (2, 1, true), (3, 4, false), (4, 4, true), (5, 4, false), (6, 4, true), (7, 1, true));
            var id = Text(fields[1], limits); var kind = (MigrationChangeKind)U32(fields[2]);
            var destructive = Boolean(fields[7]);
            if (id.Length != 64 || !id.All(char.IsAsciiHexDigitLower) || !ids.Add(id) || !Enum.IsDefined(kind) ||
                destructive != (kind is MigrationChangeKind.DropColumn or MigrationChangeKind.DropTable) ||
                fields.ContainsKey(5) != (kind is MigrationChangeKind.AddColumn or MigrationChangeKind.DropColumn)) throw Invalid();
            changes.Add(new(id, kind, fields.TryGetValue(3, out var schema) ? Text(schema, limits) : null,
                Text(fields[4], limits), fields.TryGetValue(5, out var column) ? Text(column, limits) : null,
                Text(fields[6], limits), destructive));
        }
        return new(provider, Array.AsReadOnly(changes.ToArray()));
    }
}
