using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NativeWeave.Server;

/// <summary>Creates independently owned applications using the required Rust host.</summary>
public static class NativeServer
{
    /// <summary>Creates a builder with finite loopback defaults; explicit arguments populate configuration.</summary>
    public static NativeServerBuilder CreateBuilder(string[]? args = null) => new(args);
}

/// <summary>Configures one application before Build; configuration is single-threaded and copied at Build.</summary>
public sealed class NativeServerBuilder
{
    private readonly List<ListenerConfiguration> _listeners = [];
    private readonly List<JsonSerializerContext> _json = [];
    private readonly ServiceDescriptor[] _defaultServices;
    private IServiceProvider? _external;
    private bool _disposeExternal;
    private bool _built;
    private IServerAuthenticator? _authenticator;
    private IServerAuthorizer? _authorizer;

    internal NativeServerBuilder(string[]? args)
    {
        Configuration = new ConfigurationManager();
        if (args is not null) Configuration.AddCommandLine(args);
        Services = new ServiceCollection();
        Services.AddLogging();
        _defaultServices = Services.ToArray();
        Logging = new LoggingConfiguration(Services);
    }

    /// <summary>Gets mutable configuration copied and validated by Build.</summary>
    public ServerOptions Options { get; } = new();
    /// <summary>Gets service registrations; the owned provider is built once.</summary>
    public IServiceCollection Services { get; }
    /// <summary>Gets explicit configuration; no file watching, environment or secret scanning is installed.</summary>
    public ConfigurationManager Configuration { get; }
    /// <summary>Gets logging configuration; no sink is installed by default.</summary>
    public ILoggingBuilder Logging { get; }

    /// <summary>Applies supported scalar keys immediately using invariant checked parsing.</summary>
    public NativeServerBuilder Configure(IConfiguration section)
    {
        Mutable(); ArgumentNullException.ThrowIfNull(section);
        foreach (var pair in section.GetChildren())
        {
            if (pair.Key == nameof(ServerOptions.Budget))
            {
                foreach (var budget in pair.GetChildren()) BindBudget(budget.Key, Scalar(budget));
                continue;
            }
            string value = Scalar(pair);
            switch (pair.Key)
            {
                case nameof(ServerOptions.RequestTimeout): Options.RequestTimeout = ParseTime(value); break;
                case nameof(ServerOptions.ShutdownTimeout): Options.ShutdownTimeout = ParseTime(value); break;
                case nameof(ServerOptions.MaxRequestBodyBytes): Options.MaxRequestBodyBytes = ParseLong(value); break;
                case nameof(ServerOptions.MaxJsonBytes): Options.MaxJsonBytes = ParseInt(value); break;
                case nameof(ServerOptions.MaxHeaderBytes): Options.MaxHeaderBytes = ParseInt(value); break;
                case nameof(ServerOptions.HeaderReadTimeout): Options.HeaderReadTimeout = ParseTime(value); break;
                case nameof(ServerOptions.KeepAliveTimeout): Options.KeepAliveTimeout = ParseTime(value); break;
                default: throw new ArgumentException($"Unknown server configuration key '{pair.Key}'.", nameof(section));
            }
        }
        return this;
    }

