using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace NativeWeave.Generators.Model;

/// <summary>Strict version-one sidecar validation. Unknown fields and required semantics fail closed.</summary>
internal sealed class BindingReader
{
    private readonly string file;
    private readonly byte[] source;
    private readonly Dictionary<string, long> locations = new(StringComparer.Ordinal);
    private readonly CancellationToken cancellation;
    private const int MaximumInputBytes = 1024 * 1024;
    private static readonly Dictionary<string, (uint Size, uint Alignment)> Scalars = new(StringComparer.Ordinal)
    {
        ["u8"] = (1, 1),
        ["i8"] = (1, 1),
        ["u16"] = (2, 2),
        ["i16"] = (2, 2),
        ["u32"] = (4, 4),
        ["i32"] = (4, 4),
        ["f32"] = (4, 4),
        ["u64"] = (8, 8),
        ["i64"] = (8, 8),
        ["f64"] = (8, 8),
        ["usize"] = (8, 8),
    };

    private BindingReader(string file, byte[] source, CancellationToken cancellation)
    { this.file = file; this.source = source; this.cancellation = cancellation; }

    internal static BindingDescription Read(ReadOnlySpan<byte> input, string file,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (input.Length > MaximumInputBytes)
            throw new BindingValidationException("NWABI002", new(file, "$", 1, 1), "Sidecar exceeds the finite 1 MiB input limit.");
        var reader = new BindingReader(file, input.ToArray(), cancellationToken);
        try
        {
            var tokens = new Utf8JsonReader(reader.source, new JsonReaderOptions { MaxDepth = 64 });
            if (!tokens.Read()) reader.Fail("NWABI002", "$", "Empty sidecar.");
            reader.Index(ref tokens, "$");
            if (tokens.Read()) reader.Fail("NWABI002", "$", "Trailing JSON value.");
            using var document = JsonDocument.Parse(reader.source, new JsonDocumentOptions { MaxDepth = 64 });
            return reader.Build(document.RootElement);
        }
        catch (JsonException ex)
        {
            throw new BindingValidationException("NWABI002", new(file, ex.Path ?? "$", (ex.LineNumber ?? 0) + 1,
                (ex.BytePositionInLine ?? 0) + 1), "Malformed JSON; provide one bounded schema object.");
        }
    }

