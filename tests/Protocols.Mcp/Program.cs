using System.Net;
using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using NativeWeave.Server;
using NativeWeave.Server.Mcp;

int assertions = 0, calls = 0, middleware = 0, completed = 0;
void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); Interlocked.Increment(ref assertions); Console.WriteLine("PASS " + name); }
void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { Interlocked.Increment(ref assertions); return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
SchemaFixtures.Run(Check);
var schema = StaticSchema.Create("echo", Json.Default.Input);
using (var document = JsonDocument.Parse(schema))
{
    Check(document.RootElement.GetProperty("type").GetString() == "object", "Root-only schema normalization");
    Check(document.RootElement.GetProperty("properties").GetProperty("optional").GetProperty("type").EnumerateArray().Any(t => t.GetString() == "null"), "Nested nullability preserved");
}
Throws<ArgumentException>(() => new McpRegistry().AddTool("scalar", "", Json.Default.String, Json.Default.Output, static (_, _) => Task.FromResult(McpToolResult<Output>.Success(new("x")))));
Throws<ArgumentException>(() => new McpRegistry().AddTool("nullable", "", Json.Default.NullableValueInput, Json.Default.Output, static (_, _) => Task.FromResult(McpToolResult<Output>.Success(new("x")))));
Throws<ArgumentNullException>(() => McpToolResult<Output>.Success(null!));
int retirements = 0;
var escaped = new ConcurrentBag<RequestContext>();
var retirementFailures = new ConcurrentQueue<string>();
foreach (var version in new[] { HttpVersion.Version11, HttpVersion.Version20 })
{
    calls = middleware = completed = 0;
    var registry = new McpRegistry().AddTool("echo", "Echo typed input", Json.Default.Input, Json.Default.Output, (input, context) =>
    {
        Interlocked.Increment(ref calls); Check(context.Name == "echo" && context.RequestContext.User.Identity?.IsAuthenticated == true, "One authenticated typed context");
        Throws<InvalidOperationException>(() => _ = context.RequestContext.Request.Body);
        Throws<InvalidOperationException>(() => _ = context.RequestContext.Response.Headers);
        Throws<InvalidOperationException>(() => _ = context.RequestContext.Response.Trailers);
        Throws<InvalidOperationException>(() => context.RequestContext.Response.StatusCode = 201);
        Throws<InvalidOperationException>(() => context.RequestContext.Response.OnStarting(static _ => Task.CompletedTask));
        Check(ReferenceEquals(context.RequestContext.Items["gate-context"], context.RequestContext) && ReferenceEquals(context.RequestContext.Items["gate-scope"], context.RequestContext.Services.GetRequiredService<ScopeProbe>()), "Gate and nested business share exactly one context and DI scope");
        escaped.Add(context.RequestContext);
        return Task.FromResult(McpToolResult<Output>.Success(new(input.Text + "!")));
    }).AddTool("failure", "Explicit error", Json.Default.Input, Json.Default.Output, static (_, _) => Task.FromResult(McpToolResult<Output>.Error("Safe selected failure")))
        .AddTool("throw", "Unexpected error", Json.Default.Input, Json.Default.Output, static (_, _) => throw new InvalidOperationException("secret exception payload"))
        .AddTool("badoutput", "Invalid non-nullable output", Json.Default.Input, Json.Default.Output, static (_, _) => Task.FromResult(McpToolResult<Output>.Success(new(null!))))
        .AddTool("largeoutput", "Bounded output", Json.Default.Input, Json.Default.Output, static (_, _) => Task.FromResult(McpToolResult<Output>.Success(new(new string('x', 300_000)))))
        .AddTool("empty", "Empty static object", SchemaJson.Default.Empty, SchemaJson.Default.Empty, static (_, _) => Task.FromResult(McpToolResult<Empty>.Success(new())))
        .AddTool("enum", "Static string enum", StringEnumJson.Default.EnumDto, StringEnumJson.Default.EnumDto, static (value, _) => Task.FromResult(McpToolResult<EnumDto>.Success(value)))
        .AddResource("nativeweave:wrong", "wrong", "text/plain", static _ => Task.FromResult(new McpTextResource("nativeweave:different", "secret mismatch")))
        .AddPrompt("wrongrole", "Invalid output", [], static (_, _) => Task.FromResult<IReadOnlyList<McpPromptMessage>>([new("system", "secret system message")]))
        .AddPrompt("toomany", "Invalid output count", [], static (_, _) => Task.FromResult<IReadOnlyList<McpPromptMessage>>(Enumerable.Repeat(new McpPromptMessage("user", "x"), 129).ToArray()))
        .AddResource("nativeweave:readme", "readme", "text/plain", static _ => Task.FromResult(new McpTextResource("nativeweave:readme", "Native text")))
        .AddPrompt("greet", "Greeting", [new("name", Required: true)], static (args, _) => Task.FromResult<IReadOnlyList<McpPromptMessage>>([new("assistant", "Hello " + args["name"])]));
    var builder = NativeServer.CreateBuilder(); builder.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = version == HttpVersion.Version20 ? ServerProtocols.Http2 : ServerProtocols.Http1);
    builder.Options.RequestTimeout = TimeSpan.FromSeconds(4); builder.Options.ShutdownTimeout = TimeSpan.FromSeconds(2);
    builder.UseAuthentication(new Auth()).UseAuthorization(new Authorize());
    builder.Services.AddScoped(_ => new ScopeProbe());
    int createdBefore = ScopeProbe.Created, disposedBefore = ScopeProbe.Disposed;
    await using var app = builder.Build();
    app.Use(async (context, next, token) =>
    {
        var engine = ((NativeRequest)context.Native).Engine;
        engine.TestRetirement = snapshot =>
        {
            if (!(snapshot.Released && snapshot.NativeQuiet && !snapshot.RootLive && snapshot.Controls == 0 && snapshot.CallbackEntries == 0 && snapshot.RetainedInputBytes == 0)) retirementFailures.Enqueue("An observed A07 retirement retained a root/control/input or callback entry.");
            Interlocked.Increment(ref retirements);
        };
        context.Items["gate-context"] = context; context.Items["gate-scope"] = context.Services.GetRequiredService<ScopeProbe>();
        Interlocked.Increment(ref middleware); context.Response.OnCompleted(() => { Interlocked.Increment(ref completed); return Task.CompletedTask; });
        Throws<InvalidOperationException>(() => _ = context.Request.Body);
        if (context.Request.Path == "/short") { context.Response.StatusCode = 202; context.Response.Headers.Set("X-Denied", "chosen"); return; }
        if (context.Request.Path == "/configured") { context.Response.StatusCode = 203; context.Response.Headers.Set("X-Denied", "configured"); try { await next(context, token); } catch (InvalidOperationException) { Check(true, "Pending denial configuration rejects later allow"); return; } throw new InvalidOperationException("Allow admitted configured denial"); }
        if (context.Request.Path == "/written") { context.Response.StatusCode = 202; context.Response.Headers.Set("X-Denied", "written"); await context.Response.WriteAsync("denied body"u8.ToArray(), token); try { await next(context, token); } catch (InvalidOperationException) { Check(true, "Started denial rejects later allow"); return; } throw new InvalidOperationException("Allow admitted started denial"); }
        if (context.Request.Path == "/throwgate") throw new InvalidOperationException("secret gate payload");
        await next(context, token);
    });
    app.MapMcp("/mcp", registry).RequireAuthorization("authenticated");
    app.MapMcp("/short", new McpRegistry().AddTool("echo", "", Json.Default.Input, Json.Default.Output, static (_, _) => throw new Exception("short circuit entered")));
    foreach (string path in new[] { "/configured", "/written", "/throwgate" }) app.MapMcp(path, new McpRegistry());
    Throws<InvalidOperationException>(() => registry.AddResource("nativeweave:late", "late", "text/plain", static _ => throw new Exception()));
    await app.StartAsync();
    using var client = new HttpClient { BaseAddress = app.BoundAddresses.Single(), Timeout = TimeSpan.FromSeconds(8), DefaultRequestVersion = version, DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact };
    async Task<(int Status, string Body, string? Denied)> Send(string method, string fields = "", string? name = null, bool auth = true, string path = "/mcp", Action<HttpRequestMessage>? configure = null)
    {
        string meta = "\"_meta\":{\"io.modelcontextprotocol/protocolVersion\":\"2026-07-28\",\"io.modelcontextprotocol/clientCapabilities\":{}}";
        string body = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"" + method + "\",\"params\":{" + fields + (fields.Length == 0 ? "" : ",") + meta + "}}";
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Version = version, VersionPolicy = HttpVersionPolicy.RequestVersionExact, Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("Accept", "application/json, text/event-stream"); request.Headers.Add("MCP-Protocol-Version", "2026-07-28"); request.Headers.Add("Mcp-Method", method); if (name is not null) request.Headers.Add("Mcp-Name", name); if (auth) request.Headers.Add("X-Auth", "yes"); configure?.Invoke(request);
        using var response = await client.SendAsync(request); var text = await response.Content.ReadAsStringAsync(); Console.WriteLine($"HTTP {method}: {(int)response.StatusCode} {text}"); return ((int)response.StatusCode, text, response.Headers.TryGetValues("X-Denied", out var deniedHeader) ? deniedHeader.Single() : null);
    }
    var denied = await Send("tools/list", auth: false); Check(denied.Status == 401 && calls == 0, "Automatic native list obeys endpoint policy");
    var shorted = await Send("tools/list", path: "/short"); Check(shorted.Status == 202 && shorted.Denied == "chosen", "Pending denial status and headers are preserved");
    var configured = await Send("tools/list", path: "/configured"); Check(configured.Status == 203 && configured.Denied == "configured", "Configured denial survives rejected next");
    var written = await Send("tools/list", path: "/written"); Check(written.Status == 202 && written.Denied == "written" && written.Body == "denied body", "Written denial body and headers survive rejected next");
    var throwGate = await Send("tools/list", path: "/throwgate"); Check(throwGate.Status == 500 && !throwGate.Body.Contains("secret", StringComparison.Ordinal), "Throwing middleware denies without SDK admission");
    foreach (string method in new[] { "resources/list", "prompts/list" }) { var rejected = await Send(method, auth: false); Check(rejected.Status == 401, "All automatic lists share authentication: " + method); }
    var authThrow = await Send("tools/list", configure: r => r.Headers.Add("X-Auth-Throw", "yes")); Check(authThrow.Status == 500 && !authThrow.Body.Contains("secret", StringComparison.Ordinal), "Throwing authenticator defaults to denial without parse");
    var authorizationDeny = await Send("tools/list", configure: r => r.Headers.Add("X-Policy-Deny", "yes")); Check(authorizationDeny.Status == 403, "Authenticated endpoint policy denial precedes native list");
    var deniedMalformed = await Send("tools/list", auth: false, configure: r => r.Content = new StringContent("{malformed", Encoding.UTF8, "application/json")); Check(deniedMalformed.Status == 401, "Unauthorized malformed body is never parsed");
    var list = await Send("tools/list"); Check(list.Status == 200 && list.Body.Contains("echo", StringComparison.Ordinal) && list.Body.Contains("inputSchema", StringComparison.Ordinal), "Actual rmcp tool list");
    var call = await Send("tools/call", "\"name\":\"echo\",\"arguments\":{\"text\":\"native\",\"optional\":null}", "echo");
    Check(call.Status == 200 && calls == 1, "Real same-ticket managed continuation completed");
    using (var result = JsonDocument.Parse(call.Body)) { var value = result.RootElement.GetProperty("result"); var text = value.GetProperty("content")[0].GetProperty("text").GetString(); Check(value.GetProperty("structuredContent").GetProperty("text").GetString() == "native!" && text == value.GetProperty("structuredContent").GetRawText(), "Structured output and compatibility text agree"); }
    var explicitError = await Send("tools/call", "\"name\":\"failure\",\"arguments\":{\"text\":\"x\"}", "failure"); Check(explicitError.Body.Contains("\"isError\":true", StringComparison.Ordinal), "Explicit tool error remains result layer");
    var unexpected = await Send("tools/call", "\"name\":\"throw\",\"arguments\":{\"text\":\"x\"}", "throw"); Check(unexpected.Body.Contains("\"isError\":true", StringComparison.Ordinal) && !unexpected.Body.Contains("secret", StringComparison.Ordinal), "Unexpected tool error is redacted");
    var badOutput = await Send("tools/call", "\"name\":\"badoutput\",\"arguments\":{\"text\":\"x\"}", "badoutput"); Check(badOutput.Status == 200 && badOutput.Body.Contains("\"isError\":true", StringComparison.Ordinal) && !badOutput.Body.Contains("structuredContent", StringComparison.Ordinal), "Native output schema rejection returns redacted tool result and retains reservation until retry transfer");
    var oversized = await Send("tools/call", "\"name\":\"largeoutput\",\"arguments\":{\"text\":\"x\"}", "largeoutput"); Check(oversized.Status == 200 && oversized.Body.Contains("\"isError\":true", StringComparison.Ordinal), "Oversized application output is bounded and redacted");
    var empty = await Send("tools/call", "\"name\":\"empty\",\"arguments\":{}", "empty"); Check(empty.Status == 200 && empty.Body.Contains("\"structuredContent\":{}", StringComparison.Ordinal), "Empty object schema remains an actual static object codec");
    var enumCall = await Send("tools/call", "\"name\":\"enum\",\"arguments\":{\"value\":\"renamed-value\"}", "enum"); Check(enumCall.Status == 200 && enumCall.Body.Contains("\"structuredContent\":{\"value\":\"renamed-value\"}", StringComparison.Ordinal), "Source-generated string enum roundtrip through real rmcp");
    var invalid = await Send("tools/call", "\"name\":\"echo\",\"arguments\":{}", "echo"); Check(calls == 1 && invalid.Body.Contains("error", StringComparison.Ordinal), "Required static arguments reject before business");
    var resourceList = await Send("resources/list"); Check(resourceList.Body.Contains("nativeweave:readme", StringComparison.Ordinal), "Real rmcp resource listing");
    var resource = await Send("resources/read", "\"uri\":\"nativeweave:readme\"", "nativeweave:readme"); Check(resource.Body.Contains("Native text", StringComparison.Ordinal), "Fixed text resource bridge");
    var prompt = await Send("prompts/get", "\"name\":\"greet\",\"arguments\":{\"name\":\"Linux\"}", "greet"); Check(prompt.Body.Contains("Hello Linux", StringComparison.Ordinal), "Static string prompt bridge");
    var unknown = await Send("prompts/get", "\"name\":\"greet\",\"arguments\":{\"unknown\":\"Linux\"}", "greet"); Check(unknown.Body.Contains("error", StringComparison.Ordinal), "Prompt required/unknown validation");
    var wrongResource = await Send("resources/read", "\"uri\":\"nativeweave:wrong\"", "nativeweave:wrong"); Check(wrongResource.Body.Contains("\"code\":-32603", StringComparison.Ordinal) && !wrongResource.Body.Contains("secret", StringComparison.Ordinal), "Resource identity mismatch is a redacted protocol error");
    foreach (var name in new[] { "wrongrole", "toomany" }) { var wrongPrompt = await Send("prompts/get", "\"name\":\"" + name + "\",\"arguments\":{}", name); Check(wrongPrompt.Body.Contains("\"code\":-32603", StringComparison.Ordinal), "Prompt output rejects " + name); }
    var promptList = await Send("prompts/list"); Check(promptList.Status == 200 && promptList.Body.Contains("greet", StringComparison.Ordinal), "Actual rmcp prompt list");
    foreach (var test in new (string Name, Action<HttpRequestMessage> Change, int Status)[]
    {
    ("foreign-host", r => r.Headers.Host = "foreign.example:80", 403),
    ("bad-content-type", r => r.Content!.Headers.ContentType = new("text/plain"), 415),
    ("missing-accept", r => r.Headers.Remove("Accept"), 406),
    ("missing-version", r => r.Headers.Remove("MCP-Protocol-Version"), 400),
    ("unsupported-version", r => { r.Headers.Remove("MCP-Protocol-Version"); r.Headers.Add("MCP-Protocol-Version", "2099-01-01"); }, 400),
    ("method-mismatch", r => { r.Headers.Remove("Mcp-Method"); r.Headers.Add("Mcp-Method", "prompts/list"); }, 400),
    ("malformed", r => r.Content = new StringContent("{broken", Encoding.UTF8, "application/json"), 400),
    ("duplicate-property", r => r.Content = new StringContent("{\"x\":1,\"x\":2}", Encoding.UTF8, "application/json"), 400),
    ("over-depth", r => r.Content = new StringContent(new string('[', 33) + "0" + new string(']', 33), Encoding.UTF8, "application/json"), 413)
    })
    { var bad = await Send("tools/list", configure: test.Change); Check(bad.Status == test.Status, "Exact pre-parser/SDK negative: " + test.Name); }
    var noMeta = await Send("tools/list", configure: r => r.Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"params\":{}}", Encoding.UTF8, "application/json")); Check(noMeta.Status == 400, "Required modern metadata absent");
    var wrongName = await Send("tools/call", "\"name\":\"echo\",\"arguments\":{\"text\":\"x\"}", "different"); Check(wrongName.Status == 400 && calls == 1, "Mcp-Name mismatch rejects before typed handler");
    var unknownTool = await Send("tools/call", "\"name\":\"absent\",\"arguments\":{}", "absent"); Check(unknownTool.Status == 200 && unknownTool.Body.Contains("error", StringComparison.Ordinal) && calls == 1, "Unknown tool remains protocol error");
    var origin = await Send("tools/list", configure: request => request.Headers.Add("Origin", "https://foreign.example:443")); Check(origin.Status == 403, "Origin rejection before managed gate");
    foreach (var method in new[] { HttpMethod.Get, HttpMethod.Delete }) { using var response = await client.SendAsync(new(method, "/mcp") { Version = version, VersionPolicy = HttpVersionPolicy.RequestVersionExact }); Check((int)response.StatusCode == 405, "No-event-store method405"); }
    await Task.Delay(50);
    var close = await app.CloseAsync(TimeSpan.FromSeconds(4));
    Check(close.Status == ServerCloseStatus.Completed && close.PendingRequests == 0 && close.RetainedNativeBytes == 0 && close.RetainedManagedBytes == 0, "Native and managed ownership drained");
    Check(completed == middleware, "One lifecycle completion for every admitted gate");
    Check(ScopeProbe.Created - createdBefore == middleware && ScopeProbe.Disposed - disposedBefore == middleware, "Exactly one optional scope created and disposed per admitted gate");
}
foreach (var context in escaped) Throws<ObjectDisposedException>(() => _ = context.Services);
Check(retirements > 0 && retirementFailures.IsEmpty, "Separate managed A07 root/control/input retirement witnesses");
await LifecycleFixtures.RunAsync(Check);
await LifecycleFixtures.UndecidedAsync(Check);
Console.WriteLine($"B19 actual Host assertions={assertions}, managed_retirements={retirements}, dynamic={System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported}");


