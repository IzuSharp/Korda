using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace NativeWeave.Server.Mcp;

/// <summary>Registers finite static tools, fixed text resources and string-argument prompts.</summary>
/// <remarks>Configuration is single threaded. MapMcp freezes registration; handlers and metadata remain rooted until the owning application retires. At most1024 entries of each kind and4MiB aggregate metadata are admitted; each schema is at most64KiB. The package's compiler analyzer preserves complete static DTO fields for bounded JIT and NativeAOT layout inspection; ordinary DTO/handler changes only rebuild C#. The authenticated endpoint policy applies to every automatic list and business call.</remarks>
public sealed class McpRegistry
{
    private readonly List<McpEntry> _entries = [];
    private readonly HashSet<string>[] _names = [new(StringComparer.Ordinal), new(StringComparer.Ordinal), new(StringComparer.Ordinal)];
    private readonly HashSet<string> _resourceNames = new(StringComparer.Ordinal);
    private bool _frozen;
    private int _metadata;
    /// <summary>Creates an empty registry without opening native resources.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Mcp.McpRegistry" path="/member/remarks" />
    public McpRegistry() { }
    /// <summary>Registers one typed tool using explicit source-generated input/output JSON metadata.</summary>
    /// <typeparam name="TInput">A non-null statically mapped object input DTO.</typeparam>
    /// <typeparam name="TOutput">A non-null statically mapped object output DTO.</typeparam>
    /// <param name="name">Unique finite ASCII tool name.</param>
    /// <param name="description">Bounded tool documentation.</param>
    /// <param name="inputTypeInfo">Explicit static input metadata; no reflection fallback.</param>
    /// <param name="outputTypeInfo">Explicit static output metadata; no reflection fallback.</param>
    /// <param name="handler">Typed callback invoked after one shared authorization gate and input validation.</param>
    /// <returns>This registry for further registration before MapMcp.</returns>
    /// <exception cref="ArgumentException">The identity/schema is invalid, duplicate or outside the finite subset, or its complete static layout proof is absent or exceeds startup scratch; schema failures identify NWAPI-S004.</exception>
    /// <exception cref="InvalidOperationException">Registration is already frozen.</exception>
    /// <remarks>Schema export and complete CLR layout inspection run once under startup scratch admission. Private, ignored and inherited storage and value-type padding contribute to per-tool input/output admission before native SDK parsing, so sparse input can still exceed the request budget. Root-only object normalization preserves nested nullability; one nonsemantic root extension records the proven allocation terms in the exact registered schema. Successful results produce both structuredContent and one JSON text item; unexpected business/output exceptions produce a redacted tool error.</remarks>
    public McpRegistry AddTool<TInput, TOutput>(string name, string description, JsonTypeInfo<TInput> inputTypeInfo, JsonTypeInfo<TOutput> outputTypeInfo, Func<TInput, McpCallContext, Task<McpToolResult<TOutput>>> handler)
    {
        Mutable(); McpValidation.Name(name, nameof(name)); McpValidation.Text(description, 4096, nameof(description), true); ArgumentNullException.ThrowIfNull(handler);
        var input = StaticSchema.Create(name, inputTypeInfo); var output = StaticSchema.Create(name, outputTypeInfo);
        return Add(new ToolEntry<TInput, TOutput>(name, description, inputTypeInfo, outputTypeInfo, input, output, handler), input.Length + output.Length + McpValidation.Utf8.GetByteCount(name + description) + 256);
    }
    /// <summary>Registers one fixed-URI text resource.</summary>
    /// <param name="uri">Unique absolute resource URI, at most4096 UTF-8 bytes.</param>
    /// <param name="name">Finite resource name.</param>
    /// <param name="mimeType">Exact MIME type required on every returned resource.</param>
    /// <param name="handler">Typed handler sharing the authenticated request scope.</param>
    /// <param name="description">Optional bounded documentation.</param>
    /// <returns>This registry before freeze.</returns>
    /// <exception cref="ArgumentException">Metadata is invalid or duplicated.</exception>
    /// <exception cref="InvalidOperationException">Registration is frozen.</exception>
    /// <remarks>Binary data, templates and subscriptions are not enabled. Returned URI/MIME must exactly match registration; native and managed UTF-8/JSON overlap is admitted before encoding.</remarks>
    public McpRegistry AddResource(string uri, string name, string mimeType, Func<McpCallContext, Task<McpTextResource>> handler, string? description = null)
    {
        Mutable(); McpValidation.Text(uri, 4096, nameof(uri)); if (!Uri.TryCreate(uri, UriKind.Absolute, out _)) throw new ArgumentException("A resource URI must be absolute.", nameof(uri));
        McpValidation.Name(name, nameof(name)); McpValidation.Text(mimeType, 256, nameof(mimeType)); if (description is not null) McpValidation.Text(description, 4096, nameof(description), true); ArgumentNullException.ThrowIfNull(handler);
        return Add(new ResourceEntry(uri, name, mimeType, description, handler), McpValidation.Utf8.GetByteCount(uri + name + mimeType + description) + 256);
    }
    /// <summary>Registers one named text prompt with a copied finite string-argument description.</summary>
    /// <param name="name">Unique finite prompt name.</param>
    /// <param name="description">Bounded prompt documentation.</param>
    /// <param name="arguments">Unique string arguments and required flags, copied immediately.</param>
    /// <param name="handler">Typed callback receiving only validated supplied strings.</param>
    /// <returns>This registry before freeze.</returns>
    /// <exception cref="ArgumentException">Metadata/argument names are invalid or duplicated.</exception>
    /// <exception cref="InvalidOperationException">Registration is frozen.</exception>
    /// <remarks>Unknown, missing required and non-string input rejects before invocation. Results contain at most128 user/assistant text messages; all automatic metadata methods use the same endpoint authorization.</remarks>
    public McpRegistry AddPrompt(string name, string description, IReadOnlyList<McpPromptArgument> arguments, Func<IReadOnlyDictionary<string, string>, McpCallContext, Task<IReadOnlyList<McpPromptMessage>>> handler)
    {
        Mutable(); McpValidation.Name(name, nameof(name)); McpValidation.Text(description, 4096, nameof(description), true); ArgumentNullException.ThrowIfNull(arguments); ArgumentNullException.ThrowIfNull(handler);
        if (arguments.Count > 256) throw new ArgumentException("At most256 prompt arguments are admitted.", nameof(arguments));
        var copy = new McpPromptArgument[arguments.Count]; var names = new HashSet<string>(StringComparer.Ordinal); int bytes = McpValidation.Utf8.GetByteCount(name + description) + 256;
        for (int index = 0; index < copy.Length; index++)
        {
            var arg = arguments[index] ?? throw new ArgumentException("Null prompt argument.", nameof(arguments)); McpValidation.Name(arg.Name, nameof(arguments));
            if (!names.Add(arg.Name)) throw new ArgumentException("Duplicate prompt argument.", nameof(arguments)); if (arg.Description is not null) McpValidation.Text(arg.Description, 4096, nameof(arguments), true);
            copy[index] = arg; bytes = checked(bytes + McpValidation.Utf8.GetByteCount(arg.Name + arg.Description) + 64);
        }
        return Add(new PromptEntry(name, description, copy, handler), bytes);
    }
    private void Mutable() { if (_frozen) throw new InvalidOperationException("The MCP registry is frozen by MapMcp."); }
    private McpRegistry Add(McpEntry entry, int bytes)
    {
        var names = _names[entry.Kind];
        if (names.Count == 1024 || names.Contains(entry.Identity) || entry.Kind == 1 && _resourceNames.Contains(entry.Name) || bytes > 4 * 1024 * 1024 - _metadata) throw new ArgumentException("Duplicate MCP identity or finite registry metadata limit.");
        names.Add(entry.Identity); if (entry.Kind == 1) _resourceNames.Add(entry.Name); _entries.Add(entry); _metadata += bytes; return this;
    }
    internal McpEntry[] Snapshot() => [.. _entries];
    internal void Freeze() => _frozen = true;
}

