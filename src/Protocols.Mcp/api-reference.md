# MCP API reference

Generated from the actual compiled public API and XML; compared against the exact pinned design declarations. B19 owns the finite typed MCP facade; rmcp owns protocol dispatch on the existing B17 Host.

## `F:NativeWeave.Server.Mcp.McpProtocolVersion.V2026_07_28`

```csharp
NativeWeave.Server.Mcp.McpProtocolVersion.V2026_07_28 = 0
```

The stateless 2026-07-28 MCP revision.

Only this revision is supported; no legacy initialization, sessions, replay or event store is enabled.


## `M:NativeWeave.Server.Mcp.McpEndpointExtensions.MapMcp(NativeWeave.Server.ServerRoutes,System.String,NativeWeave.Server.Mcp.McpRegistry,NativeWeave.Server.Mcp.McpEndpointOptions)`

```csharp
public static NativeWeave.Server.ServerEndpointBuilder NativeWeave.Server.Mcp.McpEndpointExtensions.MapMcp(NativeWeave.Server.ServerRoutes routes, System.String pattern, NativeWeave.Server.Mcp.McpRegistry registry, [NativeWeave.Server.Mcp.McpEndpointOptions? options = null])
```

Maps a literal MCP mount path and freezes the registry and copied endpoint options.

Raw input is always unavailable. Before allow, middleware may configure a denial response; calling next afterward rejects. After allow, rmcp exclusively owns response framing, while OnCompleted/disposal hooks remain available. Empty AllowedHosts derives exact bound loopback authorities; non-loopback listeners require an explicit allowlist.

- **routes**: The application or route group configured before startup.
- **pattern**: A literal route without placeholders or wildcards.
- **registry**: Finite static tools/resources/prompts; later mutation fails.
- **options**: Optional exact modern protocol limits and authority/origin policy, copied now.
- **returns**: The endpoint builder for names, generic timeout and authorization policies.
- **T:System.ArgumentException**: Path, limits, authority/origin or registry metadata is invalid.
- **T:System.InvalidOperationException**: The route registry is frozen or an HTTP filter/body override is inherited.

## `M:NativeWeave.Server.Mcp.McpPromptArgument.#ctor(System.String,System.String,System.Boolean)`

```csharp
public NativeWeave.Server.Mcp.McpPromptArgument.McpPromptArgument(System.String Name, [System.String? Description = null], [System.Boolean Required = false])
```

A statically named string prompt argument.

Unknown, missing required or non-string arguments reject before business dispatch. The registry copies the argument list at registration.

- **Name**: The unique argument name.
- **Description**: Optional bounded documentation.
- **Required**: Whether callers must supply this argument; default false.

## `M:NativeWeave.Server.Mcp.McpPromptMessage.#ctor(System.String,System.String)`

```csharp
public NativeWeave.Server.Mcp.McpPromptMessage.McpPromptMessage(System.String Role, System.String Text)
```

One text-only prompt message.

At most128 messages per result; rich image/audio/resource blocks are outside this initial profile.

- **Role**: Exactly user or assistant.
- **Text**: Bounded application-owned text.

## `M:NativeWeave.Server.Mcp.McpRegistry.#ctor`

```csharp
public NativeWeave.Server.Mcp.McpRegistry.McpRegistry()
```

Creates an empty registry without opening native resources.

Configuration is single threaded. MapMcp freezes registration; handlers and metadata remain rooted until the owning application retires. At most1024 entries of each kind and4MiB aggregate metadata are admitted; each schema is at most64KiB. Ordinary DTO/handler changes only rebuild C#. The authenticated endpoint policy applies to every automatic list and business call.


## `M:NativeWeave.Server.Mcp.McpRegistry.AddPrompt(System.String,System.String,System.Collections.Generic.IReadOnlyList{NativeWeave.Server.Mcp.McpPromptArgument},System.Func{System.Collections.Generic.IReadOnlyDictionary{System.String,System.String},NativeWeave.Server.Mcp.McpCallContext,System.Threading.Tasks.Task{System.Collections.Generic.IReadOnlyList{NativeWeave.Server.Mcp.McpPromptMessage}}})`

