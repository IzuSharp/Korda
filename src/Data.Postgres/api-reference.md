# NativeWeave.Database.PostgreSql component API reference

Generated from the compiled product assembly and its XML comments by `tests/Data.Postgres/ApiCheck`. B01 data descriptors and transport only; database provider execution belongs to downstream tasks.

## `F:NativeWeave.Database.PostgreSql.PostgreSqlTlsMode.DisableForLocalDevelopment`

```csharp
NativeWeave.Database.PostgreSql.PostgreSqlTlsMode.DisableForLocalDevelopment = 1
```

**summary:** Disables TLS only when the application explicitly opts into local development.

## `F:NativeWeave.Database.PostgreSql.PostgreSqlTlsMode.VerifyFull`

```csharp
NativeWeave.Database.PostgreSql.PostgreSqlTlsMode.VerifyFull = 0
```

**summary:** Requires TLS, a trusted root and a matching server hostname; the default.

## `M:NativeWeave.Database.PostgreSql.PostgreSqlOptions.#ctor`

```csharp
public NativeWeave.Database.PostgreSql.PostgreSqlOptions.PostgreSqlOptions()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

## `M:NativeWeave.Database.PostgreSql.PostgreSqlTlsMode.#ctor`

```csharp
public NativeWeave.Database.PostgreSql.PostgreSqlTlsMode.PostgreSqlTlsMode()
```

Compiler-generated record/delegate member; inherits the documented type semantics.

## `P:NativeWeave.Database.PostgreSql.PostgreSqlDataSource.Engine`

```csharp
public static NativeWeave.Interop.EngineDescriptor NativeWeave.Database.PostgreSql.PostgreSqlDataSource.Engine { get; }
```

**summary:** Gets the immutable descriptor for the maintainer-built PostgreSQL component.

**remarks:** Only postgres.component.v1 is advertised. Native adapter operations are Rust-local hooks for B04, not callable datasource exports. The native asset stays resident after Inspect; the descriptor has no disposal operation. Missing or mismatched assets are reported by Inspect using EngineLoadException.

## `P:NativeWeave.Database.PostgreSql.PostgreSqlOptions.ApplicationName`

```csharp
public string NativeWeave.Database.PostgreSql.PostgreSqlOptions.ApplicationName { get; set; }
```

**summary:** Gets or sets the PostgreSQL application name; defaults to NativeWeave.

**remarks:** Requires 1–256 valid UTF-8 bytes without NUL. The immutable UTF-8 snapshot is copied once into the bounded native option record. PostgreSQL can truncate the displayed application_name to its server limit.

## `P:NativeWeave.Database.PostgreSql.PostgreSqlOptions.ConnectTimeout`

```csharp
public System.TimeSpan NativeWeave.Database.PostgreSql.PostgreSqlOptions.ConnectTimeout { get; set; }
```

**summary:** Gets or sets a positive connect deadline of at most one hour; defaults to 15 seconds.

**remarks:** The serialized duration is exact unsigned nanoseconds. The host applies it to opening/admission; an expired caller wait is distinct from retiring a dispatched native connection operation. No network I/O occurs while setting it.

## `P:NativeWeave.Database.PostgreSql.PostgreSqlOptions.RootCertificatePath`

```csharp
public string? NativeWeave.Database.PostgreSql.PostgreSqlOptions.RootCertificatePath { get; set; }
```

**summary:** Gets or sets the optional absolute PEM root-certificate path.

**remarks:** Null uses the native TLS system trust roots. The native provider validates the file before connection; hostname verification remains required. The path and certificate are configuration, not a password; no certificate contents are copied into diagnostics.

## `P:NativeWeave.Database.PostgreSql.PostgreSqlOptions.TlsMode`

```csharp
public NativeWeave.Database.PostgreSql.PostgreSqlTlsMode NativeWeave.Database.PostgreSql.PostgreSqlOptions.TlsMode { get; set; }
```

**summary:** Gets or sets the TLS policy; defaults to VerifyFull.

**remarks:** Unknown enum values reject. DisableForLocalDevelopment is an explicit insecure development selection, never an automatic fallback after certificate failure.

## `T:NativeWeave.Database.PostgreSql.PostgreSqlDataSource`

```csharp
public static class NativeWeave.Database.PostgreSql.PostgreSqlDataSource
```

**summary:** Exposes the stateless PostgreSQL provider component descriptor.

**remarks:** The current component assembly publishes Engine preflight plus exact standalone options. The downstream B04 host composes the pinned Create and UsePostgreSql factories with the actual datasource/session owners. This component does not advertise host operation exports. Reading Engine is thread-safe and performs no file access, native call, executor creation or connection opening; Inspect hashes and loads the exact maintainer-built component and starts no runtime.

## `T:NativeWeave.Database.PostgreSql.PostgreSqlOptions`

```csharp
public sealed class NativeWeave.Database.PostgreSql.PostgreSqlOptions
```

**summary:** Configures the SQLx PostgreSQL provider before a datasource is opened.

**remarks:** Mutable configuration is snapshotted by the owning B04 factory before native admission. Do not mutate it during configuration. It starts no runtime and opens no connection. Invalid values fail before opening. Connection strings use a PostgreSQL TCP URI with explicit user and database; query-string options and ambient PG* policy variables are rejected, so they cannot weaken this typed TLS policy. The component has no managed database fallback.

## `T:NativeWeave.Database.PostgreSql.PostgreSqlTlsMode`

```csharp
public enum NativeWeave.Database.PostgreSql.PostgreSqlTlsMode : int
```

**summary:** Chooses certificate and hostname verification or explicit development plaintext.

**remarks:** The ordinal values are the native kind8 TLS policy. There is no Prefer, Require or certificate-validation bypass mode.
