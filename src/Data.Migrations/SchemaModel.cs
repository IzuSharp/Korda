using System.Text;
using System.Text.Json;
using Korda.Data;

namespace Korda.Database;

/// <summary>An immutable versioned snapshot of explicit static table metadata for migration review.</summary>
/// <remarks>Construction and persistence perform no native load or database I/O. The snapshot contains store names,
/// codec policies and declared key/generated flags, but no inferred indexes, foreign keys, defaults or backfills.
/// Provider validation occurs when a draft is requested. Keep the previously applied snapshot in source control.</remarks>
public sealed class DatabaseModel
{
    private readonly byte[][] _models;
    private DatabaseModel(byte[][] models) => _models = models;
    /// <summary>Gets the empty schema snapshot.</summary>
    public static DatabaseModel Empty { get; } = new([]);
    /// <summary>Returns a new snapshot containing one additional immutable table.</summary>
    /// <typeparam name="TEntity">The ordinary application entity type.</typeparam>
    /// <param name="table">A generated or manually declared table.</param>
    /// <returns>A snapshot independent of this instance and future builder changes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="table"/> is null.</exception>
    /// <exception cref="ArgumentException">The exact store name repeats or the finite snapshot budget is exceeded.</exception>
    public DatabaseModel Add<TEntity>(Table<TEntity> table) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(table);
        if (_models.Length >= 4096) throw new ArgumentException("A schema snapshot exceeds its table limit.", nameof(table));
        var models = new byte[_models.Length + 1][];
        _models.CopyTo(models, 0);
        models[^1] = (byte[])table.CanonicalBytes.Clone();
        return Create(models);
    }
    /// <summary>Serializes this snapshot as version-one JSON with canonical embedded table records.</summary>
    /// <returns>Persistable Unicode JSON; this operation does not inspect the database.</returns>
    public string ToJson() => "{\"version\":1,\"models\":[" + string.Join(',', _models.Select(HostEncoding.Utf8.GetString)) + "]}";
    /// <summary>Reads a persisted version-one snapshot without provider access or reflection.</summary>
    /// <param name="json">A complete snapshot previously produced by <see cref="ToJson"/>.</param>
    /// <returns>An immutable copy of its embedded table metadata.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is null.</exception>
    /// <exception cref="ArgumentException">The JSON, version, names or finite byte/table limits are invalid.</exception>
    /// <remarks>Provider-specific type checks and complete native model validation occur before draft execution.
    /// Formatting whitespace is normalized; reordering canonical table properties is not supported.</remarks>
    public static DatabaseModel Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (HostEncoding.Utf8.GetByteCount(json) > DbWire.MaxEncodedBytes) throw new ArgumentException("Schema JSON exceeds its byte budget.", nameof(json));
        try
        {
            using var document = JsonDocument.Parse(json, new() { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2 ||
                root.GetProperty("version").GetInt32() != 1 || root.GetProperty("models").ValueKind != JsonValueKind.Array)
                throw new ArgumentException("Expected a version-one schema snapshot.", nameof(json));
            var entries = root.GetProperty("models");
            if (entries.GetArrayLength() > 4096) throw new ArgumentException("Schema snapshot table limit exceeded.", nameof(json));
            var models = new byte[entries.GetArrayLength()][]; var at = 0;
            foreach (var entry in entries.EnumerateArray())
                models[at++] = Compact(entry.GetRawText());
            return Create(models);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        { throw new ArgumentException("Invalid persisted schema snapshot.", nameof(json), error); }
    }
    private static byte[] Compact(string json)
    {
        // Preserve the canonical escape spelling inside strings: Utf8JsonWriter
        // would normalize e.g. \u0008 to \b and change the native metadata bytes.
        var result = new StringBuilder(json.Length); var quoted = false; var escaped = false;
        foreach (var ch in json)
        {
            if (quoted)
            {
                result.Append(ch);
                if (escaped) escaped = false;
                else if (ch == '\\') escaped = true;
                else if (ch == '"') quoted = false;
            }
            else if (ch == '"') { quoted = true; result.Append(ch); }
            else if (ch is not (' ' or '\t' or '\r' or '\n')) result.Append(ch);
        }
        return HostEncoding.Utf8.GetBytes(result.ToString());
    }
    private static DatabaseModel Create(byte[][] models)
    {
        long bytes = 4, jsonBytes = 25 + Math.Max(0, models.Length - 1); var names = new HashSet<(string? Schema, string Table)>();
        foreach (var model in models)
        {
            bytes = checked(bytes + 4 + model.Length);
            jsonBytes = checked(jsonBytes + model.Length);
            if (Math.Max(bytes, jsonBytes) > DbWire.MaxEncodedBytes) throw new ArgumentException("Schema snapshot exceeds its byte budget.");
            using var document = JsonDocument.Parse(model);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.GetProperty("version").GetInt32() != 1) throw new ArgumentException("Invalid table metadata version.");
            var table = root.GetProperty("table").GetString();
            var schema = root.GetProperty("schema").ValueKind == JsonValueKind.Null ? null : root.GetProperty("schema").GetString();
            ArgumentException.ThrowIfNullOrWhiteSpace(table);
            if (schema is not null) ArgumentException.ThrowIfNullOrWhiteSpace(schema);
            if (!names.Add((schema, table))) throw new ArgumentException("A schema snapshot contains duplicate exact table names.");
        }
        return new(models);
    }
    internal byte[] Encode(DatabaseLimits limits)
    {
        if (_models.Length > limits.MaxQueryNodes || _models.Sum(model => (long)model.Length + 4) + 4 > limits.MaxParameterBytes)
            throw new ArgumentException("The schema snapshot exceeds the source input limits.");
        return HostEncoding.Sequence(_models);
    }
}

