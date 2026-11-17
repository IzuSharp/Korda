# Typed MCP tools on the Rust Server Host

`NativeWeave.Server.Mcp` adds an optional, statically described MCP endpoint to the
existing `NativeWeave.Server` application. Rust rmcp 3.10.0 owns JSON-RPC, modern
request metadata, automatic lists and response framing. C# supplies source-generated
JSON metadata, authorization, typed application handlers and request-scoped services.
Ordinary DTO and handler edits rebuild C# only.

## Install and register a tool

Use the maintainer-provided Linux x64 package feed and the repository's pinned .NET
11 toolchain. The package brings its normal Server dependency and precompiled native
asset; a consumer does not install Cargo or build Rust.

```xml
<PackageReference Include="NativeWeave.Server.Mcp" Version="0.1.0-preview.1" />
```

```csharp
using System.Text.Json.Serialization;
using NativeWeave.Server;
using NativeWeave.Server.Mcp;

var builder = NativeServer.CreateBuilder();
builder.ListenLocalhost(5000);
await using var app = builder.Build();
var registry = new McpRegistry().AddTool(
    "add", "Add two integers", ToolJson.Default.Input, ToolJson.Default.Output,
    static (input, call) => Task.FromResult(
        McpToolResult<Output>.Success(new(input.Left + input.Right))));
app.MapMcp("/mcp", registry);
await app.RunAsync();

internal sealed record Input(int Left, int Right);
internal sealed record Output(int Total);
[JsonSerializable(typeof(Input))]
[JsonSerializable(typeof(Output))]
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
internal partial class ToolJson : JsonSerializerContext;
```

The complete [sample](../../samples/api/protocols-mcp/Program.cs) contains minimal and
advanced modes. Advanced mode adds a fixed text resource, a string-argument prompt
and a sample authenticator/endpoint policy. Its header-based local identity is an
example integration seam; production authentication belongs to the application.

From a restored sample directory:

```sh
dotnet run -- --self-test --minimal
dotnet run -- --self-test
dotnet publish -c Release -r linux-x64 -p:PublishAot=true
./bin/Release/net11.0/linux-x64/publish/Mcp.Sample --self-test
```

The self-test exercises real HTTP/1.1 and HTTP/2 listeners. AOT uses the same explicit
`JsonTypeInfo` objects and precompiled Rust module. The task verifier also copies the
sample outside the repository, restores an exact NuGet closure into a fresh package
cache, places failing Rust-tool traps on PATH, and verifies the unchanged native hash
after a DTO-only edit.

## One endpoint, one authorization gate

`MapMcp` accepts a literal path and freezes a snapshot of the registry and options.
Add all tools, resources and prompts before mapping. Repeated identities, invalid
limits and later registry mutation fail explicitly. A group can contribute ordinary
middleware and authorization policies; HTTP filters and raw body overrides are not
MCP protocol extensions.

Every admitted request first runs the same middleware, authenticator and endpoint
policy, including `tools/list`, `resources/list` and `prompts/list`. Rust does not
poll the application body or enter rmcp until the awaited innermost `next` allows
it. A denial can set HTTP status/headers and write a bounded rejection body. Calling
`next` after configuring or starting that denial fails and preserves the denial.
Throwing, short-circuiting or canceling before allow never enables native parsing.

For a business method, the native continuation uses the same ticket, `RequestContext`,
principal, deadline and optional DI scope. Middleware runs once. `McpCallContext`
provides its registered name, request context, cancellation token and remaining time.
Await application work; the borrowed context expires after dispatch cleanup.

Raw request-body APIs are unavailable throughout MCP. After allow, rmcp owns output:
raw status, headers, trailers, writes and `OnStarting` are unavailable. `OnCompleted`
and registered disposal hooks still run during the single request cleanup. If a handler
ignores cancellation, `CloseAsync` can return `Pending` with its owners/credits and
scope still retained. `DisposeAsync` joins actual retirement. A completed Task is not
permission to release a native owner whose retirement join has not completed.

## Exact initial protocol profile