public sealed record Input(string Text, string? Optional = null);
public sealed record Output(string Text);
public readonly record struct ValueInput(int Number);
[JsonSerializable(typeof(Input))]
[JsonSerializable(typeof(Output))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(ValueInput?))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, GenerationMode = JsonSourceGenerationMode.Metadata)]
internal partial class Json : JsonSerializerContext;
internal sealed class Auth : IServerAuthenticator
{ public Task<ClaimsPrincipal?> AuthenticateAsync(RequestContext context, CancellationToken cancellationToken) => context.Request.Headers.Contains("X-Auth-Throw") ? throw new InvalidOperationException("secret authentication failure") : Task.FromResult(context.Request.Headers.Contains("X-Auth") ? new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "mcp")], "test")) : null); }
internal sealed class Authorize : IServerAuthorizer
{ public Task<bool> AuthorizeAsync(RequestContext context, string policy, CancellationToken cancellationToken) => Task.FromResult(context.User.Identity?.IsAuthenticated == true && !context.Request.Headers.Contains("X-Policy-Deny")); }

internal sealed class ScopeProbe : IDisposable
{
    internal static int Created, Disposed;
    internal ScopeProbe() => Interlocked.Increment(ref Created);
    public void Dispose() => Interlocked.Increment(ref Disposed);
}
