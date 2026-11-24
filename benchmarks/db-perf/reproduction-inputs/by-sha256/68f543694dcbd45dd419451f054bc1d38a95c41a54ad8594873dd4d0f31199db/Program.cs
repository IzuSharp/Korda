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
#if !POSTGRES
if (args.Length != 2) throw new ArgumentException("SQLite requires the exact independently built sqlite3 path.");
var nativePath = Path.GetFullPath(args[1]);
var native = NativeLibrary.Load(nativePath);
NativeLibrary.SetDllImportResolver(typeof(SQLitePCL.SQLite3Provider_sqlite3).Assembly,
    (name, _, _) => name == "sqlite3" ? native : nint.Zero);
SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
using (var file = File.OpenRead(nativePath)) nativeHash = Convert.ToHexStringLower(SHA256.HashData(file));
#endif
await using var connection = new ProviderConnection(args[0]);
await connection.OpenAsync();
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
    "Dapper.AOT", "1.1.0", "2.1.100", connection.ServerVersion, actual, streamRows, nativeHash), ProbeJson.Default.ProbeReceipt));

public sealed record ProbeRow(long Id, string? Note);
internal sealed record ProbeReceipt(bool NativeAot, string Provider, string ProviderVersion, string Orm,
    string OrmVersion, string DapperVersion, string DatabaseVersion, ProbeRow Actual, int StreamRows, string? SqliteNativeSha256);
[JsonSerializable(typeof(ProbeReceipt))]
internal partial class ProbeJson : JsonSerializerContext { }
