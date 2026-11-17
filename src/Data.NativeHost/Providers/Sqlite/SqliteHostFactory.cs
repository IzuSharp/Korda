using NativeWeave.Interop;
namespace NativeWeave.Database.Sqlite;

public static partial class SqliteDataSource
{
    internal static EngineDescriptor HostEngine { get; } = DatabaseEngineProfile.Create("nativeweave.database.sqlite", "NativeWeave.Database.Sqlite", GeneratedEngineProfile.RuntimeIdentifier, GeneratedEngineProfile.RelativePath, GeneratedEngineProfile.Sha256, typeof(SqliteDataSource).Assembly);
    /// <summary>Creates one independently owned lazy native SQLite data source.</summary>
    /// <param name="path">The nonempty database path; no connection is opened during construction.</param>
    /// <param name="configure">An optional synchronous options callback whose values are immediately snapshotted.</param>
    /// <returns>A data source whose sessions and source should be asynchronously disposed.</returns>
    /// <exception cref="ArgumentException">The path or options are invalid.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="EngineLoadException">The exact packaged module cannot be negotiated.</exception>
    public static DataSource Create(string path, Action<SqliteOptions>? configure = null) => new DataSourceBuilder().UseSqlite(path, configure).Build();
}
/// <summary>Configures the explicitly selected independently packaged SQLite engine.</summary>
public static class SqliteDataSourceBuilderExtensions
{
    /// <summary>Snapshots SQLite configuration without loading a module or opening a database.</summary>
    /// <param name="builder">The single-writer source builder.</param>
    /// <param name="path">The nonempty database path.</param>
    /// <param name="configure">Optional synchronous configuration; exceptions prevent builder mutation.</param>
    /// <returns>The supplied builder.</returns>
    /// <exception cref="ArgumentNullException">The builder or path is null.</exception>
    /// <exception cref="ArgumentException">A provider setting or path is invalid.</exception>
    public static DataSourceBuilder UseSqlite(this DataSourceBuilder builder, string path, Action<SqliteOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder); ArgumentNullException.ThrowIfNull(path);
        var options = new SqliteOptions(); configure?.Invoke(options); var record = SqliteConfiguration.Snapshot(options);
        return builder.Configure(SqliteDataSource.Engine, DatabaseProvider.Sqlite, path, record);
    }
}
