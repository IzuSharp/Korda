using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace NativeWeave.Reference.TaskService;

internal sealed record PrincipalConfiguration(string Token, string Tenant, string Principal, bool CanWrite = true);

/// <summary>Benchmark-only configuration compatible with the accepted reference application's ordinary profiles.</summary>
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
        if (Provider is not ("sqlite" or "postgresql") || string.IsNullOrWhiteSpace(Database) || string.IsNullOrWhiteSpace(ConsumerDatabase) ||
            string.IsNullOrWhiteSpace(BlobDirectory) || !IPAddress.TryParse(ListenAddress, out _) || Port is < 0 or > 65535 ||
            MaxUploads is < 1 or > 16 || MaxBlobBytes < BlobStore.MaximumAttachment || OrphanGraceSeconds is < 60 or > 604800 ||
            RequestTimeoutSeconds is < 1 or > 3600 || (TlsCertificatePem is null) != (TlsPrivateKeyPem is null) ||
            Convert.FromBase64String(CursorKey).Length is < 32 or > 64 || Principals.Length is < 1 or > 128)
            throw new ArgumentException("Invalid comparator configuration.");
        HashSet<string> tokens = new(StringComparer.Ordinal);
        foreach (var principal in Principals)
            if (principal.Token.Length is < 16 or > 256 || principal.Token.Any(c => c < '!' || c > '~') || !tokens.Add(principal.Token) ||
                !ValidIdentity(principal.Tenant) || !ValidIdentity(principal.Principal)) throw new ArgumentException("Invalid comparator identity.");
        if (Provider == "sqlite" && Path.GetFullPath(Database) == Path.GetFullPath(ConsumerDatabase))
            throw new ArgumentException("The consumer must use an independent database.");
    }

    private static bool ValidIdentity(string value) => !string.IsNullOrWhiteSpace(value) && Canonical.Utf8.GetByteCount(value) <= 128 && !value.Any(char.IsControl);

    internal static void ConfigureSqlite()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "libsqlite3.so");
        if (!File.Exists(path)) throw new InvalidOperationException("Build and supply the pinned comparator SQLite library.");
        nint module = NativeLibrary.Load(path);
        NativeLibrary.SetDllImportResolver(typeof(SQLitePCL.SQLite3Provider_sqlite3).Assembly, (name, _, _) => name == "sqlite3" ? module : 0);
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
        SQLitePCL.raw.FreezeProvider();
        string version = SQLitePCL.raw.sqlite3_libversion().utf8_to_string();
        if (version != "3.51.3") throw new InvalidOperationException("The comparator SQLite version differs from the locked product engine.");
        Console.Error.WriteLine("B23_SQLITE version=" + version + " sha256=" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
    }

    internal static string SqliteConnectionString(string path) => new SqliteConnectionStringBuilder
    { DataSource = Path.GetFullPath(path), Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Private, ForeignKeys = true, Pooling = true, DefaultTimeout = 5 }.ToString();

    internal string PostgreSqlConnectionString()
    {
        Uri uri = new(Database);
        if (uri.Scheme is not ("postgres" or "postgresql") || uri.Query.Length != 0) throw new ArgumentException("Use an explicit PostgreSQL URI without overriding query options.");
        string[] identity = uri.UserInfo.Split(':', 2);
        return new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host, Port = uri.IsDefaultPort ? 5432 : uri.Port, Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
            Username = Uri.UnescapeDataString(identity[0]), Password = identity.Length == 2 ? Uri.UnescapeDataString(identity[1]) : "",
            SslMode = AllowLocalDatabaseTlsDisable ? SslMode.Disable : SslMode.VerifyFull,
            MaxPoolSize = 16, MinPoolSize = 0, Timeout = 5, CommandTimeout = 15, Multiplexing = false, NoResetOnClose = false
        }.ToString();
    }
}

internal sealed class Authentication
{
    private readonly (byte[] Hash, AuthIdentity Identity)[] _identities;
    internal Authentication(PrincipalConfiguration[] principals) => _identities = principals.Select(value =>
        (SHA256.HashData(Canonical.Utf8.GetBytes(value.Token)), new AuthIdentity(value.Tenant, value.Principal, value.CanWrite))).ToArray();

    internal AuthIdentity Authenticate(HttpContext context)
    {
        if (context.Request.Headers.Authorization.Count != 1) throw new ApplicationProblem(401, "unauthenticated");
        string value = context.Request.Headers.Authorization[0]!;
        if (!value.StartsWith("Bearer ", StringComparison.Ordinal) || value.Length > 263) throw new ApplicationProblem(401, "unauthenticated");
        byte[] hash = SHA256.HashData(Canonical.Utf8.GetBytes(value[7..]));
        AuthIdentity? found = null;
        foreach (var entry in _identities) if (CryptographicOperations.FixedTimeEquals(hash, entry.Hash)) found = entry.Identity;
        return found ?? throw new ApplicationProblem(401, "unauthenticated");
    }
}

internal sealed record OperationResult(string Scope, string RequestKey, string RequestHash, string OperationId, string Tenant,
    string Principal, string Semantic, long ResponseVersion, long ResponseStatus, byte[] ResponseBytes, long CompletedAt);
internal sealed record Attachment(string Tenant, string ItemId, string ContentHash, long ContentLength, string OperationId, long Version);
internal sealed record OutboxEntry(string EventId, string OperationId, byte[] Payload, long Attempts);