```csharp
public NativeWeave.Server.Mcp.McpRegistry NativeWeave.Server.Mcp.McpRegistry.AddPrompt(System.String name, System.String description, System.Collections.Generic.IReadOnlyList<NativeWeave.Server.Mcp.McpPromptArgument> arguments, System.Func<System.Collections.Generic.IReadOnlyDictionary<System.String, System.String>, NativeWeave.Server.Mcp.McpCallContext, System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<NativeWeave.Server.Mcp.McpPromptMessage>>> handler)
```

Registers one named text prompt with a copied finite string-argument description.

Unknown, missing required and non-string input rejects before invocation. Results contain at most128 user/assistant text messages; all automatic metadata methods use the same endpoint authorization.

- **name**: Unique finite prompt name.
- **description**: Bounded prompt documentation.
- **arguments**: Unique string arguments and required flags, copied immediately.
- **handler**: Typed callback receiving only validated supplied strings.
- **returns**: This registry before freeze.
- **T:System.ArgumentException**: Metadata/argument names are invalid or duplicated.
- **T:System.InvalidOperationException**: Registration is frozen.

## `M:NativeWeave.Server.Mcp.McpRegistry.AddResource(System.String,System.String,System.String,System.Func{NativeWeave.Server.Mcp.McpCallContext,System.Threading.Tasks.Task{NativeWeave.Server.Mcp.McpTextResource}},System.String)`

```csharp
public NativeWeave.Server.Mcp.McpRegistry NativeWeave.Server.Mcp.McpRegistry.AddResource(System.String uri, System.String name, System.String mimeType, System.Func<NativeWeave.Server.Mcp.McpCallContext, System.Threading.Tasks.Task<NativeWeave.Server.Mcp.McpTextResource>> handler, [System.String? description = null])
```

Registers one fixed-URI text resource.

Binary data, templates and subscriptions are not enabled. Returned URI/MIME must exactly match registration; native and managed UTF-8/JSON overlap is admitted before encoding.

- **uri**: Unique absolute resource URI, at most4096 UTF-8 bytes.
- **name**: Finite resource name.
- **mimeType**: Exact MIME type required on every returned resource.
- **handler**: Typed handler sharing the authenticated request scope.
- **description**: Optional bounded documentation.
- **returns**: This registry before freeze.
- **T:System.ArgumentException**: Metadata is invalid or duplicated.
- **T:System.InvalidOperationException**: Registration is frozen.

## `M:NativeWeave.Server.Mcp.McpRegistry.AddTool``2(System.String,System.String,System.Text.Json.Serialization.Metadata.JsonTypeInfo{``0},System.Text.Json.Serialization.Metadata.JsonTypeInfo{``1},System.Func{``0,NativeWeave.Server.Mcp.McpCallContext,System.Threading.Tasks.Task{NativeWeave.Server.Mcp.McpToolResult{``1}}})`

```csharp
public NativeWeave.Server.Mcp.McpRegistry NativeWeave.Server.Mcp.McpRegistry.AddTool<TInput, TOutput>(System.String name, System.String description, System.Text.Json.Serialization.Metadata.JsonTypeInfo<TInput> inputTypeInfo, System.Text.Json.Serialization.Metadata.JsonTypeInfo<TOutput> outputTypeInfo, System.Func<TInput, NativeWeave.Server.Mcp.McpCallContext, System.Threading.Tasks.Task<NativeWeave.Server.Mcp.McpToolResult<TOutput>>> handler)
```

Registers one typed tool using explicit source-generated input/output JSON metadata.

Schema export runs once under startup scratch admission. Root-only object normalization preserves nested nullability. Successful results produce both structuredContent and one JSON text item; unexpected business/output exceptions produce a redacted tool error.

