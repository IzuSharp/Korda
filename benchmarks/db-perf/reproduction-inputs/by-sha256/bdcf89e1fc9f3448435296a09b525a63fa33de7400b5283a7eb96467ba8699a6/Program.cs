using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
#if POSTGRES
using ProviderConnection = Npgsql.NpgsqlConnection;
#else
using ProviderConnection = Microsoft.Data.Sqlite.SqliteConnection;
#endif

[module: DapperAot]

if (RuntimeFeature.IsDynamicCodeSupported) throw new InvalidOperationException("Actual Native AOT execution required.");
if (args.Length < 1) throw new ArgumentException("Supply the dedicated test connection string and, for SQLite, the exact shared native library.");
string? nativeHash = null;
string? actualNativePath = null;
string[] compileOptions = [];
#if !POSTGRES
if (args.Length != 2) throw new ArgumentException("SQLite requires the exact independently built sqlite3 path.");
var nativePath = Path.GetFullPath(args[1]);
var native = NativeLibrary.Load(nativePath);
NativeLibrary.SetDllImportResolver(typeof(SQLitePCL.SQLite3Provider_sqlite3).Assembly,
    (name, _, _) => name == "sqlite3" ? native : nint.Zero);
SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
using (var file = File.OpenRead(nativePath)) nativeHash = Convert.ToHexStringLower(SHA256.HashData(file));
var nativeMaps = File.ReadLines("/proc/self/maps").Select(line => line.Split(' ', 6, StringSplitOptions.RemoveEmptyEntries))
    .Where(parts => parts.Length == 6 && Path.GetFileName(parts[5]) == "libsqlite3.so").Select(parts => parts[5]).Distinct().ToArray();
if (nativeMaps.Length != 1 || Path.GetFullPath(nativeMaps[0]) != nativePath)
    throw new InvalidOperationException("The selected SQLite library was not the sole actual mapped sqlite3 image.");
actualNativePath = nativeMaps[0];
#endif
var connectionString = args[0];
#if POSTGRES
if (connectionString.StartsWith("postgresql://", StringComparison.Ordinal))
{
    var uri = new Uri(connectionString);
    connectionString = new Npgsql.NpgsqlConnectionStringBuilder { Host = uri.Host, Port = uri.Port < 0 ? 5432 : uri.Port,
        Username = uri.UserInfo, Database = uri.AbsolutePath.Trim('/'), Pooling = false, SslMode = Npgsql.SslMode.Disable }.ConnectionString;
}
#endif
await using var connection = new ProviderConnection(connectionString);
await connection.OpenAsync();
#if !POSTGRES
compileOptions = connection.Query<string>("PRAGMA compile_options").Order(StringComparer.Ordinal).ToArray();
#endif
var rows = await connection.QueryAsync<ProbeRow>(
    "SELECT \"Id\",\"Note\" FROM orders WHERE \"TenantId\"=@tenant AND \"Id\"=@id",
    new { tenant = 1L, id = 1L, cancellation = CancellationToken.None });
var actual = rows.Single();
if (actual.Id != 1 || actual.Note != "note-1") throw new InvalidOperationException("The real ORM query returned the wrong row.");
var streamRows = 0;
await foreach (var row in connection.QueryUnbufferedAsync<ProbeRow>(
    "SELECT \"Id\",\"Note\" FROM orders WHERE \"TenantId\"=@tenant AND \"Id\"<=@maximum ORDER BY \"Id\"",
    new { tenant = 1L, maximum = 32L }))
{
    streamRows++;
    if (row.Id != streamRows || row.Note != (row.Id % 5 == 0 ? null : "note-" + row.Id % 16))
        throw new InvalidOperationException("The real ORM stream returned an incorrect or reordered row.");
}
if (streamRows != 32) throw new InvalidOperationException("The real ORM stream was incomplete.");
Console.WriteLine("DB_PERF_ORM_PROBE " + JsonSerializer.Serialize(new ProbeReceipt(true,
#if POSTGRES
    "Npgsql", "11.0.0",
#else
    "Microsoft.Data.Sqlite.Core", "11.0.0",
#endif
    "Dapper.AOT", "1.1.0", "2.1.100", connection.ServerVersion, actual, streamRows, nativeHash, actualNativePath, compileOptions), ProbeJson.Default.ProbeReceipt));

public sealed record ProbeRow(long Id, string? Note);
internal sealed record ProbeReceipt(bool NativeAot, string Provider, string ProviderVersion, string Orm,
    string OrmVersion, string DapperVersion, string DatabaseVersion, ProbeRow Actual, int StreamRows, string? SqliteNativeSha256,
    string? ActualSqliteNativePath, string[] SqliteCompileOptions);
[JsonSerializable(typeof(ProbeReceipt))]
internal partial class ProbeJson : JsonSerializerContext { }
