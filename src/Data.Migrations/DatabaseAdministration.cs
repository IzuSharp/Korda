using Korda.Data;
using Korda.Query.Internal;

namespace Korda.Database;

public sealed partial class DataSource
{
    private DatabaseAdministration? _administration;
    /// <summary>Gets the explicit administration facade without opening a database connection.</summary>
    /// <remarks>Each operation owns a separate, bounded administrative connection. Source creation and DI
    /// activation never apply migrations. Private in-memory SQLite cannot expose a separate administrative connection.</remarks>
    /// <exception cref="ObjectDisposedException">Source admission is closed.</exception>
    public DatabaseAdministration Administration
    {
        get { CheckOpen(); return LazyInitializer.EnsureInitialized(ref _administration, () => new DatabaseAdministration(this)); }
    }
}

/// <summary>Runs explicit trusted migration and catalog operations through the native provider.</summary>
/// <remarks>One independent administrative connection executes at a time per source, with bounded waiting.
/// Every outcome closes that connection before task retirement. It never uses a request session or request
/// transaction. Result counts, strings and nested columns are charged to source buffering and field limits.</remarks>
public sealed class DatabaseAdministration
{
    private readonly DataSource _source;
    internal DatabaseAdministration(DataSource source) => _source = source;
    /// <summary>Applies the selected forward migration range using the native provider and the retained checksum ledger.</summary>
    /// <param name="bundle">An immutable trusted bundle for this source's provider.</param>
    /// <param name="options">Target and explicit nontransactional policy; null uses transactional defaults.</param>
    /// <param name="cancellationToken">Requests cancellation while retained native work and connection closure complete.</param>
    /// <returns>A confirmed completed report; a failed migration throws.</returns>
    /// <exception cref="ArgumentException">The bundle/provider/options are invalid.</exception>
    /// <exception cref="DatabaseException">Provider, checksum, dirty phase or finite result capacity fails.</exception>
    /// <exception cref="DatabaseCapabilityException">The native provider/profile does not support the requested migration mode.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is confirmed after native cleanup.</exception>
    /// <remarks>PostgreSQL commits each transactional entry independently. SQLite commits the entire selected
    /// batch in one tracked IMMEDIATE transaction. Nontransactional PostgreSQL preserves partial effects and
    /// an applying phase; only a new explicit identical call may resume a replay-safe script. Cancellation or
    /// connection closure never proves that a previously dispatched business commit did not happen.</remarks>
    public Task<MigrationReport> MigrateAsync(MigrationBundle bundle, MigrationOptions? options = null, CancellationToken cancellationToken = default)
    {
        _source.CheckOpen(); ArgumentNullException.ThrowIfNull(bundle); options ??= new();
        return Start(0, AdministrationEncoding.Request(_source, 0, bundle, options, null), bytes => AdministrationEncoding.Report(bytes, _source.Limits), cancellationToken);
    }
    /// <summary>Reads an immutable comparison of this bundle and the successful native ledger.</summary>
    /// <param name="bundle">The exact provider-bound bundle, including previously applied entries.</param>
    /// <param name="cancellationToken">The cancellation request retained through connection cleanup.</param>
    /// <returns>Statuses in strictly ascending version order.</returns>
    /// <exception cref="ArgumentException">The bundle/provider is invalid.</exception>
    /// <exception cref="DatabaseException">History, checksum, provider or result limits fail.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is confirmed after cleanup.</exception>
    public Task<IReadOnlyList<MigrationStatus>> GetMigrationStatusAsync(MigrationBundle bundle, CancellationToken cancellationToken = default)
    {
        _source.CheckOpen(); ArgumentNullException.ThrowIfNull(bundle);
        return Start(1, AdministrationEncoding.Request(_source, 1, bundle, null, null), bytes => AdministrationEncoding.Status(bytes, _source.Limits), cancellationToken);
    }
    /// <summary>Validates checksum, missing/extra history and dirty execution phases without changing schema.</summary>
    /// <param name="bundle">The complete trusted manifest expected in the database.</param>
    /// <param name="cancellationToken">The cancellation request retained through cleanup.</param>
    /// <returns>A task completing only after validation and dedicated connection retirement.</returns>
    /// <exception cref="ArgumentException">The manifest/provider is invalid.</exception>
    /// <exception cref="DatabaseException">The ledger is incompatible, dirty, inaccessible or exceeds bounds.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is confirmed after cleanup.</exception>
    public Task ValidateMigrationsAsync(MigrationBundle bundle, CancellationToken cancellationToken = default)
    {
        _source.CheckOpen(); ArgumentNullException.ThrowIfNull(bundle);
        return Start(2, AdministrationEncoding.Request(_source, 2, bundle, null, null), bytes => { AdministrationEncoding.Validated(bytes, _source.Limits); return true; }, cancellationToken);
    }
    /// <summary>Reads bounded table and column metadata with native parameterized catalog queries.</summary>
    /// <param name="schema">A user-schema identifier; null selects PostgreSQL current_schema or SQLite main.</param>
    /// <param name="cancellationToken">The cancellation request retained through cleanup.</param>
    /// <returns>Immutable tables with columns in catalog order.</returns>
    /// <exception cref="ArgumentException">The schema scope is invalid or names a system/attached schema.</exception>
    /// <exception cref="DatabaseException">Provider or nested buffering/field limits fail.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is confirmed after cleanup.</exception>
    /// <remarks>This reads catalog metadata only. It performs no schema difference inference or automatic model creation.</remarks>
    public Task<IReadOnlyList<SchemaTable>> GetTablesAsync(string? schema = null, CancellationToken cancellationToken = default)
    {
        _source.CheckOpen();
        return Start(3, AdministrationEncoding.Request(_source, 3, null, null, schema), bytes => AdministrationEncoding.Tables(bytes, _source.Limits), cancellationToken);
    }
    private Task<T> Start<T>(uint action, byte[] payload, Func<byte[], T> read, CancellationToken token) => _source.Start(() => Create(action, payload), operation =>
    {
        using var buffer = NativeHost.TakeBuffer(_source.Binding, operation);
        return read(buffer.Copy(checked((int)Math.Min(_source.Limits.MaxBufferedBytes, DbWire.MaxEncodedBytes))));
    }, token);
    private unsafe nint Create(uint action, byte[] payload)
    {
        using var owner = _source.Owner.Borrow(DatabaseOwnerKind.Source); nint result = 0; var error = BindingChecks.Error();
        BindingChecks.Require((nint)_source.Binding.Calls.nw_db_admin_create_v1, "admin_create");
        uint status;
        fixed (byte* bytes = payload)
            status = _source.Binding.Calls.nw_db_admin_create_v1(owner.Pointer, action, BindingChecks.View(bytes, payload.Length), &result, &error);
        if (status == Korda.Interop.Abi.NativeStatus.NotSupported && error.Domain == 0x4442 && error.Code == 5)
            throw new DatabaseCapabilityException(action == 3 ? "db.schema.read.v1" : "db.migrations.v1", _source.Provider.ToString());
        HostFailure.Check(_source.Binding, status, error);
        return result;
    }
}
