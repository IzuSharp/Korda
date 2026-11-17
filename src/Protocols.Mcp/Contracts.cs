using System.Text;

namespace NativeWeave.Server.Mcp;

/// <summary>The explicitly enabled modern stateless MCP protocol revision.</summary>
/// <remarks>Only this revision is supported; no legacy initialization, sessions, replay or event store is enabled.</remarks>
public enum McpProtocolVersion
{
    /// <summary>The stateless 2026-07-28 MCP revision.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Mcp.McpProtocolVersion" path="/member/remarks" />
    V2026_07_28
}

/// <summary>Finite per-endpoint limits and exact authority/origin policy.</summary>
/// <remarks>Options are copied when MapMcp returns. Wire, depth, nodes and decoded strings are independent; native peak admission may reject a body below the wire cap. Host/Origin are validated before managed authorization and before parsing. Missing Origin never bypasses authorization.</remarks>
public sealed class McpEndpointOptions
{
    /// <summary>Gets or sets the exact modern protocol revision.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Mcp.McpEndpointOptions" path="/member/remarks" />
    public McpProtocolVersion ProtocolVersion { get; set; } = McpProtocolVersion.V2026_07_28;
    /// <summary>Gets allowed wire authorities with explicit ports; empty derives exact bound loopback authorities.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Mcp.McpEndpointOptions" path="/member/remarks" />
    public IList<string> AllowedHosts { get; } = new List<string>();
    /// <summary>Gets allowed scheme/host/port origins; empty rejects every request carrying Origin.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Mcp.McpEndpointOptions" path="/member/remarks" />
    public IList<string> AllowedOrigins { get; } = new List<string>();
    /// <summary>Gets or sets maximum HTTP wire bytes; default 1 MiB, subject to the separately charged parse peak.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Mcp.McpEndpointOptions" path="/member/remarks" />
    public int MaxRequestBytes { get; set; } = 1_048_576;
    /// <summary>Gets or sets maximum JSON depth; default and hard maximum 32.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Mcp.McpEndpointOptions" path="/member/remarks" />
    public int MaxJsonDepth { get; set; } = 32;
    /// <summary>Gets or sets the structural node limit; default and hard maximum 4096, including object keys.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Mcp.McpEndpointOptions" path="/member/remarks" />
    public int MaxJsonNodes { get; set; } = 4096;
    /// <summary>Gets or sets aggregate decoded UTF-8 string bytes; default and hard maximum 256 KiB.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Mcp.McpEndpointOptions" path="/member/remarks" />
    public int MaxJsonStringBytes { get; set; } = 262_144;
}

/// <summary>A borrowed call context for one authenticated tool, resource or prompt invocation.</summary>
/// <remarks>Valid until the typed invocation and response work have completed; no new request scope or authentication occurs for the business continuation. Raw body access and admitted-protocol HTTP output are forbidden. Cancellation is intent, not proof that external side effects did not occur.</remarks>
public sealed class McpCallContext
{
    private readonly RequestContext _request;
    private readonly string _name;
    internal McpCallContext(RequestContext request, string name) { _request = request; _name = name; }
    /// <summary>Gets the shared request identity, principal, services and completion/disposal hooks.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Mcp.McpCallContext" path="/member/remarks" />
    public RequestContext RequestContext { get { _request.Alive(); return _request; } }
    /// <summary>Gets the immutable registered item name for this invocation.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Mcp.McpCallContext" path="/member/remarks" />
    public string Name { get { _request.Alive(); return _name; } }
    /// <summary>Gets the shared disconnect, total-deadline and shutdown cancellation intent.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Mcp.McpCallContext" path="/member/remarks" />
    public CancellationToken CancellationToken => RequestContext.RequestAborted;
    /// <summary>Gets the remaining monotonic total request time; reads never reset it.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Mcp.McpCallContext" path="/member/remarks" />
    public TimeSpan TimeRemaining => RequestContext.TimeRemaining;
}