- **TInput**: A non-null statically mapped object input DTO.
- **TOutput**: A non-null statically mapped object output DTO.
- **name**: Unique finite ASCII tool name.
- **description**: Bounded tool documentation.
- **inputTypeInfo**: Explicit static input metadata; no reflection fallback.
- **outputTypeInfo**: Explicit static output metadata; no reflection fallback.
- **handler**: Typed callback invoked after one shared authorization gate and input validation.
- **returns**: This registry for further registration before MapMcp.
- **T:System.ArgumentException**: The identity/schema is invalid, duplicate or outside the finite subset; schema failures identify NWAPI-S004.
- **T:System.InvalidOperationException**: Registration is already frozen.

## `M:NativeWeave.Server.Mcp.McpTextResource.#ctor(System.String,System.String,System.String)`

```csharp
public NativeWeave.Server.Mcp.McpTextResource.McpTextResource(System.String Uri, System.String Text, [System.String MimeType = "text/plain"])
```

A bounded fixed-URI text resource result.

Immutable descriptor with no native ownership. The adapter rejects a URI or MIME type different from registration; binary/subscription/template resources are outside this profile.

- **Uri**: The exact registered URI.
- **Text**: Application-owned UTF-8 text bounded at reply admission.
- **MimeType**: The exact registered MIME type; default text/plain.

## `M:NativeWeave.Server.Mcp.McpToolResult`1.Error(System.String)`

```csharp
public static NativeWeave.Server.Mcp.McpToolResult<T> NativeWeave.Server.Mcp.McpToolResult<T>.Error(System.String message)
```

Creates an explicit bounded tool failure without successful structured content.

Success is serialized with its explicit static metadata into structuredContent and one compatibility JSON text item. The native and managed overlap is charged. Unexpected handler/output failures are redacted tool errors; malformed requests remain protocol errors.

- **message**: Safe application-selected text, at most 256 KiB UTF-8; do not include secrets.
- **returns**: A tool failure encoded with isError=true.
- **T:System.ArgumentException**: The text is null, invalid Unicode or exceeds the finite string limit.

## `M:NativeWeave.Server.Mcp.McpToolResult`1.Success(`0)`

```csharp
public static NativeWeave.Server.Mcp.McpToolResult<T> NativeWeave.Server.Mcp.McpToolResult<T>.Success(T value)
```

Creates a non-null object-root success value for the registered output codec.

Success is serialized with its explicit static metadata into structuredContent and one compatibility JSON text item. The native and managed overlap is charged. Unexpected handler/output failures are redacted tool errors; malformed requests remain protocol errors.

- **value**: The application-owned DTO; keep it unchanged until response encoding completes.
- **returns**: The typed result; no native operation or serialization occurs here.
- **T:System.ArgumentNullException**: The success value is null.

## `P:NativeWeave.Server.Mcp.McpCallContext.CancellationToken`

```csharp
public System.Threading.CancellationToken NativeWeave.Server.Mcp.McpCallContext.CancellationToken | getter=Public;setter=;init=
```

Gets the shared disconnect, total-deadline and shutdown cancellation intent.

Valid until the typed invocation and response work have completed; no new request scope or authentication occurs for the business continuation. Raw body access and admitted-protocol HTTP output are forbidden. Cancellation is intent, not proof that external side effects did not occur.


## `P:NativeWeave.Server.Mcp.McpCallContext.Name`

```csharp
public System.String NativeWeave.Server.Mcp.McpCallContext.Name | getter=Public;setter=;init=
```

Gets the immutable registered item name for this invocation.

Valid until the typed invocation and response work have completed; no new request scope or authentication occurs for the business continuation. Raw body access and admitted-protocol HTTP output are forbidden. Cancellation is intent, not proof that external side effects did not occur.


## `P:NativeWeave.Server.Mcp.McpCallContext.RequestContext`

```csharp
public NativeWeave.Server.RequestContext NativeWeave.Server.Mcp.McpCallContext.RequestContext | getter=Public;setter=;init=
```

Gets the shared request identity, principal, services and completion/disposal hooks.

Valid until the typed invocation and response work have completed; no new request scope or authentication occurs for the business continuation. Raw body access and admitted-protocol HTTP output are forbidden. Cancellation is intent, not proof that external side effects did not occur.


## `P:NativeWeave.Server.Mcp.McpCallContext.TimeRemaining`