    /// <summary>Registers an explicit native listener; the first explicit registration replaces the loopback default.</summary>
    public NativeServerBuilder Listen(IPAddress address, int port, Action<ServerListenOptions>? configure = null)
    {
        Mutable(); ArgumentNullException.ThrowIfNull(address);
        if (port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        var options = new ServerListenOptions(); configure?.Invoke(options);
        _listeners.Add(new(address, port, options)); return this;
    }

    /// <summary>Registers both IPv4 and IPv6 loopback listeners; startup must bind both.</summary>
    public NativeServerBuilder ListenLocalhost(int port = 5000, Action<ServerListenOptions>? configure = null)
    {
        Listen(IPAddress.Loopback, port, configure);
        return Listen(IPAddress.IPv6Loopback, port, configure);
    }

    /// <summary>Registers explicit static JSON metadata without runtime serializer generation.</summary>
    public NativeServerBuilder UseJson(JsonSerializerContext context)
    {
        Mutable(); ArgumentNullException.ThrowIfNull(context);
        if (!_json.Contains(context)) _json.Add(context);
        return this;
    }

    /// <summary>Uses an external scope-capable service provider; disposal transfers only when explicitly selected.</summary>
    public NativeServerBuilder UseServiceProvider(IServiceProvider provider, bool disposeWithServer = false)
    {
        Mutable(); ArgumentNullException.ThrowIfNull(provider);
        if (provider.GetService(typeof(IServiceScopeFactory)) is not IServiceScopeFactory)
            throw new ArgumentException("A request IServiceScopeFactory is required.", nameof(provider));
        _external = provider; _disposeExternal = disposeWithServer; return this;
    }

    /// <summary>Registers a caller-owned, thread-safe application authenticator.</summary>
    public NativeServerBuilder UseAuthentication(IServerAuthenticator authenticator)
    { Mutable(); ArgumentNullException.ThrowIfNull(authenticator); _authenticator = authenticator; return this; }
    /// <summary>Registers a caller-owned, thread-safe named-policy authorizer.</summary>
    public NativeServerBuilder UseAuthorization(IServerAuthorizer authorizer)
    { Mutable(); ArgumentNullException.ThrowIfNull(authorizer); _authorizer = authorizer; return this; }

    /// <summary>Builds once, validates and snapshots configuration, and loads the native module without starting listeners.</summary>
    public NativeServerApplication Build()
    {
        Mutable();
        var options = ServerConfiguration.Snapshot(Options);
        var listeners = _listeners.Count == 0
            ? new List<ListenerConfiguration> { new(IPAddress.Loopback, 5000, new()) }
            : _listeners;
        var names = new HashSet<string>(StringComparer.Ordinal);
        var frozen = listeners.Select((entry, index) => entry.Freeze(index, names)).ToArray();
        if (_external is not null && !Services.SequenceEqual(_defaultServices))
            throw new InvalidOperationException("Service registrations cannot be combined with an external provider.");
        var configuration = new ServerConfiguration(options, frozen, _json.ToArray(), _authenticator, _authorizer);
        var host = NativeHostFactory.Create(configuration);
        IServiceProvider provider;
        try { provider = _external ?? Services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true }); }
        catch { host.ReleaseUnstarted(); throw; }
        _built = true;
        return new(configuration, host, provider, _external is null || _disposeExternal);
    }

    private void Mutable() { if (_built) throw new InvalidOperationException("This builder has already built its application."); }
    private static string Scalar(IConfigurationSection value) => value.Value is { } text && !value.GetChildren().Any()
        ? text : throw new ArgumentException($"'{value.Key}' requires a scalar value.");
    private static int ParseInt(string value) => int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
    private static long ParseLong(string value) => long.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
    private static TimeSpan ParseTime(string value) => TimeSpan.ParseExact(value, "c", CultureInfo.InvariantCulture);
    private void BindBudget(string key, string value)
    {
        switch (key)
        {
            case nameof(ServerBudgetOptions.MaxConnections): Options.Budget.MaxConnections = ParseInt(value); break;
            case nameof(ServerBudgetOptions.MaxHttp2StreamsPerConnection): Options.Budget.MaxHttp2StreamsPerConnection = ParseInt(value); break;
            case nameof(ServerBudgetOptions.MaxActiveRequests): Options.Budget.MaxActiveRequests = ParseInt(value); break;
            case nameof(ServerBudgetOptions.MaxQueuedRequests): Options.Budget.MaxQueuedRequests = ParseInt(value); break;
            case nameof(ServerBudgetOptions.ManagedPayloadBytes): Options.Budget.ManagedPayloadBytes = ParseLong(value); break;
            case nameof(ServerBudgetOptions.NativePayloadBytes): Options.Budget.NativePayloadBytes = ParseLong(value); break;
            case nameof(ServerBudgetOptions.RetainedBytesPerRequest): Options.Budget.RetainedBytesPerRequest = ParseLong(value); break;
            case nameof(ServerBudgetOptions.MaxChunkBytes): Options.Budget.MaxChunkBytes = ParseInt(value); break;
            default: throw new ArgumentException($"Unknown server budget key '{key}'.", nameof(key));
        }
    }
    private sealed class LoggingConfiguration(IServiceCollection services) : ILoggingBuilder { public IServiceCollection Services { get; } = services; }
}