/// <summary>The supported explicit changes in a native migration draft.</summary>
public enum MigrationChangeKind
{
    /// <summary>Creates a new table with explicit columns and primary key.</summary>
    CreateTable,
    /// <summary>Adds one nullable, nongenerated, nonkey column.</summary>
    AddColumn,
    /// <summary>Drops a nonkey column and requires individual developer approval.</summary>
    DropColumn,
    /// <summary>Drops a table and requires individual developer approval.</summary>
    DropTable
}

/// <summary>One detached, immutable, reviewable native schema change.</summary>
/// <remarks>This record retains bounded managed strings and no native resource. Its SQL has not executed;
/// the owning draft enforces explicit per-change destructive approval before conversion to a migration.</remarks>
/// <param name="Id">A stable digest of this exact provider-specific operation.</param>
/// <param name="Kind">The explicitly supported change kind.</param>
/// <param name="Schema">The exact schema, or null for the default schema.</param>
/// <param name="Table">The exact table name.</param>
/// <param name="Column">The exact column, or null for a table operation.</param>
/// <param name="Sql">Provider SQL for review, with identifiers safely quoted by the native dialect.</param>
/// <param name="IsDestructive">Whether converting the draft requires this exact change ID's approval.</param>
public sealed record MigrationChange(string Id, MigrationChangeKind Kind, string? Schema, string Table, string? Column, string Sql, bool IsDestructive);

/// <summary>A bounded provider-specific migration draft that has not changed the database.</summary>
/// <remarks>The draft owns read-only managed values and no native lease. Review SQL and approve every destructive
/// change ID before converting it to a trusted migration. Applying the resulting migration is a separate explicit call.</remarks>
public sealed class MigrationDraft
{
    internal MigrationDraft(DatabaseProvider provider, IReadOnlyList<MigrationChange> changes) { Provider = provider; Changes = changes; }
    /// <summary>Gets the provider that compiled this draft.</summary>
    public DatabaseProvider Provider { get; }
    /// <summary>Gets the immutable ordered list of changes; an empty list means no supported difference.</summary>
    public IReadOnlyList<MigrationChange> Changes { get; }
    /// <summary>Converts reviewed SQL to an explicit transactional migration after per-change approvals.</summary>
    /// <param name="version">A positive deployment version; bundle ordering is checked by MigrationBundle.</param>
    /// <param name="description">A nonempty diagnostic description.</param>
    /// <param name="approvedDestructiveChanges">Every destructive change ID chosen by the developer; null approves none.</param>
    /// <returns>A trusted migration for this draft's provider, ready for explicit bundle construction.</returns>
    /// <exception cref="ArgumentException">An approval is unknown or repeated, or version/description is invalid.</exception>
    /// <exception cref="InvalidOperationException">The draft is empty or a destructive change lacks explicit approval.</exception>
    /// <remarks>All supported changes are included. To omit one, revise the desired model and generate another draft.
    /// Unsupported alteration/default/backfill semantics require an independently reviewed handwritten migration.</remarks>
    public Migration ToMigration(long version, string description, IReadOnlyCollection<string>? approvedDestructiveChanges = null)
    {
        if (version <= 0) throw new ArgumentOutOfRangeException(nameof(version));
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (description.Contains('\0')) throw new ArgumentException("Descriptions cannot contain NUL.", nameof(description));
        if (Changes.Count == 0) throw new InvalidOperationException("The draft has no changes.");
        var destructive = Changes.Where(change => change.IsDestructive).Select(change => change.Id).ToHashSet(StringComparer.Ordinal);
        var approved = new HashSet<string>(StringComparer.Ordinal);
        if (approvedDestructiveChanges is not null)
            foreach (var id in approvedDestructiveChanges)
                if (id is null || !destructive.Contains(id) || !approved.Add(id)) throw new ArgumentException("An approval is unknown, repeated or not destructive.", nameof(approvedDestructiveChanges));
        if (!approved.SetEquals(destructive)) throw new InvalidOperationException("Unapproved destructive changes: " + string.Join(", ", destructive.Except(approved).Order(StringComparer.Ordinal)));
        return new(version, description, string.Join('\n', Changes.Select(change => change.Sql)));
    }
}