```csharp
public System.TimeSpan NativeWeave.Server.Mcp.McpCallContext.TimeRemaining | getter=Public;setter=;init=
```

Gets the remaining monotonic total request time; reads never reset it.

Valid until the typed invocation and response work have completed; no new request scope or authentication occurs for the business continuation. Raw body access and admitted-protocol HTTP output are forbidden. Cancellation is intent, not proof that external side effects did not occur.


## `P:NativeWeave.Server.Mcp.McpEndpointOptions.AllowedHosts`

```csharp
public System.Collections.Generic.IList<System.String> NativeWeave.Server.Mcp.McpEndpointOptions.AllowedHosts | getter=Public;setter=;init=
```

Gets allowed wire authorities with explicit ports; empty derives exact bound loopback authorities.

Options are copied when MapMcp returns. Wire, depth, nodes and decoded strings are independent; native peak admission may reject a body below the wire cap. Host/Origin are validated before managed authorization and before parsing. Missing Origin never bypasses authorization.


## `P:NativeWeave.Server.Mcp.McpEndpointOptions.AllowedOrigins`

```csharp
public System.Collections.Generic.IList<System.String> NativeWeave.Server.Mcp.McpEndpointOptions.AllowedOrigins | getter=Public;setter=;init=
```

Gets allowed scheme/host/port origins; empty rejects every request carrying Origin.

Options are copied when MapMcp returns. Wire, depth, nodes and decoded strings are independent; native peak admission may reject a body below the wire cap. Host/Origin are validated before managed authorization and before parsing. Missing Origin never bypasses authorization.


## `P:NativeWeave.Server.Mcp.McpEndpointOptions.MaxJsonDepth`

```csharp
public System.Int32 NativeWeave.Server.Mcp.McpEndpointOptions.MaxJsonDepth | getter=Public;setter=Public;init=False
```

Gets or sets maximum JSON depth; default and hard maximum 32.

Options are copied when MapMcp returns. Wire, depth, nodes and decoded strings are independent; native peak admission may reject a body below the wire cap. Host/Origin are validated before managed authorization and before parsing. Missing Origin never bypasses authorization.


## `P:NativeWeave.Server.Mcp.McpEndpointOptions.MaxJsonNodes`

```csharp
public System.Int32 NativeWeave.Server.Mcp.McpEndpointOptions.MaxJsonNodes | getter=Public;setter=Public;init=False
```

Gets or sets the structural node limit; default and hard maximum 4096, including object keys.

Options are copied when MapMcp returns. Wire, depth, nodes and decoded strings are independent; native peak admission may reject a body below the wire cap. Host/Origin are validated before managed authorization and before parsing. Missing Origin never bypasses authorization.


## `P:NativeWeave.Server.Mcp.McpEndpointOptions.MaxJsonStringBytes`

```csharp
public System.Int32 NativeWeave.Server.Mcp.McpEndpointOptions.MaxJsonStringBytes | getter=Public;setter=Public;init=False
```

Gets or sets aggregate decoded UTF-8 string bytes; default and hard maximum 256 KiB.

Options are copied when MapMcp returns. Wire, depth, nodes and decoded strings are independent; native peak admission may reject a body below the wire cap. Host/Origin are validated before managed authorization and before parsing. Missing Origin never bypasses authorization.


## `P:NativeWeave.Server.Mcp.McpEndpointOptions.MaxRequestBytes`

```csharp
public System.Int32 NativeWeave.Server.Mcp.McpEndpointOptions.MaxRequestBytes | getter=Public;setter=Public;init=False
```

Gets or sets maximum HTTP wire bytes; default 1 MiB, subject to the separately charged parse peak.

Options are copied when MapMcp returns. Wire, depth, nodes and decoded strings are independent; native peak admission may reject a body below the wire cap. Host/Origin are validated before managed authorization and before parsing. Missing Origin never bypasses authorization.


## `P:NativeWeave.Server.Mcp.McpEndpointOptions.ProtocolVersion`

```csharp
public NativeWeave.Server.Mcp.McpProtocolVersion NativeWeave.Server.Mcp.McpEndpointOptions.ProtocolVersion | getter=Public;setter=Public;init=False
```

