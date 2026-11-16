using NativeWeave.Interop;

namespace NativeWeave.Database.PostgreSql;

/// <summary>Chooses certificate and hostname verification or explicit development plaintext.</summary>
/// <remarks>The ordinal values are the native kind8 TLS policy. There is no Prefer, Require or certificate-validation bypass mode.</remarks>
public enum PostgreSqlTlsMode
{
    /// <summary>Requires TLS, a trusted root and a matching server hostname; the default.</summary>
    VerifyFull,
    /// <summary>Disables TLS only when the application explicitly opts into local development.</summary>
    DisableForLocalDevelopment
}

/// <summary>Configures the SQLx PostgreSQL provider before a datasource is opened.</summary>
/// <remarks>Mutable configuration is snapshotted by the owning B04 factory before native admission. Do not mutate it during configuration. It starts no runtime and opens no connection. Invalid values fail before opening. Connection strings use a PostgreSQL TCP URI with explicit user and database; query-string options and ambient PG* policy variables are rejected, so they cannot weaken this typed TLS policy. The component has no managed database fallback.</remarks>
public sealed class PostgreSqlOptions
{
    /// <summary>Creates the verified-TLS profile with application name NativeWeave and a 15-second connect deadline.</summary>
    public PostgreSqlOptions() { }
    /// <summary>Gets or sets the TLS policy; defaults to VerifyFull.</summary>
    /// <remarks>Unknown enum values reject. DisableForLocalDevelopment is an explicit insecure development selection, never an automatic fallback after certificate failure.</remarks>
    public PostgreSqlTlsMode TlsMode { get; set; } = PostgreSqlTlsMode.VerifyFull;
    /// <summary>Gets or sets the optional absolute PEM root-certificate path.</summary>
    /// <remarks>Null uses the native TLS system trust roots. The native provider validates the file before connection; hostname verification remains required. The path and certificate are configuration, not a password; no certificate contents are copied into diagnostics.</remarks>
    public string? RootCertificatePath { get; set; }
    /// <summary>Gets or sets the PostgreSQL application name; defaults to NativeWeave.</summary>
    /// <remarks>Requires 1–256 valid UTF-8 bytes without NUL. The immutable UTF-8 snapshot is copied once into the bounded native option record. PostgreSQL can truncate the displayed application_name to its server limit.</remarks>
    public string ApplicationName { get; set; } = "NativeWeave";
    /// <summary>Gets or sets a positive connect deadline of at most one hour; defaults to 15 seconds.</summary>
    /// <remarks>The serialized duration is exact unsigned nanoseconds. The host applies it to opening/admission; an expired caller wait is distinct from retiring a dispatched native connection operation. No network I/O occurs while setting it.</remarks>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(15);
}

/// <summary>Exposes the stateless PostgreSQL provider component descriptor.</summary>
/// <remarks>The current component assembly publishes Engine preflight plus exact standalone options. The downstream B04 host composes the pinned Create and UsePostgreSql factories with the actual datasource/session owners. This component does not advertise host operation exports. Reading Engine is thread-safe and performs no file access, native call, executor creation or connection opening; Inspect hashes and loads the exact maintainer-built component and starts no runtime.</remarks>
public static class PostgreSqlDataSource
{
    /// <summary>Gets the immutable descriptor for the maintainer-built PostgreSQL component.</summary>
    /// <remarks>Only postgres.component.v1 is advertised. Native adapter operations are Rust-local hooks for B04, not callable datasource exports. The native asset stays resident after Inspect; the descriptor has no disposal operation. Missing or mismatched assets are reported by Inspect using EngineLoadException.</remarks>
    public static EngineDescriptor Engine => PostgreSqlEngineProfile.Descriptor;
}