internal abstract class McpEntry(uint kind, string identity, string name, string? description)
{
    internal uint Kind => kind;
    internal string Identity => identity;
    internal string Name => name;
    internal string? Description => description;
    internal abstract void Metadata(Utf8JsonWriter writer, ulong handler);
    internal abstract Task<McpReply> InvokeAsync(ReadOnlyMemory<byte> arguments, McpCallContext context);
    internal static McpReply ProtocolError() => McpReply.Error(-32602, "Invalid statically registered arguments");
}
internal sealed class ToolEntry<TInput, TOutput>(string name, string description, JsonTypeInfo<TInput> inputInfo, JsonTypeInfo<TOutput> outputInfo, byte[] inputSchema, byte[] outputSchema, Func<TInput, McpCallContext, Task<McpToolResult<TOutput>>> handler) : McpEntry(0, name, name, description)
{
    internal override void Metadata(Utf8JsonWriter writer, ulong id)
    {
        writer.WriteStartObject(); writer.WriteString("name", Name); writer.WriteString("description", Description); writer.WriteNumber("handler_id", id); writer.WriteNumber("input_codec_id", checked(id * 2)); writer.WriteNumber("output_codec_id", checked(id * 2 + 1));
        writer.WritePropertyName("input_schema"); writer.WriteRawValue(inputSchema); writer.WritePropertyName("output_schema"); writer.WriteRawValue(outputSchema); writer.WriteEndObject();
    }
    internal override async Task<McpReply> InvokeAsync(ReadOnlyMemory<byte> arguments, McpCallContext context)
    {
        TInput? input;
        try { input = JsonSerializer.Deserialize(arguments.Span, inputInfo); if (input is null) return ProtocolError(); }
        catch (Exception error) when (error is JsonException or FormatException or OverflowException or ArgumentException) { return ProtocolError(); }
        try
        {
            var result = await handler(input, context).ConfigureAwait(false);
            return result is null ? McpReply.ToolError("Tool execution failed") : result.IsError ? McpReply.ToolError(result.Message!) : McpReply.Tool(result.Value!, outputInfo);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested) { throw; }
        catch { return McpReply.ToolError("Tool execution failed"); }
    }
}
internal sealed class ResourceEntry(string uri, string name, string mime, string? description, Func<McpCallContext, Task<McpTextResource>> handler) : McpEntry(1, uri, name, description)
{
    internal override void Metadata(Utf8JsonWriter writer, ulong id)
    { writer.WriteStartObject(); writer.WriteString("uri", Identity); writer.WriteString("name", Name); writer.WriteString("mime_type", mime); writer.WriteString("description", Description); writer.WriteNumber("handler_id", id); writer.WriteEndObject(); }
    internal override async Task<McpReply> InvokeAsync(ReadOnlyMemory<byte> arguments, McpCallContext context)
    {
        try
        {
            var result = await handler(context).ConfigureAwait(false);
            if (result is null || result.Uri != Identity || result.MimeType != mime) return McpReply.Error(-32603, "Resource output failed");
            return McpReply.Resource(result);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested) { throw; }
        catch { return McpReply.Error(-32603, "Resource execution failed"); }
    }
}
internal sealed class PromptEntry(string name, string description, McpPromptArgument[] declared, Func<IReadOnlyDictionary<string, string>, McpCallContext, Task<IReadOnlyList<McpPromptMessage>>> handler) : McpEntry(2, name, name, description)
{
    internal override void Metadata(Utf8JsonWriter writer, ulong id)
    {
        writer.WriteStartObject(); writer.WriteString("name", Name); writer.WriteString("description", Description); writer.WriteNumber("handler_id", id); writer.WriteStartArray("arguments");
        foreach (var argument in declared) { writer.WriteStartObject(); writer.WriteString("name", argument.Name); writer.WriteString("description", argument.Description); writer.WriteBoolean("required", argument.Required); writer.WriteEndObject(); }
        writer.WriteEndArray(); writer.WriteEndObject();
    }
    internal override async Task<McpReply> InvokeAsync(ReadOnlyMemory<byte> arguments, McpCallContext context)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(arguments, new() { MaxDepth = 32 }); if (doc.RootElement.ValueKind != JsonValueKind.Object) return ProtocolError();
            foreach (var entry in doc.RootElement.EnumerateObject()) if (entry.Value.ValueKind != JsonValueKind.String || !declared.Any(a => a.Name == entry.Name) || !values.TryAdd(entry.Name, entry.Value.GetString()!)) return ProtocolError();
            if (declared.Any(a => a.Required && !values.ContainsKey(a.Name))) return ProtocolError();
        }
        catch (JsonException) { return ProtocolError(); }
        try { return McpReply.Prompt(await handler(values, context).ConfigureAwait(false)); }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested) { throw; }
        catch { return McpReply.Error(-32603, "Prompt execution failed"); }
    }
}

