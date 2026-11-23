using Korda.Data;
using Korda.Interop;

namespace Korda.Database;

/// <summary>A single-writer builder for one explicitly selected native database engine.</summary>
/// <remarks>Configuration snapshots values. Build negotiates the packaged module and creates a lazy native pool;
/// it opens no database connection. Independent builds create independent source generations.</remarks>
public sealed class DataSourceBuilder
{
    private DatabaseLimits _limits = new();
    private string _name = "default";
    private EngineDescriptor? _engine;
    private DatabaseProvider _provider;
    private byte[]? _connection, _options;
    /// <summary>Creates an unconfigured builder without loading a module.</summary>
    public DataSourceBuilder() { }
    /// <summary>Copies and validates finite resource limits for subsequent builds.</summary>
    /// <param name="limits">The immutable limit settings.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="limits"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A value exceeds the native finite profile.</exception>
    public DataSourceBuilder ConfigureLimits(DatabaseLimits limits)
    { ArgumentNullException.ThrowIfNull(limits); _limits = limits.Validate(); return this; }
    /// <summary>Sets the diagnostic source identity without including connection credentials.</summary>
    /// <param name="name">A nonempty strict-Unicode name of at most 256 UTF-8 bytes.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">The name is empty, contains NUL, or is invalid Unicode.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The name exceeds its byte limit.</exception>
    public DataSourceBuilder WithName(string name) { HostEncoding.Text(name, 256, nameof(name)); _name = name; return this; }
    /// <summary>Negotiates the selected precompiled engine and creates an independently owned lazy pool.</summary>
    /// <returns>A source to dispose asynchronously when its sessions have finished.</returns>
    /// <exception cref="InvalidOperationException">No provider was configured.</exception>
    /// <exception cref="EngineLoadException">The packaged module fails preflight.</exception>
    /// <exception cref="ArgumentException">The native provider rejects its immutable configuration.</exception>
    /// <remarks>This call starts an engine-local runtime but performs no connection or migration I/O.</remarks>
    public DataSource Build()
    {
        var engine = _engine ?? throw new InvalidOperationException("Select a provider with UseSqlite or UsePostgreSql before Build.");
        var limits = _limits.Validate();
        var config = ControlRecord.Encode(1, new(1, 1, ControlRecord.U32((uint)_provider)), new(2, 4, _connection!), new(3, 5, limits.Encode()), new(4, 5, _options!), new(5, 4, HostEncoding.Text(_name, 256, nameof(_name))));
        return DataSource.Create(engine, _provider, _name, limits, config);
    }
    internal DataSourceBuilder Configure(EngineDescriptor engine, DatabaseProvider provider, string connection, byte[] options)
    {
        ArgumentNullException.ThrowIfNull(engine); ArgumentNullException.ThrowIfNull(options);
        var copy = HostEncoding.Text(connection, 8192, nameof(connection));
        _engine = engine; _provider = provider; _connection = copy; _options = options.ToArray(); return this;
    }
}
