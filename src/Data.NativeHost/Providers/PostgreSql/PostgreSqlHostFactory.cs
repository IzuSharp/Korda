using NativeWeave.Interop;
namespace NativeWeave.Database.PostgreSql;

public static partial class PostgreSqlDataSource
{
    internal static EngineDescriptor HostEngine { get; } = DatabaseEngineProfile.Create("nativeweave.database.postgresql", "NativeWeave.Database.PostgreSql", "linux-x64", "runtimes/linux-x64/native/libnativeweave_database_postgres_host.so", PostgreSqlAssetIdentity.Sha256, typeof(PostgreSqlDataSource).Assembly);
    /// <summary>Creates one independently owned lazy SQLx PostgreSQL data source.</summary>
    /// <param name="connectionString">An explicit TCP PostgreSQL URI; ambient connection policy is rejected.</param>
    /// <param name="configure">An optional synchronous options callback, snapshotted before native admission.</param>
    /// <returns>A data source to dispose after its sessions finish.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="connectionString"/> is null.</exception>
    /// <exception cref="ArgumentException">The connection or typed security policy is invalid.</exception>
    /// <exception cref="EngineLoadException">The exact packaged module fails negotiation.</exception>
    public static DataSource Create(string connectionString, Action<PostgreSqlOptions>? configure = null) => new DataSourceBuilder().UsePostgreSql(connectionString, configure).Build();
}
/// <summary>Configures the independently packaged SQLx PostgreSQL engine.</summary>
public static class PostgreSqlDataSourceBuilderExtensions
{
    /// <summary>Snapshots explicit connection and TLS policy without opening a connection.</summary>
    /// <param name="builder">The single-writer builder.</param>
    /// <param name="connectionString">A nonempty explicit PostgreSQL TCP URI.</param>
    /// <param name="configure">Optional synchronous configuration; failure leaves the builder unchanged.</param>
    /// <returns>The supplied builder.</returns>
    /// <exception cref="ArgumentNullException">The builder or connection is null.</exception>
    /// <exception cref="ArgumentException">A setting is invalid.</exception>
    public static DataSourceBuilder UsePostgreSql(this DataSourceBuilder builder, string connectionString, Action<PostgreSqlOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder); ArgumentNullException.ThrowIfNull(connectionString);
        var options = new PostgreSqlOptions(); configure?.Invoke(options); var record = PostgreSqlConfiguration.Snapshot(options).Record.ToArray();
        return builder.Configure(PostgreSqlDataSource.Engine, DatabaseProvider.PostgreSql, connectionString, record);
    }
}