    private void Index(ref Utf8JsonReader reader, string path)
    {
        cancellation.ThrowIfCancellationRequested(); locations[path] = reader.TokenStartIndex;
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) Fail("NWABI002", path, "Expected property name.");
                var name = reader.GetString()!; var child = path + "." + name;
                locations[child] = reader.TokenStartIndex;
                if (!names.Add(name)) Fail("NWABI002", child, "Duplicate JSON property.");
                if (!reader.Read()) Fail("NWABI002", child, "Missing property value.");
                Index(ref reader, child);
            }
        }
        else if (reader.TokenType == JsonTokenType.StartArray)
        {
            var index = 0;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray) Index(ref reader, path + "[" + index++ + "]");
        }
    }

    private BindingDescription Build(JsonElement root)
    {
        Object(root, "$", "schema_version", "module", "provenance", "capabilities", "records", "exports", "owners", "operations", "callbacks", "managed_symbols");
        if (Number(root, "schema_version", "$") != 1) Fail("NWABI002", "$.schema_version", "Unknown schema version; migrate the sidecar explicitly.");
        var module = root.GetProperty("module");
        Object(module, "$.module", "id", "abi_major", "abi_minor", "rid", "target", "pointer_width_bits");
        var moduleId = Identity(module, "id", "$.module");
        if (Number(module, "abi_major", "$.module") == 0) Fail("NWABI002", "$.module.abi_major", "ABI major must be positive.");
        _ = Number(module, "abi_minor", "$.module");
        var rid = Text(module, "rid", "$.module"); var target = Text(module, "target", "$.module");
        if (!((rid == "linux-x64" && target == "x86_64-unknown-linux-gnu") ||
              (rid == "win-x64" && target == "x86_64-pc-windows-msvc")) || Number(module, "pointer_width_bits", "$.module") != 64)
            Fail("NWABI002", "$.module", "Target RID/triple/pointer width must describe one supported x64 ABI; other targets need explicit layout profiles.");
        Provenance(root.GetProperty("provenance"));
        var capabilities = Strings(root.GetProperty("capabilities"), "$.capabilities", true);
        var records = ImmutableArray.CreateBuilder<WireRecord>();
        foreach (var (entry, path) in Array(root, "records", "$"))
        {
            Object(entry, path, "name", "size", "alignment", "fields");
            var name = Identifier(entry, "name", path); var size = Number(entry, "size", path); var alignment = Number(entry, "alignment", path);
            if (alignment is not (1 or 2 or 4 or 8) || size is 0 or > 1048576 || size % alignment != 0)
                Fail("NWABI002", path, "Record size/alignment is not a bounded C layout.");
            var fields = ImmutableArray.CreateBuilder<WireField>(); uint end = 0; uint maximumAlignment = 1;
            foreach (var (field, fieldPath) in Array(entry, "fields", path, 256))
            {
                Object(field, fieldPath, "name", "type", "offset", "reserved");
                var fieldName = Identifier(field, "name", fieldPath); var type = Text(field, "type", fieldPath);
                if (!Scalars.TryGetValue(type, out var layout))
                    Fail("NWABI001", fieldPath + ".type", "Wire fields use fixed-width scalars; encode pointers as usize and never infer private Rust layout.");
                var offset = Number(field, "offset", fieldPath); var reserved = Boolean(field, "reserved", fieldPath);
                var expected = checked((end + layout.Alignment - 1) / layout.Alignment * layout.Alignment);
                if (offset != expected || offset + (ulong)layout.Size > size)
                    Fail("NWABI002", fieldPath + ".offset", "Field offsets must match the declared sequential C layout without overlap.");
                end = checked(offset + layout.Size); maximumAlignment = Math.Max(maximumAlignment, layout.Alignment);
                fields.Add(new(fieldName, type, offset, reserved));
            }
            Unique(fields.Select(x => x.Name), path, "field");
            if (fields.Count == 0 || maximumAlignment != alignment || checked((end + alignment - 1) / alignment * alignment) != size)
                Fail("NWABI002", path, "Record has inconsistent fields, final size or alignment. Padding is not semantic data.");
            records.Add(new(name, size, alignment, fields.ToImmutable()));
        }
        Unique(records.Select(x => x.Name), "$.records", "record");
        var recordNames = records.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        var owners = ImmutableArray.CreateBuilder<BindingOwner>();
        foreach (var (entry, path) in Array(root, "owners", "$"))
        {
            Object(entry, path, "kind", "module", "release", "clone", "parent", "release_executor", "close_progress");
            var kind = Identifier(entry, "kind", path); var creator = Identity(entry, "module", path);
            if (creator != moduleId) Fail("NWOWN001", path + ".module", "An owner must be released by its creating module.");
            owners.Add(new(kind, creator, Identifier(entry, "release", path), OptionalIdentifier(entry, "clone", path), OptionalIdentifier(entry, "parent", path), Choice(entry, "release_executor", path, "calling-thread", "owner-thread", "runtime", "external-driver"), Choice(entry, "close_progress", path, "none", "caller-drives", "runtime-drives")));
        }
        Unique(owners.Select(x => x.Kind), "$.owners", "owner kind");
        var ownerNames = owners.Select(x => x.Kind).ToHashSet(StringComparer.Ordinal);
        foreach (var owner in owners)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal) { owner.Kind }; var parent = owner.Parent;
            while (parent.Length != 0)
            {
                if (!visited.Add(parent) || !ownerNames.Contains(parent)) Fail("NWOWN001", "$.owners", "Unknown or cyclic parent-state owner relation.");
                parent = owners.First(x => x.Kind == parent).Parent;
            }
        }
        var exports = ImmutableArray.CreateBuilder<BindingExport>();
        foreach (var (entry, path) in Array(root, "exports", "$"))
        {
            Object(entry, path, "symbol", "calling_convention", "return", "capability", "error", "thread", "reentrancy", "targets", "parameters");
            var symbol = Identifier(entry, "symbol", path);
            if (Text(entry, "calling_convention", path) != "C") Fail("NWABI002", path, "Only exact C calling convention is supported.");
            var result = Text(entry, "return", path); if (result is not ("u32" or "void")) Fail("NWABI001", path + ".return", "Exports return fixed u32 status or void cleanup; values use explicit output storage.");
            var cap = Text(entry, "capability", path);
            if (cap.Length != 0 && !capabilities.Contains(cap)) Fail("NWABI002", path + ".capability", "Unknown required capability.");
            var error = Choice(entry, "error", path, "status-v1", "cleanup-no-fail");
            if ((result == "void") != (error == "cleanup-no-fail")) Fail("NWABI002", path, "Return storage and error profile disagree.");
            var thread = Choice(entry, "thread", path, "free-threaded", "serialized", "owner-thread", "externally-driven");
            var reentrancy = Choice(entry, "reentrancy", path, "forbidden", "notification-only");
            var targets = Strings(entry.GetProperty("targets"), path + ".targets", false);
            if (targets.Count != 1 || !targets.Contains(rid)) Fail("NWABI002", path + ".targets", "This normalized description must name exactly its target; normalize conditional exports per target.");
            var parameters = ImmutableArray.CreateBuilder<BindingParameter>();
            foreach (var (parameter, parameterPath) in Array(entry, "parameters", path, 64))
            {
                Object(parameter, parameterPath, "name", "type", "direction", "nullable", "borrow_end", "owner", "mutable", "copy_before_accept", "maximum_bytes");
                var name = Identifier(parameter, "name", parameterPath); var type = Text(parameter, "type", parameterPath);
                var direction = Choice(parameter, "direction", parameterPath, "in", "out", "inout");
                var nullable = Boolean(parameter, "nullable", parameterPath); var mutable = Boolean(parameter, "mutable", parameterPath);
                var copied = Boolean(parameter, "copy_before_accept", parameterPath); var bound = Number(parameter, "maximum_bytes", parameterPath);
                var borrow = Choice(parameter, "borrow_end", parameterPath, "none", "call", "callback", "owner", "retirement", "transfer-success");
                var owner = OptionalIdentifier(parameter, "owner", parameterPath);
                var pointer = type.StartsWith("const:", StringComparison.Ordinal) || type.StartsWith("mut:", StringComparison.Ordinal);
                var typeParts = type.Split(':');
                var targetType = typeParts[^1];
                if (typeParts.Length > 3 || typeParts.Take(typeParts.Length - 1).Any(x => x is not ("const" or "mut"))) Fail("NWABI001", parameterPath + ".type", "Unsupported pointer nesting; use at most one output-owner indirection.");
                if (!(Scalars.ContainsKey(targetType) || (pointer && (targetType == "void" || recordNames.Contains(targetType) || ownerNames.Contains(targetType)))))
                    Fail("NWABI001", parameterPath + ".type", "Unknown or unsupported ABI type; supply a declared record/owner or a fixed scalar.");
                if (owner.Length != 0 && !ownerNames.Contains(owner)) Fail("NWOWN001", parameterPath + ".owner", "Unknown owner kind.");
                if (!pointer && (nullable || direction != "in" || borrow != "none" || mutable || copied || bound != 0 || owner.Length != 0))
                    Fail("NWABI001", parameterPath, "Scalar by-value inputs have no pointer lifetime/nullability contract.");
                if (pointer && (bound == 0 || borrow == "none" || (direction != "in" && !type.StartsWith("mut:", StringComparison.Ordinal))))
                    Fail("NWOWN001", parameterPath, "Pointers require finite bounds, explicit lifetime and mutable output storage.");
                if (mutable && !type.StartsWith("mut:", StringComparison.Ordinal)) Fail("NWOWN001", parameterPath, "Exclusive mutable access requires a mutable pointer.");
                if (borrow is "owner" or "transfer-success" && owner.Length == 0) Fail("NWOWN001", parameterPath, "Retained/returned owners require an explicit kind and release pair.");
                if (borrow == "transfer-success" && (direction != "out" || !type.StartsWith("mut:", StringComparison.Ordinal))) Fail("NWOWN001", parameterPath, "Output owner storage must be mutable and transfer only on success; initialize the stored owner to null on failure.");
                if (copied && (direction != "in" || borrow != "call")) Fail("NWASYNC001", parameterPath, "Copy-before-accept is only meaningful for call-borrowed inputs.");
                parameters.Add(new(name, type, direction, nullable, borrow, owner, mutable, copied, bound));
            }
            Unique(parameters.Select(x => x.Name), path, "parameter");
            exports.Add(new(symbol, result, cap, error, thread, reentrancy, parameters.ToImmutable()));
        }
        Unique(exports.Select(x => x.Symbol), "$.exports", "export");
        var bySymbol = exports.ToDictionary(x => x.Symbol, StringComparer.Ordinal);
        BindingExport Function(string symbol, string path)
        { if (!bySymbol.TryGetValue(symbol, out var export)) Fail("NWABI002", path, "Missing referenced export: " + symbol); return export!; }
        foreach (var owner in owners)
        {
            var release = Function(owner.Release, "$.owners");
            if (release.ErrorProfile != "cleanup-no-fail" || release.Parameters.Length != 1 ||
                release.Parameters[0].Owner != owner.Kind || release.Parameters[0].BorrowEnd != "call" || release.Parameters[0].Type != "mut:" + owner.Kind)
                Fail("NWOWN001", "$.owners", "Release must be nonfallible cleanup of exactly one borrowed owner from this module.");
            if (owner.ReleaseExecutor == "owner-thread" && release.ThreadProfile != "owner-thread") Fail("NWOWN001", "$.owners", "Owner-thread release requires a matching export thread profile.");
            if (owner.ReleaseExecutor == "external-driver" && owner.CloseProgress != "caller-drives") Fail("NWOWN001", "$.owners", "An externally driven release must declare caller progress.");
            if (owners.Count(x => x.Release == owner.Release) != 1) Fail("NWOWN001", "$.owners", "Each owner kind requires exactly one unambiguous release export.");
            if (owner.CloneExport.Length != 0)
            {
                var clone = Function(owner.CloneExport, "$.owners");
                if (!clone.Parameters.Any(x => x.Owner == owner.Kind && x.BorrowEnd == "transfer-success")) Fail("NWOWN001", "$.owners", "Clone must return one explicitly transferred owner of the same kind.");
            }
        }
        var operations = ImmutableArray.CreateBuilder<BindingOperation>();
        foreach (var (entry, path) in Array(root, "operations", "$"))
        {
            Object(entry, path, "name", "owner", "create", "start", "request_cancel", "take_result", "try_retire", "release", "input_end", "state_profile");
            var name = Identifier(entry, "name", path); var owner = Identifier(entry, "owner", path);
            if (!ownerNames.Contains(owner)) Fail("NWOWN001", path, "Operation owner is not declared.");
            if (Text(entry, "state_profile", path) != "D06-v1") Fail("NWASYNC001", path, "Unknown async state profile; declare D06-v1 transitions explicitly.");
            var inputEnd = Choice(entry, "input_end", path, "copied-before-accept", "retirement");
            var create = Identifier(entry, "create", path); var start = Identifier(entry, "start", path);
            var cancel = Identifier(entry, "request_cancel", path); var take = Identifier(entry, "take_result", path);
            var retire = Identifier(entry, "try_retire", path); var release = Identifier(entry, "release", path);
            var functions = new[] { create, start, cancel, take, retire, release };
            if (functions.Distinct(StringComparer.Ordinal).Count() != 6) Fail("NWASYNC001", path, "Create/start/cancel/take/retire/release are separate operation steps.");
            foreach (var symbol in functions) _ = Function(symbol, path);
            if (owners.First(x => x.Kind == owner).Release != release) Fail("NWOWN001", path, "Operation release differs from its owner release pair.");
            if (!Function(create, path).Parameters.Any(x => x.Owner == owner && x.BorrowEnd == "transfer-success")) Fail("NWOWN001", path, "Create must transfer its operation owner only on success.");
            foreach (var symbol in new[] { start, cancel, take, retire })
                if (!Function(symbol, path).Parameters.Any(x => x.Owner == owner && x.BorrowEnd == "call")) Fail("NWASYNC001", path, "Every operation step must borrow the same retained operation owner.");
            foreach (var parameter in Function(start, path).Parameters.Where(x => x.Direction == "in" && x.Type.Contains(':') && x.Owner.Length == 0))
            {
                if (inputEnd == "copied-before-accept" && !parameter.CopyBeforeAccept || inputEnd == "retirement" && parameter.BorrowEnd != "retirement")
                    Fail("NWASYNC001", path, "Async input cannot retain a call-scoped pin: copy before accept or keep it until retirement.");
            }
            operations.Add(new(name, owner, create, start, cancel, take, retire, release, inputEnd));
        }
        Unique(operations.Select(x => x.Name), "$.operations", "operation");
        var callbackSymbols = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (entry, path) in Array(root, "callbacks", "$"))
        {
            Object(entry, path, "symbol", "cookie_index", "notification_owner", "reentrancy", "close", "drain", "profile");
            var callback = Function(Identifier(entry, "symbol", path), path); var cookie = Number(entry, "cookie_index", path);
            if (!callbackSymbols.Add(callback.Symbol)) Fail("NWGEN001", path, "Duplicate callback contract.");
            if (cookie >= callback.Parameters.Length || callback.Parameters[(int)cookie].Type != "usize" || callback.ReturnType != "u32")
                Fail("NWABI001", path, "Callback needs an exact blittable usize cookie and u32 return.");
            if (!ownerNames.Contains(Identifier(entry, "notification_owner", path)) || Text(entry, "reentrancy", path) != "notification-only" || callback.Reentrancy != "notification-only")
                Fail("NWASYNC001", path, "Callbacks publish notifications under a retained owner; they cannot call user code reentrantly.");
            if (Text(entry, "profile", path) != "D05-close-drain-v1") Fail("NWASYNC001", path, "Callback retirement must follow D05 close/drain.");
            _ = Function(Identifier(entry, "close", path), path); _ = Function(Identifier(entry, "drain", path), path);
        }
        var managed = Strings(root.GetProperty("managed_symbols"), "$.managed_symbols", false).Order(StringComparer.Ordinal).ToImmutableArray();
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) Canonical(root, writer, "$", cancellation);
        return new(moduleId, buffer.ToArray(), records.OrderBy(x => x.Name, StringComparer.Ordinal).ToImmutableArray(),
            exports.OrderBy(x => x.Symbol, StringComparer.Ordinal).ToImmutableArray(), owners.OrderBy(x => x.Kind, StringComparer.Ordinal).ToImmutableArray(),
            operations.OrderBy(x => x.Name, StringComparer.Ordinal).ToImmutableArray(), managed);
    }

    private void Provenance(JsonElement e)
    {
        const string path = "$.provenance"; Object(e, path, "adapter_sha256", "tool", "tool_version", "upstream");
        var digest = Text(e, "adapter_sha256", path);
        if (digest.Length != 64 || !digest.All(c => char.IsAsciiHexDigit(c) && !char.IsAsciiLetterUpper(c))) Fail("NWGEN001", path, "Adapter source requires a lowercase SHA-256 input identity.");
        _ = Identity(e, "tool", path); var version = Text(e, "tool_version", path);
        if (version.Length == 0) Fail("NWGEN001", path, "Generating tool version is required.");
        var repositories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (upstream, itemPath) in Array(e, "upstream", path))
        {
            Object(upstream, itemPath, "repository", "commit", "features");
            var repository = Text(upstream, "repository", itemPath); var commit = Text(upstream, "commit", itemPath);
            if (!Uri.TryCreate(repository, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                Fail("NWGEN001", itemPath, "Provenance repository must be an HTTPS source identity without credentials or query.");
            if (!repositories.Add(repository)) Fail("NWGEN001", itemPath, "Duplicate upstream repository identity.");
            if (commit.Length != 40 || !commit.All(char.IsAsciiHexDigit)) Fail("NWGEN001", itemPath, "Pin an exact upstream commit, not a moving branch.");
            _ = Strings(upstream.GetProperty("features"), itemPath + ".features", false);
        }
    }

    private static void Canonical(JsonElement e, Utf8JsonWriter writer, string path, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var p in e.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
                { writer.WritePropertyName(p.Name); Canonical(p.Value, writer, path + "." + p.Name, cancellation); }
                writer.WriteEndObject(); break;
            case JsonValueKind.Array:
                writer.WriteStartArray(); IEnumerable<JsonElement> values = e.EnumerateArray();
                var key = path switch { "$.records" => "name", "$.exports" => "symbol", "$.owners" => "kind", "$.operations" => "name", "$.callbacks" => "symbol", "$.provenance.upstream" => "repository", _ => "" };
                if (key.Length != 0) values = values.OrderBy(x => x.GetProperty(key).GetString(), StringComparer.Ordinal);
                else if (path.EndsWith(".capabilities", StringComparison.Ordinal) || path.EndsWith(".targets", StringComparison.Ordinal) || path.EndsWith(".features", StringComparison.Ordinal) || path == "$.managed_symbols")
                    values = values.OrderBy(x => x.GetString(), StringComparer.Ordinal);
                foreach (var value in values) Canonical(value, writer, path + "[]", cancellation);
                writer.WriteEndArray(); break;
            default: e.WriteTo(writer); break;
        }
    }

    private void Object(JsonElement e, string path, params string[] names)
    {
        if (e.ValueKind != JsonValueKind.Object) Fail("NWABI002", path, "Expected an object.");
        foreach (var p in e.EnumerateObject()) if (!names.Contains(p.Name, StringComparer.Ordinal)) Fail("NWABI002", path + "." + p.Name, "Unknown required semantic field; update the supported schema explicitly.");
        foreach (var name in names) if (!e.TryGetProperty(name, out _)) Fail("NWABI002", path + "." + name, "Required field is missing.");
    }
    private IEnumerable<(JsonElement Entry, string Path)> Array(JsonElement e, string name, string path, int maximum = 4096)
    {
        var value = e.GetProperty(name); var itemPath = path + "." + name;
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > maximum) Fail("NWABI002", itemPath, "Expected a finite array within its item limit.");
        var i = 0; foreach (var item in value.EnumerateArray()) { cancellation.ThrowIfCancellationRequested(); yield return (item, itemPath + "[" + i++ + "]"); }
    }
    private HashSet<string> Strings(JsonElement e, string path, bool identity)
    {
        if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() > 4096) Fail("NWABI002", path, "Expected a bounded string inventory.");
        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var v in e.EnumerateArray())
        {
            if (v.ValueKind != JsonValueKind.String) Fail("NWABI002", path, "Expected string identity.");
            var text = v.GetString()!;
            if (text.Length is 0 or > 1024 || identity && !ValidIdentity(text) || !values.Add(text)) Fail("NWGEN001", path, "Invalid or duplicate inventory identity.");
        }
        return values;
    }
    private string Text(JsonElement e, string name, string path)
    { var v = e.GetProperty(name); if (v.ValueKind != JsonValueKind.String || v.GetString()!.Length > 1024) Fail("NWABI002", path + "." + name, "Expected bounded text."); return v.GetString()!; }
    private uint Number(JsonElement e, string name, string path)
    { var v = e.GetProperty(name); uint n = 0; if (v.ValueKind != JsonValueKind.Number || !v.TryGetUInt32(out n)) Fail("NWABI002", path + "." + name, "Expected an unsigned 32-bit integer."); return n; }
    private bool Boolean(JsonElement e, string name, string path)
    { var v = e.GetProperty(name); if (v.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) Fail("NWABI002", path + "." + name, "Expected a boolean."); return v.GetBoolean(); }
    private string Identifier(JsonElement e, string name, string path)
    { var text = Text(e, name, path); if (!BindingDescription.IsIdentifier(text)) Fail("NWGEN001", path + "." + name, "Use a unique ASCII C identifier, not a namespace-stripped or generic short name."); return text; }
    private string OptionalIdentifier(JsonElement e, string name, string path)
    { var text = Text(e, name, path); if (text.Length != 0 && !BindingDescription.IsIdentifier(text)) Fail("NWGEN001", path + "." + name, "Expected an identifier or empty optional relation."); return text; }
    private string Identity(JsonElement e, string name, string path)
    { var text = Text(e, name, path); if (!ValidIdentity(text)) Fail("NWGEN001", path + "." + name, "Expected a stable qualified identity."); return text; }
    private static bool ValidIdentity(string s) => s.Length is > 0 and <= 128 && char.IsAsciiLetterLower(s[0]) && s.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '-' or '_');
    private string Choice(JsonElement e, string name, string path, params string[] choices)
    { var text = Text(e, name, path); if (!choices.Contains(text, StringComparer.Ordinal)) Fail("NWABI002", path + "." + name, "Unknown profile; supported: " + string.Join(", ", choices)); return text; }
    private void Unique(IEnumerable<string> values, string path, string label)
    { var seen = new HashSet<string>(StringComparer.Ordinal); foreach (var value in values) if (!seen.Add(value)) Fail("NWGEN001", path, "Duplicate " + label + ": " + value); }
    private void Fail(string code, string path, string message)
    {
        var position = locations.GetValueOrDefault(path, locations.GetValueOrDefault(path[..Math.Max(1, path.LastIndexOf('.'))], 0));
        long line = 1; long column = 1;
        for (var i = 0; i < position; i++) { if (source[i] == (byte)'\n') { line++; column = 1; } else column++; }
        throw new BindingValidationException(code, new(file, path, line, column), message);
    }
}