internal sealed record ListenerConfiguration(IPAddress Address, int Port, ServerListenOptions Options)
{
    internal ListenerSnapshot Freeze(int index, HashSet<string> names)
    {
        string name = Options.Name ?? $"listener-{index}";
        ServerValidation.Text(name, 128, nameof(Options.Name));
        if (!names.Add(name)) throw new InvalidOperationException("Duplicate listener name.");
        if (Options.Protocols is < ServerProtocols.Http1 or > ServerProtocols.Http1AndHttp2)
            throw new ArgumentOutOfRangeException(nameof(Options.Protocols));
        if (Options.Protocols == ServerProtocols.Http1AndHttp2 && Options.Tls is null)
            throw new ArgumentException("Combined HTTP/1 and HTTP/2 requires TLS ALPN.");
        return new(Address, Port, name, Options.Protocols, Options.Tls is null ? null : TlsSnapshot.Create(Options.Tls));
    }
}
internal sealed record ListenerSnapshot(IPAddress Address, int Port, string Name, ServerProtocols Protocols, TlsSnapshot? Tls);
internal sealed record TlsSnapshot(string CertificateChainFile, string PrivateKeyFile, string? ClientCertificateAuthorityFile, TimeSpan HandshakeTimeout, int MaxConcurrentHandshakes)
{
    internal static TlsSnapshot Create(ServerTlsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ServerValidation.Duration(options.HandshakeTimeout, false, nameof(options.HandshakeTimeout));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxConcurrentHandshakes);
        foreach (var path in new[] { options.CertificateChainFile, options.PrivateKeyFile, options.ClientCertificateAuthorityFile })
            if (path is not null && (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.Directory) != 0))
                throw new ArgumentException("TLS configuration requires readable regular files.", nameof(options));
        return new(options.CertificateChainFile, options.PrivateKeyFile, options.ClientCertificateAuthorityFile, options.HandshakeTimeout, options.MaxConcurrentHandshakes);
    }
}

internal sealed record ServerConfiguration(ServerOptions Options, ListenerSnapshot[] Listeners, JsonSerializerContext[] JsonContexts, IServerAuthenticator? Authenticator, IServerAuthorizer? Authorizer)
{
    internal JsonTypeInfo<T> Resolve<T>(JsonTypeInfo<T>? explicitInfo = null)
    {
        if (explicitInfo is not null) return explicitInfo;
        JsonTypeInfo<T>? selected = null;
        foreach (var context in JsonContexts)
            if (context.GetTypeInfo(typeof(T)) is JsonTypeInfo<T> info)
            {
                if (selected is not null && !ReferenceEquals(selected, info))
                    throw new InvalidOperationException($"Conflicting static JSON metadata for {typeof(T)}.");
                selected = info;
            }
        return selected ?? throw new InvalidOperationException($"NWAPI-S001: register UseJson or supply JsonTypeInfo for {typeof(T)}.");
    }

