#if SQLITE
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using SQLitePCL;

namespace DbPerf.IndependentOrm;

internal sealed record SqliteImageIdentity(string Path, string Sha256, string Version,
    string SourceId, string[] CompileOptions);

internal static class SqliteImage
{
    private static readonly object Gate = new();
    private static SqliteImageIdentity? s_identity;
    private static nint s_module;
    private static string? s_explicitPath;
    internal static string? ExplicitPath
    {
        get => s_explicitPath;
        set
        {
            lock (Gate)
            {
                if (s_identity is not null) throw new InvalidOperationException("SQLite selection is frozen after initialization.");
                s_explicitPath = value;
            }
        }
    }

    internal static unsafe SqliteImageIdentity EnsureLoaded()
    {
        if (Volatile.Read(ref s_identity) is { } initialized) return initialized;
        lock (Gate)
        {
            if (s_identity is { } prior) return prior;
            var selected = Path.GetFullPath(ExplicitPath ?? Environment.GetEnvironmentVariable("DB_PERF_SQLITE_NATIVE")
                ?? throw new ArgumentException("Supply --sqlite-native or DB_PERF_SQLITE_NATIVE with the frozen SQLite shared library."));
            var module = NativeLibrary.Load(selected);
            var resolverInstalled = false;
            try
            {
                var versionAddress = NativeLibrary.GetExport(module, "sqlite3_libversion");
                var mapped = LoadedPath(versionAddress);
                var expectedHash = HashFile(selected);
                if (HashFile(mapped) != expectedHash)
                    throw new InvalidOperationException("Actually mapped SQLite bytes differ from the selected image.");
                var version = (delegate* unmanaged[Cdecl]<nint>)versionAddress;
                var source = (delegate* unmanaged[Cdecl]<nint>)NativeLibrary.GetExport(module, "sqlite3_sourceid");
                var option = (delegate* unmanaged[Cdecl]<int, nint>)NativeLibrary.GetExport(module, "sqlite3_compileoption_get");
                var options = new List<string>();
                for (var index = 0; ; index++)
                {
                    var pointer = option(index);
                    if (pointer == nint.Zero) break;
                    options.Add(Marshal.PtrToStringUTF8(pointer)!);
                }
                options.Sort(StringComparer.Ordinal);
                var identity = new SqliteImageIdentity(mapped, expectedHash,
                    Marshal.PtrToStringUTF8(version())!, Marshal.PtrToStringUTF8(source())!, options.ToArray());
                if (identity.Version != "3.51.3")
                    throw new InvalidOperationException("The frozen campaign requires SQLite 3.51.3.");
                NativeLibrary.SetDllImportResolver(typeof(SQLitePCL.SQLite3Provider_sqlite3).Assembly,
                    (name, _, _) => name == "sqlite3" ? module : nint.Zero);
                resolverInstalled = true;
                SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
                if (SQLitePCL.raw.sqlite3_libversion().utf8_to_string() != identity.Version ||
                    SQLitePCL.raw.sqlite3_sourceid().utf8_to_string() != identity.SourceId)
                    throw new InvalidOperationException("SQLitePCLRaw's actual calls reach a different SQLite engine fingerprint.");
                // SQLitePCL's provider is process-global. Its one library owner intentionally
                // remains loaded until process exit; it is not a growing per-operation cache.
                s_module = module;
                Volatile.Write(ref s_identity, identity);
                return identity;
            }
            catch
            {
                // An installed resolver retains this handle even if provider initialization
                // fails. Leaving it valid is safer than exposing a freed function table.
                if (!resolverInstalled) NativeLibrary.Free(module);
                throw;
            }
        }
    }

    internal static unsafe (long Current, long Highwater) SnapshotMemory()
    {
        _ = EnsureLoaded();
        var current = (delegate* unmanaged[Cdecl]<long>)NativeLibrary.GetExport(s_module, "sqlite3_memory_used");
        var highwater = (delegate* unmanaged[Cdecl]<int, long>)NativeLibrary.GetExport(s_module, "sqlite3_memory_highwater");
        return (current(), highwater(0));
    }

    private static string LoadedPath(nint address)
    {
        var target = (ulong)(nuint)address;
        foreach (var line in File.ReadLines("/proc/self/maps"))
        {
            var parts = line.Split(' ', 6, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 6 || !parts[5].StartsWith('/')) continue;
            var range = parts[0].Split('-', 2);
            var first = ulong.Parse(range[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var last = ulong.Parse(range[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (target >= first && target < last) return Path.GetFullPath(parts[5]);
        }
        throw new InvalidOperationException("The selected SQLite function address has no file-backed native mapping.");
    }

    internal static string HashFile(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(file));
    }
}
#endif
