using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using NativeWeave.Database;
#if !REFERENCE_SQLITE_ONLY
using NativeWeave.Database.PostgreSql;
#endif
using NativeWeave.Database.Sqlite;
using NativeWeave.Server;

namespace NativeWeave.Reference.TaskService;

internal sealed record PrincipalConfiguration(string Token, string Tenant, string Principal, bool CanWrite = true);

/// <summary>Explicit finite application settings, loaded once from a protected local JSON file.</summary>
internal sealed class ServiceConfiguration
{
    public required string Provider { get; init; }
    public required string Database { get; init; }
    public required string BlobDirectory { get; init; }
    public required string ConsumerDatabase { get; init; }
    public required string CursorKey { get; init; }
    public required PrincipalConfiguration[] Principals { get; init; }
    public string ListenAddress { get; init; } = "127.0.0.1";
    public int Port { get; init; }
    public string? TlsCertificatePem { get; init; }
    public string? TlsPrivateKeyPem { get; init; }
    public bool AllowLocalDatabaseTlsDisable { get; init; }
    public int MaxUploads { get; init; } = 4;
    public long MaxBlobBytes { get; init; } = 256L * 1024 * 1024;
    public int OrphanGraceSeconds { get; init; } = 3600;
    public int RequestTimeoutSeconds { get; init; } = 120;
    public bool RunWorkers { get; init; } = true;

    internal void Validate()
    {
#if REFERENCE_SQLITE_ONLY
        if (Provider != "sqlite") throw new ArgumentException("The minimum sample includes only the SQLite provider package.");
#endif
        if (Provider is not ("sqlite" or "postgresql") || string.IsNullOrWhiteSpace(Database) || string.IsNullOrWhiteSpace(ConsumerDatabase) ||
            string.IsNullOrWhiteSpace(BlobDirectory) || !IPAddress.TryParse(ListenAddress, out _) || Port is < 0 or > 65535 ||
            MaxUploads is < 1 or > 16 || MaxBlobBytes < BlobStore.MaximumAttachment || MaxBlobBytes > 1024L * 1024 * 1024 * 1024 ||
            OrphanGraceSeconds is < 60 or > 604800 || RequestTimeoutSeconds is < 1 or > 3600 ||
            (TlsCertificatePem is null) != (TlsPrivateKeyPem is null) || Convert.FromBase64String(CursorKey).Length is < 32 or > 64 ||
            Principals.Length is < 1 or > 128)
            throw new ArgumentException("Invalid reference service configuration.");
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (var identity in Principals)
        {
            if (identity.Token.Length is < 16 or > 256 || !tokens.Add(identity.Token) || identity.Token.Any(c => c < '!' || c > '~') ||
                !ValidIdentity(identity.Tenant) || !ValidIdentity(identity.Principal)) throw new ArgumentException("Invalid authenticated identity configuration.");
        }
        if (Provider == "sqlite" && Path.GetFullPath(Database) == Path.GetFullPath(ConsumerDatabase))
            throw new ArgumentException("The outbox consumer must use a separate persistent database.");
    }

    private static bool ValidIdentity(string value) => !string.IsNullOrWhiteSpace(value) && Canonical.Utf8.GetByteCount(value) <= 128 && !value.Any(char.IsControl);

    internal DataSource CreateSource()
    {
        DataSourceBuilder builder = new();
        if (Provider == "sqlite") builder.UseSqlite(Database, options => { options.BeginMode = SqliteBeginMode.Immediate; options.BusyTimeout = TimeSpan.FromSeconds(5); });
#if !REFERENCE_SQLITE_ONLY
        else builder.UsePostgreSql(Database, options => { if (AllowLocalDatabaseTlsDisable) options.TlsMode = PostgreSqlTlsMode.DisableForLocalDevelopment; });
#else
        else throw new ArgumentException("The minimum sample includes only the SQLite provider package.");
#endif
        return builder.ConfigureLimits(new DatabaseLimits
        {
            MaxConnections = 16,
            MaxWaiters = 64,
            AdmissionTimeout = TimeSpan.FromSeconds(5),
            CommandTimeout = TimeSpan.FromSeconds(15),
            MaxBufferedRows = 128,
            MaxBufferedBytes = 1024 * 1024,
            MaxBatchRows = 100,
            MaxBatchBytes = 512 * 1024,
            MaxFieldBytes = 1024 * 1024
        }).Build();
    }

    internal DataSource CreateConsumer() => new DataSourceBuilder()
        .UseSqlite(ConsumerDatabase, options => { options.BeginMode = SqliteBeginMode.Immediate; options.BusyTimeout = TimeSpan.FromSeconds(5); })
        .ConfigureLimits(new() { MaxConnections = 2, MaxWaiters = 4, MaxBufferedRows = 32, MaxBufferedBytes = 128 * 1024 }).Build();
}

internal sealed class Authentication : IServerAuthenticator, IServerAuthorizer
{
    private readonly (byte[] TokenHash, AuthIdentity Identity)[] _identities;
    internal Authentication(PrincipalConfiguration[] identities) => _identities = identities.Select(value =>
        (SHA256.HashData(Canonical.Utf8.GetBytes(value.Token)), new AuthIdentity(value.Tenant, value.Principal, value.CanWrite))).ToArray();

    public Task<ClaimsPrincipal?> AuthenticateAsync(RequestContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? value = context.Request.Headers.GetSingle("Authorization");
        if (value is null || !value.StartsWith("Bearer ", StringComparison.Ordinal) || value.Length > 263) return Task.FromResult<ClaimsPrincipal?>(null);
        byte[] hash = SHA256.HashData(Canonical.Utf8.GetBytes(value[7..]));
        AuthIdentity? match = null;
        foreach (var identity in _identities)
            if (CryptographicOperations.FixedTimeEquals(hash, identity.TokenHash)) match = identity.Identity;
        if (match is null) return Task.FromResult<ClaimsPrincipal?>(null);
        ClaimsIdentity claims = new([new("tenant", match.Tenant), new(ClaimTypes.NameIdentifier, match.Principal), new("write", match.CanWrite ? "yes" : "no")], "reference-token");
        return Task.FromResult<ClaimsPrincipal?>(new(claims));
    }

    public Task<bool> AuthorizeAsync(RequestContext context, string policy, CancellationToken cancellationToken = default) =>
        Task.FromResult(context.User.Identity?.IsAuthenticated == true && (policy == "read" || policy == "write" && context.User.FindFirst("write")?.Value == "yes"));

    internal static AuthIdentity Identity(RequestContext context) => new(
        context.User.FindFirst("tenant")?.Value ?? throw new ApplicationProblem(401, "unauthenticated"),
        context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? throw new ApplicationProblem(401, "unauthenticated"),
        context.User.FindFirst("write")?.Value == "yes");
}