| Concern | Enabled behavior |
| --- | --- |
| Version | Only `McpProtocolVersion.V2026_07_28` |
| Transport | Modern stateless POST with JSON responses |
| GET / DELETE | HTTP 405; no event store, replay or legacy sessions |
| Request headers | JSON Content-Type; Accept includes JSON and event-stream; exact `MCP-Protocol-Version`, `Mcp-Method`, and applicable `Mcp-Name` |
| Request metadata | `_meta.io.modelcontextprotocol/protocolVersion` agrees with the header; `_meta.io.modelcontextprotocol/clientCapabilities` is required |
| Host | Original wire authority, including an explicit port; forwarded host cannot replace it |
| Origin | Explicit exact HTTP(S) scheme/host/port allowlist; absence still runs authentication |
| Limits | At most 1 MiB wire, depth 32, 4,096 structural nodes, 256 KiB decoded strings; endpoint options may lower these limits |

Empty `AllowedHosts` derives the actual bound loopback authorities. Non-loopback
listeners require explicit allowed hosts, such as `api.example.com:443`. An allowed
origin is an exact value such as `https://app.example.com:443`; default ports remain
explicit. Host/Origin/content-type/Accept checks precede the managed gate. rmcp then
interprets modern method/name/metadata consistency using its public transport.
Origin protection is independent of application authentication.

## Static schema and error behavior

Both tool roots must be non-null object DTOs from the explicitly supplied
source-generated context. Scalar, collection, dictionary, open-object and CLR
`Nullable<T>` roots fail with `NWAPI-S004`. Root normalization changes only an exported
object or object/null root to object. Nested required/nullable properties retain their
BCL schema semantics.

The finite nested subset includes static objects, arrays/`List<T>`, BCL scalar
codecs and numeric/string enums. A metadata prewalk checks at most 64 distinct types,
256 total properties, depth 16 and 16 KiB of property names. Enum names/rename
attributes are bounded to 256 fields per enum and included in the constant budget.
Recursive/open JSON, dictionaries, custom converters, unknown exporter attribute
constructors and unregistered polymorphism fail registration. The prewalk resolves
only types reachable through the supplied static context. It does not scan assemblies.
Schema export happens once, under a separate startup scratch grant; each encoded
schema is at most 64 KiB and aggregate registry metadata at most 4 MiB.

`get_tool` returns the exact registered schema. Native finite-shape checks and the
static C# decoder reject invalid arguments before a typed callback. This adapter does
not claim to be an arbitrary JSON Schema validator. Application business validation
remains explicit, and tool annotations never grant authorization.

| Failure | Result |
| --- | --- |
| Header/authority/body/structural admission | Bounded HTTP failure before SDK/business admission |
| Modern metadata conflict, unknown method/tool, bad arguments | Native JSON-RPC/MCP protocol error |
| `McpToolResult<T>.Error(message)` | Tool result with `isError: true` |
| Unexpected tool exception, invalid output DTO or oversized output | Redacted tool error, no successful `structuredContent` |
| Invalid resource URI/MIME or prompt role/count | Redacted application protocol error |
| Cancellation / disconnect / deadline | Abort and truthful ownership retirement; no replay/session continuation |

A successful tool result contains both `structuredContent` and one text item whose
text is the same JSON value. Resources use their registered URI/MIME exactly. Prompts
accept only known declared string arguments, check required names, and return at most
128 user/assistant text messages. Binary resources, templates, subscriptions and rich
prompt content require a separately specified profile.

## Memory, copies and verification

A 1 MiB wire limit alone does not bound decoded JSON or a DTO graph. Before public
rmcp parsing, the native Host performs a budgeted serde visitor scan and reserves
separate native and managed live-overlap peaks. All byte-credit admission is
nonwaiting. An otherwise valid request can receive HTTP 503 if its peak cannot fit
4 MiB per request/side or the Host's global budget.

There is one native input collector, the SDK's own parser/tree, one owned UTF-8
argument bridge and one static C# materialization. Output performs static serialization,
structured/text compatibility encoding and native SDK model/response construction.
The output reservation is granted before SDK/business entry and moves through the
reply operation to the actual network body. It is not freed at an early terminal
notification. The [allocation proof and fixtures](../../tests/Protocols.Mcp/allocation-proof.md)
record exact source/layout assumptions, formulas, measured cases and their limits.
These credits cover controlled payload ownership; they are not process RSS, allocator
fragmentation, JIT/static runtime caches or arbitrary allocations by application
constructors/handlers/getters.

The Linux verifier checks the exact 65-symbol API and compiled XML/reference, static
consumer compilation, native hostile-input/zero-admission tests, real JIT and Native
AOT HTTP/1+HTTP/2 behavior, separate native and managed retirement witnesses, and an
independent NuGet consumer. This task's receipt covers Linux x64 only. It does not
claim Windows or final pristine multi-package A16/B24 acceptance.
