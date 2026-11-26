using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Korda.Database;

internal sealed class NativeDiagnostics : IDisposable
{
    private readonly nint _module, _count;
    internal string Path { get; }
    internal string Sha256 { get; }
    internal NativeDiagnostics(DatabaseProvider provider)
    {
        var name = provider == DatabaseProvider.PostgreSql ? "libnw_database_postgresql.so" : "libnw_database_sqlite.so";
        var files = File.ReadLines("/proc/self/maps").Select(line => line.Split(' ', 6, StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length == 6 && System.IO.Path.GetFileName(parts[5]) == name).Select(parts => parts[5]).Distinct().ToArray();
        if (files.Length != 1) throw new InvalidOperationException("Expected one actually loaded provider library.");
        Path = files[0];
        using (var file = File.OpenRead(Path)) Sha256 = Convert.ToHexStringLower(SHA256.HashData(file));
        _module = NativeLibrary.Load(Path); _count = NativeLibrary.GetExport(_module, "nw_db_diagnostic_count_v1");
    }
    internal unsafe NativeCounts Read()
    {
        var count = (delegate* unmanaged[Cdecl]<uint, ulong>)_count;
        return new(count(0), count(1), count(2), count(3), count(4), count(5), count(6), count(7));
    }
    public void Dispose() => NativeLibrary.Free(_module);
}

internal sealed record NativeCounts(ulong Sources, ulong Sessions, ulong Operations, ulong Cursors,
    ulong Buffers, ulong Owners, ulong SqlExecutions, ulong ProtocolSubmissions);