// A deferred encoder retains only the application result until output admission.
// It does not allocate JSON payload while the native write reservation waits.
internal sealed class McpReply(uint kind, Action<Utf8JsonWriter> write)
{
    internal const int MaximumBytes = 262_144;
    internal uint Kind => kind;
    internal byte[] Encode()
    { using var buffer = new BoundedJsonBuffer(MaximumBytes); using (var writer = new Utf8JsonWriter(buffer)) write(writer); return buffer.WrittenMemory.ToArray(); }
    internal static McpReply Error(int code, string message) => new(2, writer => { writer.WriteStartObject(); writer.WriteNumber("code", code); writer.WriteString("message", message); writer.WriteEndObject(); });
    internal static McpReply ToolError(string message) => new(1, writer => { writer.WriteStartObject(); writer.WriteStartArray("content"); Text(writer, message); writer.WriteEndArray(); writer.WriteBoolean("isError", true); writer.WriteEndObject(); });
    internal static McpReply Tool<T>(T value, JsonTypeInfo<T> info) => new(0, writer =>
    {
        StaticSchema.CheckOutput(value, info);
        using var dto = new BoundedJsonBuffer(MaximumBytes); using (var json = new Utf8JsonWriter(dto)) JsonSerializer.Serialize(json, value, info);
        if (dto.WrittenMemory.Span.IsEmpty || dto.WrittenMemory.Span[0] != (byte)'{') throw new JsonException("Static tool output must remain an object.");
        writer.WriteStartObject(); writer.WritePropertyName("structuredContent"); writer.WriteRawValue(dto.WrittenMemory.Span); writer.WriteStartArray("content"); writer.WriteStartObject(); writer.WriteString("type", "text"); writer.WritePropertyName("text"); writer.WriteStringValue(dto.WrittenMemory.Span); writer.WriteEndObject(); writer.WriteEndArray(); writer.WriteBoolean("isError", false); writer.WriteEndObject();
    });
    internal static McpReply Resource(McpTextResource value) => new(0, writer =>
    { McpValidation.Text(value.Text, MaximumBytes, nameof(value), true); writer.WriteStartObject(); writer.WriteStartArray("contents"); writer.WriteStartObject(); writer.WriteString("uri", value.Uri); writer.WriteString("mimeType", value.MimeType); writer.WriteString("text", value.Text); writer.WriteEndObject(); writer.WriteEndArray(); writer.WriteEndObject(); });
    internal static McpReply Prompt(IReadOnlyList<McpPromptMessage> values) => new(0, writer =>
    {
        ArgumentNullException.ThrowIfNull(values); if (values.Count > 128) throw new ArgumentException("Prompt message limit."); writer.WriteStartObject(); writer.WriteStartArray("messages");
        foreach (var message in values) { if (message is null || message.Role is not ("user" or "assistant")) throw new ArgumentException("Invalid prompt role."); McpValidation.Text(message.Text, MaximumBytes, nameof(values), true); writer.WriteStartObject(); writer.WriteString("role", message.Role); writer.WritePropertyName("content"); Text(writer, message.Text); writer.WriteEndObject(); }
        writer.WriteEndArray(); writer.WriteEndObject();
    });
    private static void Text(Utf8JsonWriter writer, string value) { writer.WriteStartObject(); writer.WriteString("type", "text"); writer.WriteString("text", value); writer.WriteEndObject(); }
}