    internal static ServerOptions Snapshot(ServerOptions source)
    {
        ArgumentNullException.ThrowIfNull(source.Budget);
        ServerValidation.Duration(source.RequestTimeout, false, nameof(source.RequestTimeout));
        ServerValidation.Duration(source.ShutdownTimeout, true, nameof(source.ShutdownTimeout));
        ServerValidation.Duration(source.HeaderReadTimeout, false, nameof(source.HeaderReadTimeout));
        ServerValidation.Duration(source.KeepAliveTimeout, false, nameof(source.KeepAliveTimeout));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(source.MaxRequestBodyBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(source.MaxJsonBytes);
        if (source.MaxJsonBytes > source.MaxRequestBodyBytes || source.MaxHeaderBytes is < 1024 or > 1048576)
            throw new ArgumentException("HTTP body/JSON/header limits are inconsistent.", nameof(source));
        var b = source.Budget;
        if (b.MaxConnections <= 0 || b.MaxHttp2StreamsPerConnection <= 0 || b.MaxActiveRequests <= 0 || b.MaxQueuedRequests < 0 ||
            b.ManagedPayloadBytes <= 0 || b.NativePayloadBytes <= 0 || b.RetainedBytesPerRequest <= 0 || b.MaxChunkBytes <= 0 ||
            b.MaxChunkBytes > b.RetainedBytesPerRequest || b.MaxChunkBytes > b.ManagedPayloadBytes || b.MaxChunkBytes > b.NativePayloadBytes ||
            checked(3L * source.MaxJsonBytes + b.MaxChunkBytes) > Math.Min(b.ManagedPayloadBytes, b.RetainedBytesPerRequest))
            throw new ArgumentException("Finite server budgets cannot admit the configured payload peak.", nameof(source));
        TrustedProxyOptions? proxies = null;
        if (source.TrustedProxies is { } input)
        {
            if (input.ForwardLimit is < 1 or > 16 || input.KnownProxies.Count > 256) throw new ArgumentOutOfRangeException(nameof(source.TrustedProxies));
            proxies = new() { ForwardLimit = input.ForwardLimit };
            foreach (var ip in input.KnownProxies)
            {
                ArgumentNullException.ThrowIfNull(ip);
                var normalized = ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
                if (proxies.KnownProxies.Contains(normalized)) throw new ArgumentException("Duplicate trusted peer.", nameof(source));
                proxies.KnownProxies.Add(normalized);
            }
        }
        return new()
        {
            RequestTimeout = source.RequestTimeout, ShutdownTimeout = source.ShutdownTimeout, MaxRequestBodyBytes = source.MaxRequestBodyBytes,
            MaxJsonBytes = source.MaxJsonBytes, MaxHeaderBytes = source.MaxHeaderBytes, HeaderReadTimeout = source.HeaderReadTimeout,
            KeepAliveTimeout = source.KeepAliveTimeout, TrustedProxies = proxies,
            Budget = new() { MaxConnections = b.MaxConnections, MaxHttp2StreamsPerConnection = b.MaxHttp2StreamsPerConnection,
                MaxActiveRequests = b.MaxActiveRequests, MaxQueuedRequests = b.MaxQueuedRequests, ManagedPayloadBytes = b.ManagedPayloadBytes,
                NativePayloadBytes = b.NativePayloadBytes, RetainedBytesPerRequest = b.RetainedBytesPerRequest, MaxChunkBytes = b.MaxChunkBytes }
        };
    }
}

internal static class ServerValidation
{
    internal static void Duration(TimeSpan duration, bool zero, string name)
    { if (duration < TimeSpan.Zero || (!zero && duration == TimeSpan.Zero) || duration > TimeSpan.FromDays(1)) throw new ArgumentOutOfRangeException(name); }
    internal static void Text(string value, int maximumUtf8, string name)
    { ArgumentException.ThrowIfNullOrWhiteSpace(value, name); if (Encoding.UTF8.GetByteCount(value) > maximumUtf8 || value.Any(char.IsControl)) throw new ArgumentException("Invalid or oversized text.", name); }
    internal static void Status(int value) { if (value is < 200 or > 599) throw new ArgumentOutOfRangeException(nameof(value)); }
    internal static void ContentStatus(int value) { Status(value); if (value is 204 or 205 or 304) throw new ArgumentException("This status cannot carry content.", nameof(value)); }
}
