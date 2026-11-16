using System.Collections.Immutable;
using System.Text.Json;

namespace NativeWeave.Generators.Model;

/// <summary>Reads the complete maintainer parser inventory; source positions do not enter the semantic digest.</summary>
internal static class SourceInventory
{
    internal static ImmutableArray<SourceExport> Read(ReadOnlySpan<byte> input, string expectedAdapterDigest)
    {
        if (input.Length > 1024 * 1024) Fail("NWABI002", "$", "Inventory exceeds 1 MiB.");
        try
        {
            using var document = JsonDocument.Parse(input.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            Object(root, "$", "schema_version", "adapter_sha256", "tool", "tool_version", "parser", "exports");
            if (Integer(root, "schema_version", "$") != 1 || Text(root, "adapter_sha256", "$") != expectedAdapterDigest)
                Fail("NWGEN001", "$", "Parsed inventory is stale or from a different adapter source.");
            if (Text(root, "tool", "$") != "nativeweave-binding-inventory" || Text(root, "tool_version", "$") != "0.1.0" || Text(root, "parser", "$") != "syn-2.0.119")
                Fail("NWGEN001", "$", "Unexpected inventory tool identity; use the pinned maintainer extractor.");
            var entries = root.GetProperty("exports");
            if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() > 4096) Fail("NWABI002", "$.exports", "Export array exceeds its finite 4096-item limit.");
            var builder = ImmutableArray.CreateBuilder<SourceExport>();
            foreach (var e in entries.EnumerateArray())
            {
                var path = "$.exports[" + builder.Count + "]";
                Object(e, path, "symbol", "calling_convention", "return_type", "generic", "parameters", "file", "line", "column");
                var parameters = e.GetProperty("parameters"); var generic = e.GetProperty("generic");
                if (parameters.ValueKind != JsonValueKind.Array || parameters.GetArrayLength() > 64) Fail("NWABI002", path + ".parameters", "Expected at most 64 parameter types.");
                if (generic.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) Fail("NWABI002", path + ".generic", "Expected generic flag.");
                var parameterTypes = ImmutableArray.CreateBuilder<string>();
                foreach (var type in parameters.EnumerateArray())
                {
                    if (type.ValueKind != JsonValueKind.String || type.GetString()!.Length is 0 or > 1024) Fail("NWABI002", path + ".parameters", "Expected bounded Rust type text.");
                    parameterTypes.Add(type.GetString()!);
                }
                var line = Integer(e, "line", path); var column = Integer(e, "column", path);
                if (line <= 0 || column <= 0) Fail("NWABI002", path, "Source positions must be positive.");
                builder.Add(new(Text(e, "symbol", path), Text(e, "calling_convention", path), Text(e, "return_type", path),
                    generic.GetBoolean(), parameterTypes.ToImmutable(), new(Text(e, "file", path), "export:" + Text(e, "symbol", path), line, column)));
            }
            return builder.ToImmutable();
        }
        catch (JsonException e)
        {
            throw new BindingValidationException("NWABI002", new("inventory", e.Path ?? "$", (e.LineNumber ?? 0) + 1, (e.BytePositionInLine ?? 0) + 1), "Malformed parser inventory JSON.");
        }
    }

    private static void Object(JsonElement e, string path, params string[] fields)
    {
        if (e.ValueKind != JsonValueKind.Object) Fail("NWABI002", path, "Expected an object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in e.EnumerateObject())
            if (!fields.Contains(p.Name, StringComparer.Ordinal) || !seen.Add(p.Name)) Fail("NWABI002", path + "." + p.Name, "Unknown or duplicate inventory field.");
        if (seen.Count != fields.Length) Fail("NWABI002", path, "Required inventory field is missing.");
    }
    private static string Text(JsonElement e, string name, string path)
    {
        var value = e.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String || value.GetString()!.Length > 4096) Fail("NWABI002", path + "." + name, "Expected bounded inventory text.");
        return value.GetString()!;
    }
    private static long Integer(JsonElement e, string name, string path)
    {
        var value = e.GetProperty(name); long number = 0;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out number)) Fail("NWABI002", path + "." + name, "Expected integer inventory value.");
        return number;
    }
    private static void Fail(string code, string path, string message) => throw new BindingValidationException(code, new("inventory", path, 1, 1), message);
}