Gets or sets the exact modern protocol revision.

Options are copied when MapMcp returns. Wire, depth, nodes and decoded strings are independent; native peak admission may reject a body below the wire cap. Host/Origin are validated before managed authorization and before parsing. Missing Origin never bypasses authorization.


## `P:NativeWeave.Server.Mcp.McpPromptArgument.Description`

```csharp
public System.String? NativeWeave.Server.Mcp.McpPromptArgument.Description | getter=Public;setter=Public;init=True
```

Gets or initializes optional bounded argument documentation.

Unknown, missing required or non-string arguments reject before business dispatch. The registry copies the argument list at registration.


## `P:NativeWeave.Server.Mcp.McpPromptArgument.Name`

```csharp
public System.String NativeWeave.Server.Mcp.McpPromptArgument.Name | getter=Public;setter=Public;init=True
```

Gets or initializes the unique registered argument name.

Unknown, missing required or non-string arguments reject before business dispatch. The registry copies the argument list at registration.


## `P:NativeWeave.Server.Mcp.McpPromptArgument.Required`

```csharp
public System.Boolean NativeWeave.Server.Mcp.McpPromptArgument.Required | getter=Public;setter=Public;init=True
```

Gets or initializes whether absence rejects before business dispatch.

Unknown, missing required or non-string arguments reject before business dispatch. The registry copies the argument list at registration.


## `P:NativeWeave.Server.Mcp.McpPromptMessage.Role`

```csharp
public System.String NativeWeave.Server.Mcp.McpPromptMessage.Role | getter=Public;setter=Public;init=True
```

Gets or initializes exactly user or assistant.

At most128 messages per result; rich image/audio/resource blocks are outside this initial profile.


## `P:NativeWeave.Server.Mcp.McpPromptMessage.Text`

```csharp
public System.String NativeWeave.Server.Mcp.McpPromptMessage.Text | getter=Public;setter=Public;init=True
```

Gets or initializes the bounded application-owned message text.

At most128 messages per result; rich image/audio/resource blocks are outside this initial profile.


## `P:NativeWeave.Server.Mcp.McpTextResource.MimeType`

```csharp
public System.String NativeWeave.Server.Mcp.McpTextResource.MimeType | getter=Public;setter=Public;init=True
```

Gets or initializes the exact registered MIME type.

Immutable descriptor with no native ownership. The adapter rejects a URI or MIME type different from registration; binary/subscription/template resources are outside this profile.


## `P:NativeWeave.Server.Mcp.McpTextResource.Text`

```csharp
public System.String NativeWeave.Server.Mcp.McpTextResource.Text | getter=Public;setter=Public;init=True
```

Gets or initializes the bounded application-owned resource text.

Immutable descriptor with no native ownership. The adapter rejects a URI or MIME type different from registration; binary/subscription/template resources are outside this profile.


## `P:NativeWeave.Server.Mcp.McpTextResource.Uri`

```csharp
public System.String NativeWeave.Server.Mcp.McpTextResource.Uri | getter=Public;setter=Public;init=True
```

Gets or initializes the exact registered resource URI.

Immutable descriptor with no native ownership. The adapter rejects a URI or MIME type different from registration; binary/subscription/template resources are outside this profile.


## `T:NativeWeave.Server.Mcp.McpCallContext`

```csharp
NativeWeave.Server.Mcp.McpCallContext | kind=Class;static=False;sealed=True;abstract=False;base=System.Object;interfaces=
```

A borrowed call context for one authenticated tool, resource or prompt invocation.

Valid until the typed invocation and response work have completed; no new request scope or authentication occurs for the business continuation. Raw body access and admitted-protocol HTTP output are forbidden. Cancellation is intent, not proof that external side effects did not occur.


## `T:NativeWeave.Server.Mcp.McpEndpointExtensions`

```csharp
NativeWeave.Server.Mcp.McpEndpointExtensions | kind=Class;static=True;sealed=False;abstract=False;base=System.Object;interfaces=
```

Maps a frozen optional rmcp endpoint onto the existing Rust Server Host.

