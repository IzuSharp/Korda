using System.Text.Json;
using NativeWeave.Interop.Extensibility;

namespace NativeWeave.Interop;

internal static class AssetLocation
{
    internal static string Resolve(EngineDescriptor descriptor, NativeAssetDescriptor asset, string rid, string? explicitPath)
    {
        if (explicitPath is not null)
            return explicitPath;
        string locationFile = Path.Combine(AppContext.BaseDirectory, $"NativeWeave.{descriptor.EngineId}.assets.json");
        try
        {
            using var stream = new FileStream(locationFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is < 2 or > 16384)
                throw Invalid(descriptor, rid);
            byte[] bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            // A writer replacing/growing the output record cannot smuggle a suffix
            // beyond the bounded record originally observed.
            if (stream.ReadByte() != -1)
                throw Invalid(descriptor, rid);
            return Read(bytes, descriptor, asset, rid);
        }
        catch (FileNotFoundException)
        {
            throw EngineModuleLoader.Failure(descriptor, rid, EngineLoadFailure.MissingAsset,
                "NWPKG002: the SDK-selected native output record is missing. Restore and rebuild the provider package.");
        }
        catch (DirectoryNotFoundException)
        {
            throw EngineModuleLoader.Failure(descriptor, rid, EngineLoadFailure.MissingAsset,
                "NWPKG002: the application output directory is missing. Restore and rebuild the provider package.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw EngineModuleLoader.Failure(descriptor, rid, EngineLoadFailure.MissingAsset,
                "NWPKG002: the native output record is unreadable. Repair the provider installation and file access.");
        }
    }

    internal static string Read(ReadOnlySpan<byte> bytes, EngineDescriptor descriptor, NativeAssetDescriptor asset, string rid)
    {
        if (bytes.Length is < 2 or > 16384)
            throw Invalid(descriptor, rid);
        try
        {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 32 });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                throw Invalid(descriptor, rid);
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            bool schemaSeen = false;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                    throw Invalid(descriptor, rid);
                string name = reader.GetString()!;
                if (!reader.Read())
                    throw Invalid(descriptor, rid);
                if (name == "schemaVersion")
                {
                    if (schemaSeen || reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out int schema) || schema != 1)
                        throw Invalid(descriptor, rid);
                    schemaSeen = true;
                }
                else if (name is "engineId" or "packageId" or "packageVersion" or "rid" or "sourcePath" or "outputPath" or "sha256")
                {
                    if (reader.TokenType != JsonTokenType.String || !values.TryAdd(name, reader.GetString()!))
                        throw Invalid(descriptor, rid);
                    MetadataValidation.Text(values[name], name, name is "sourcePath" or "outputPath" ? 4096 : 256);
                }
                else
                    reader.Skip(); // Future optional fields remain bounded by the whole record and depth limits.
            }
            if (!schemaSeen || values.Count != 7 || reader.TokenType != JsonTokenType.EndObject || reader.Read())
                throw Invalid(descriptor, rid);
            if (values["engineId"] != descriptor.EngineId || values["packageId"] != descriptor.PackageId ||
                values["packageVersion"] != descriptor.PackageVersion || values["rid"] != rid ||
                values["sourcePath"] != asset.RelativePath || values["sha256"] != asset.Sha256)
                throw Invalid(descriptor, rid);
            string relative = MetadataValidation.RelativePath(values["outputPath"], "outputPath");
            string basePath = Path.GetFullPath(AppContext.BaseDirectory);
            if (!Path.EndsInDirectorySeparator(basePath))
                basePath += Path.DirectorySeparatorChar;
            string result = Path.GetFullPath(Path.Combine(basePath, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!result.StartsWith(basePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw Invalid(descriptor, rid);
            return result;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
        {
            throw Invalid(descriptor, rid);
        }
    }

    private static EngineLoadException Invalid(EngineDescriptor descriptor, string rid) =>
        EngineModuleLoader.Failure(descriptor, rid, EngineLoadFailure.InvalidManifest,
            "NWPKG002: the bounded SDK native output record is malformed or disagrees with the package. Restore and rebuild the provider package.");
}