/// <summary>A typed tool execution result, distinct from a JSON-RPC protocol failure.</summary>
/// <typeparam name="T">The statically registered non-null object output DTO.</typeparam>
/// <remarks>Success is serialized with its explicit static metadata into structuredContent and one compatibility JSON text item. The native and managed overlap is charged. Unexpected handler/output failures are redacted tool errors; malformed requests remain protocol errors.</remarks>
public sealed class McpToolResult<T>
{
    internal T? Value { get; }
    internal string? Message { get; }
    internal bool IsError { get; }
    private McpToolResult(T? value, string? message, bool error) { Value = value; Message = message; IsError = error; }
    /// <summary>Creates a non-null object-root success value for the registered output codec.</summary>
    /// <param name="value">The application-owned DTO; keep it unchanged until response encoding completes.</param>
    /// <returns>The typed result; no native operation or serialization occurs here.</returns>
    /// <exception cref="ArgumentNullException">The success value is null.</exception>
    /// <inheritdoc cref="T:NativeWeave.Server.Mcp.McpToolResult`1" path="/member/remarks" />
    public static McpToolResult<T> Success(T value) { ArgumentNullException.ThrowIfNull(value); return new(value, null, false); }
    /// <summary>Creates an explicit bounded tool failure without successful structured content.</summary>
    /// <param name="message">Safe application-selected text, at most 256 KiB UTF-8; do not include secrets.</param>
    /// <returns>A tool failure encoded with isError=true.</returns>
    /// <exception cref="ArgumentException">The text is null, invalid Unicode or exceeds the finite string limit.</exception>
    /// <inheritdoc cref="T:NativeWeave.Server.Mcp.McpToolResult`1" path="/member/remarks" />
    public static McpToolResult<T> Error(string message) { McpValidation.Text(message, 262_144, nameof(message), empty: true); return new(default, message, true); }
}

/// <summary>A bounded fixed-URI text resource result.</summary>
/// <param name="Uri">The exact registered URI.</param>
/// <param name="Text">Application-owned UTF-8 text bounded at reply admission.</param>
/// <param name="MimeType">The exact registered MIME type; default text/plain.</param>
/// <remarks>Immutable descriptor with no native ownership. The adapter rejects a URI or MIME type different from registration; binary/subscription/template resources are outside this profile.</remarks>
public sealed record McpTextResource(string Uri, string Text, string MimeType = "text/plain")
{
    /// <summary>Gets or initializes the exact registered resource URI.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Mcp.McpTextResource" path="/member/remarks" />
    public string Uri { get; init; } = Uri;
    /// <summary>Gets or initializes the bounded application-owned resource text.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Mcp.McpTextResource" path="/member/remarks" />
    public string Text { get; init; } = Text;
    /// <summary>Gets or initializes the exact registered MIME type.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Mcp.McpTextResource" path="/member/remarks" />
    public string MimeType { get; init; } = MimeType;
}
/// <summary>A statically named string prompt argument.</summary>
/// <param name="Name">The unique argument name.</param>
/// <param name="Description">Optional bounded documentation.</param>
/// <param name="Required">Whether callers must supply this argument; default false.</param>
/// <remarks>Unknown, missing required or non-string arguments reject before business dispatch. The registry copies the argument list at registration.</remarks>
public sealed record McpPromptArgument(string Name, string? Description = null, bool Required = false)
{
    /// <summary>Gets or initializes the unique registered argument name.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Mcp.McpPromptArgument" path="/member/remarks" />
    public string Name { get; init; } = Name;
    /// <summary>Gets or initializes optional bounded argument documentation.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Mcp.McpPromptArgument" path="/member/remarks" />
    public string? Description { get; init; } = Description;
    /// <summary>Gets or initializes whether absence rejects before business dispatch.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Mcp.McpPromptArgument" path="/member/remarks" />
    public bool Required { get; init; } = Required;
}
/// <summary>One text-only prompt message.</summary>
/// <param name="Role">Exactly user or assistant.</param>
/// <param name="Text">Bounded application-owned text.</param>
/// <remarks>At most128 messages per result; rich image/audio/resource blocks are outside this initial profile.</remarks>
public sealed record McpPromptMessage(string Role, string Text)
{
    /// <summary>Gets or initializes exactly user or assistant.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Mcp.McpPromptMessage" path="/member/remarks" />
    public string Role { get; init; } = Role;
    /// <summary>Gets or initializes the bounded application-owned message text.</summary>
    /// <inheritdoc cref="T:NativeWeave.Server.Mcp.McpPromptMessage" path="/member/remarks" />
    public string Text { get; init; } = Text;
}

internal static class McpValidation
{
    internal static readonly UTF8Encoding Utf8 = new(false, true);
    internal static void Text(string value, int maximum, string name, bool empty = false)
    {
        ArgumentNullException.ThrowIfNull(value, name);
        if ((!empty && value.Length == 0) || value.Length > maximum || Utf8.GetByteCount(value) > maximum) throw new ArgumentException("The MCP text is outside its finite UTF-8 limit.", name);
    }
    internal static void Name(string value, string name)
    {
        Text(value, 128, name);
        if (value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.'))) throw new ArgumentException("An MCP name uses only ASCII letters, digits, underscore, hyphen or period.", name);
    }
    internal static ArgumentException Schema(string name, Type type, string reason) => new($"NWAPI-S004: tool '{name}' type '{type}' must use a finite static non-null object DTO: {reason}.");
}