One pre-parse authorization gate covers automatic methods and typed calls. The same request, principal, optional scope and deadline survive one nested business continuation. GET/DELETE return405; legacy sessions, event stores and replay are disabled. This package adds no second server runtime.


## `T:NativeWeave.Server.Mcp.McpEndpointOptions`

```csharp
NativeWeave.Server.Mcp.McpEndpointOptions | kind=Class;static=False;sealed=True;abstract=False;base=System.Object;interfaces=
```

Finite per-endpoint limits and exact authority/origin policy.

Options are copied when MapMcp returns. Wire, depth, nodes and decoded strings are independent; native peak admission may reject a body below the wire cap. Host/Origin are validated before managed authorization and before parsing. Missing Origin never bypasses authorization.


## `T:NativeWeave.Server.Mcp.McpPromptArgument`

```csharp
NativeWeave.Server.Mcp.McpPromptArgument | kind=Class;static=False;sealed=True;abstract=False;base=System.Object;interfaces=System.IEquatable<NativeWeave.Server.Mcp.McpPromptArgument>
```

A statically named string prompt argument.

Unknown, missing required or non-string arguments reject before business dispatch. The registry copies the argument list at registration.

- **Name**: The unique argument name.
- **Description**: Optional bounded documentation.
- **Required**: Whether callers must supply this argument; default false.

## `T:NativeWeave.Server.Mcp.McpPromptMessage`

```csharp
NativeWeave.Server.Mcp.McpPromptMessage | kind=Class;static=False;sealed=True;abstract=False;base=System.Object;interfaces=System.IEquatable<NativeWeave.Server.Mcp.McpPromptMessage>
```

One text-only prompt message.

At most128 messages per result; rich image/audio/resource blocks are outside this initial profile.

- **Role**: Exactly user or assistant.
- **Text**: Bounded application-owned text.

## `T:NativeWeave.Server.Mcp.McpProtocolVersion`

```csharp
NativeWeave.Server.Mcp.McpProtocolVersion | kind=Enum;static=False;sealed=True;abstract=False;base=System.Enum;interfaces=
```

The explicitly enabled modern stateless MCP protocol revision.

Only this revision is supported; no legacy initialization, sessions, replay or event store is enabled.


## `T:NativeWeave.Server.Mcp.McpRegistry`

```csharp
NativeWeave.Server.Mcp.McpRegistry | kind=Class;static=False;sealed=True;abstract=False;base=System.Object;interfaces=
```

Registers finite static tools, fixed text resources and string-argument prompts.

Configuration is single threaded. MapMcp freezes registration; handlers and metadata remain rooted until the owning application retires. At most1024 entries of each kind and4MiB aggregate metadata are admitted; each schema is at most64KiB. Ordinary DTO/handler changes only rebuild C#. The authenticated endpoint policy applies to every automatic list and business call.


## `T:NativeWeave.Server.Mcp.McpTextResource`

```csharp
NativeWeave.Server.Mcp.McpTextResource | kind=Class;static=False;sealed=True;abstract=False;base=System.Object;interfaces=System.IEquatable<NativeWeave.Server.Mcp.McpTextResource>
```

A bounded fixed-URI text resource result.

Immutable descriptor with no native ownership. The adapter rejects a URI or MIME type different from registration; binary/subscription/template resources are outside this profile.

- **Uri**: The exact registered URI.
- **Text**: Application-owned UTF-8 text bounded at reply admission.
- **MimeType**: The exact registered MIME type; default text/plain.

## `T:NativeWeave.Server.Mcp.McpToolResult`1`

```csharp
NativeWeave.Server.Mcp.McpToolResult<T> | kind=Class;static=False;sealed=True;abstract=False;base=System.Object;interfaces=
```

A typed tool execution result, distinct from a JSON-RPC protocol failure.

Success is serialized with its explicit static metadata into structuredContent and one compatibility JSON text item. The native and managed overlap is charged. Unexpected handler/output failures are redacted tool errors; malformed requests remain protocol errors.

- **T**: The statically registered non-null object output DTO.

