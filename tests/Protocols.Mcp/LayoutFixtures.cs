using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using NativeWeave.Server;
using NativeWeave.Server.Mcp;

internal static class LayoutFixtures
{
    private const string Metadata = "\"_meta\":{\"io.modelcontextprotocol/protocolVersion\":\"2026-07-28\",\"io.modelcontextprotocol/clientCapabilities\":{}}";
    private static string Arguments => "{\"items\":[" + string.Join(',', Enumerable.Repeat("{}", 128)) + "]}";
    internal static async Task RunAsync(Action<bool, string> check)
    {
        string arguments = Arguments;
        void Measure<T>(string name, JsonTypeInfo<T> info, Func<T, int> count, long minimum)
        {
            _ = JsonSerializer.Deserialize(arguments, info);
            long before = GC.GetAllocatedBytesForCurrentThread();
            T parsed = JsonSerializer.Deserialize(arguments, info)!;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            using var schema = JsonDocument.Parse(StaticSchema.Create(name, info));
            long coefficient = schema.RootElement.GetProperty(ManagedLayout.Annotation).GetProperty("nodeBytes").GetInt64();
            long bound = 3L * Encoding.UTF8.GetByteCount(arguments) + 131 * coefficient + 8 * 5 + 65536;
            check(count(parsed) == 128 && allocated >= minimum && allocated <= bound, "Complete CLR layout bound covers actual static allocation: " + name);
            Console.WriteLine($"layout case={name} node_bytes={coefficient} allocated={allocated} minimum={minimum} bound={bound}");
            GC.KeepAlive(parsed);
        }
        Measure("mapped", LayoutJson.Default.MappedLayoutInput, value => value.Items.Length, 128L * 80 * Unsafe.SizeOf<Big06>());
        Measure("padded", LayoutJson.Default.PaddedLayoutInput, value => value.Items.Length, 128L * Unsafe.SizeOf<PaddedValue>());
        Measure("hidden", LayoutJson.Default.HiddenLayoutInput, value => value.Items.Length + value.Items[0].Touch(), 128L * Unsafe.SizeOf<PaddedValue>());
        Measure("ignored", LayoutJson.Default.IgnoredLayoutInput, value => value.Items.Length, 128L * Unsafe.SizeOf<PaddedValue>());
        Measure("inherited", LayoutJson.Default.InheritedLayoutInput, value => value.Items.Length + value.Items[0].Touch(), 128L * Unsafe.SizeOf<PaddedValue>());
        Measure("explicit-offset", LayoutJson.Default.OffsetLayoutInput, value => value.Items.Length, 128L * 65540);
        Measure("explicit-size", LayoutJson.Default.SizedLayoutInput, value => value.Items.Length, 128L * 65536);
        foreach (var version in new[] { HttpVersion.Version11, HttpVersion.Version20 })
        {
            int entered = 0;
            var registry = new McpRegistry();
            void Add<T>(string name, JsonTypeInfo<T> info) => registry.AddTool(name, "Static layout regression", info, Json.Default.Output,
                (_, _) => { Interlocked.Increment(ref entered); return Task.FromResult(McpToolResult<Output>.Success(new("unexpected"))); });
            Add("mapped", LayoutJson.Default.MappedLayoutInput); Add("padded", LayoutJson.Default.PaddedLayoutInput);
            Add("hidden", LayoutJson.Default.HiddenLayoutInput); Add("ignored", LayoutJson.Default.IgnoredLayoutInput);
            Add("inherited", LayoutJson.Default.InheritedLayoutInput); Add("offset", LayoutJson.Default.OffsetLayoutInput); Add("sized", LayoutJson.Default.SizedLayoutInput);
            registry.AddTool("small", "Ordinary object-valued struct remains supported", LayoutJson.Default.SmallPair, Json.Default.Output,
                (input, _) => { Interlocked.Increment(ref entered); return Task.FromResult(McpToolResult<Output>.Success(new((input.Left + input.Right).ToString(System.Globalization.CultureInfo.InvariantCulture)))); });
            registry.AddTool("boxing", "Output boxing peak is admitted before the SDK", SchemaJson.Default.Empty, LayoutJson.Default.Box13,
                (_, _) => { Interlocked.Increment(ref entered); return Task.FromResult(McpToolResult<Box13>.Success(default)); });
            var builder = NativeServer.CreateBuilder();
            builder.Listen(IPAddress.Loopback, 0, o => o.Protocols = version == HttpVersion.Version20 ? ServerProtocols.Http2 : ServerProtocols.Http1);
            builder.Options.RequestTimeout = TimeSpan.FromSeconds(10);
            await using var app = builder.Build(); app.MapMcp("/mcp", registry); await app.StartAsync();
            using var client = new HttpClient { BaseAddress = app.BoundAddresses.Single(), Timeout = TimeSpan.FromSeconds(12) };
            async Task<HttpResponseMessage> Call(string name, string args, string? header = null)
            {
                string body = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"" + name + "\",\"arguments\":" + args + "," + Metadata + "}}";
                using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Version = version, VersionPolicy = HttpVersionPolicy.RequestVersionExact, Content = new StringContent(body, Encoding.UTF8, "application/json") };
                request.Headers.Add("Accept", "application/json, text/event-stream"); request.Headers.Add("MCP-Protocol-Version", "2026-07-28"); request.Headers.Add("Mcp-Method", "tools/call"); request.Headers.Add("Mcp-Name", header ?? name);
                return await client.SendAsync(request);
            }
            foreach (string name in new[] { "mapped", "padded", "hidden", "ignored", "inherited", "offset", "sized" })
            {
                using var response = await Call(name, arguments);
                check(response.StatusCode == HttpStatusCode.ServiceUnavailable && entered == 0, "Static CLR expansion rejects before typed deserialization/handler: " + name + " HTTP/" + version);
            }
            using (var response = await Call("boxing", "{}")) check(response.StatusCode == HttpStatusCode.ServiceUnavailable && entered == 0, "Output boxing path rejects before SDK/business admission");
            using (var response = await Call("hidden", arguments, "small")) check(response.StatusCode == HttpStatusCode.BadRequest && entered == 0, "Header mismatch cannot select a wider admitted CLR codec");
            using (var response = await Call("small", "{\"left\":1,\"right\":2}")) check(response.IsSuccessStatusCode && entered == 1 && (await response.Content.ReadAsStringAsync()).Contains("\"text\":\"3\"", StringComparison.Ordinal), "Small non-nullable struct tool still runs beside wide registered DTOs");
            using (var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Version = version, VersionPolicy = HttpVersionPolicy.RequestVersionExact, Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\",\"params\":{" + Metadata + "}}", Encoding.UTF8, "application/json") })
            {
                request.Headers.Add("Accept", "application/json, text/event-stream"); request.Headers.Add("MCP-Protocol-Version", "2026-07-28"); request.Headers.Add("Mcp-Method", "tools/list");
                using var response = await client.SendAsync(request); using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                using var expected = JsonDocument.Parse(StaticSchema.Create("small", LayoutJson.Default.SmallPair));
                var tool = document.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().Single(value => value.GetProperty("name").GetString() == "small");
                check(response.IsSuccessStatusCode && JsonElement.DeepEquals(tool.GetProperty("inputSchema"), expected.RootElement), "Exact registered schema and root allocation annotation round-trip through public rmcp");
            }
            var closed = await app.CloseAsync(TimeSpan.FromSeconds(5));
            check(closed.Status == ServerCloseStatus.Completed && closed.PendingRequests == 0 && closed.RetainedNativeBytes == 0 && closed.RetainedManagedBytes == 0, "Rejected layout admissions and positive struct request retire with zero credits");
        }
    }
}
internal sealed class MappedLayoutInput { public MappedLayoutItem[] Items { get; set; } = null!; }
internal sealed class MappedLayoutItem
{
    public Big06 P00 { get; set; }
    public Big06 P01 { get; set; }
    public Big06 P02 { get; set; }
    public Big06 P03 { get; set; }
    public Big06 P04 { get; set; }
    public Big06 P05 { get; set; }
    public Big06 P06 { get; set; }
    public Big06 P07 { get; set; }
    public Big06 P08 { get; set; }
    public Big06 P09 { get; set; }
    public Big06 P10 { get; set; }
    public Big06 P11 { get; set; }
    public Big06 P12 { get; set; }
    public Big06 P13 { get; set; }
    public Big06 P14 { get; set; }
    public Big06 P15 { get; set; }
    public Big06 P16 { get; set; }
    public Big06 P17 { get; set; }
    public Big06 P18 { get; set; }
    public Big06 P19 { get; set; }
    public Big06 P20 { get; set; }
    public Big06 P21 { get; set; }
    public Big06 P22 { get; set; }
    public Big06 P23 { get; set; }
    public Big06 P24 { get; set; }
    public Big06 P25 { get; set; }
    public Big06 P26 { get; set; }
    public Big06 P27 { get; set; }
    public Big06 P28 { get; set; }
    public Big06 P29 { get; set; }
    public Big06 P30 { get; set; }
    public Big06 P31 { get; set; }
    public Big06 P32 { get; set; }
    public Big06 P33 { get; set; }
    public Big06 P34 { get; set; }
    public Big06 P35 { get; set; }
    public Big06 P36 { get; set; }
    public Big06 P37 { get; set; }
    public Big06 P38 { get; set; }
    public Big06 P39 { get; set; }
    public Big06 P40 { get; set; }
    public Big06 P41 { get; set; }
    public Big06 P42 { get; set; }
    public Big06 P43 { get; set; }
    public Big06 P44 { get; set; }
    public Big06 P45 { get; set; }
    public Big06 P46 { get; set; }
    public Big06 P47 { get; set; }
    public Big06 P48 { get; set; }
    public Big06 P49 { get; set; }
    public Big06 P50 { get; set; }
    public Big06 P51 { get; set; }
    public Big06 P52 { get; set; }
    public Big06 P53 { get; set; }
    public Big06 P54 { get; set; }
    public Big06 P55 { get; set; }
    public Big06 P56 { get; set; }
    public Big06 P57 { get; set; }
    public Big06 P58 { get; set; }
    public Big06 P59 { get; set; }
    public Big06 P60 { get; set; }
    public Big06 P61 { get; set; }
    public Big06 P62 { get; set; }
    public Big06 P63 { get; set; }
    public Big06 P64 { get; set; }
    public Big06 P65 { get; set; }
    public Big06 P66 { get; set; }
    public Big06 P67 { get; set; }
    public Big06 P68 { get; set; }
    public Big06 P69 { get; set; }
    public Big06 P70 { get; set; }
    public Big06 P71 { get; set; }
    public Big06 P72 { get; set; }
    public Big06 P73 { get; set; }
    public Big06 P74 { get; set; }
    public Big06 P75 { get; set; }
    public Big06 P76 { get; set; }
    public Big06 P77 { get; set; }
    public Big06 P78 { get; set; }
    public Big06 P79 { get; set; }
}
internal struct Big01 { public decimal A { get; set; } }
internal struct Big02 { public Big01 A { get; set; } public Big01 B { get; set; } }
internal struct Big03 { public Big02 A { get; set; } public Big02 B { get; set; } }
internal struct Big04 { public Big03 A { get; set; } public Big03 B { get; set; } }
internal struct Big05 { public Big04 A { get; set; } public Big04 B { get; set; } }
internal struct Big06 { public Big05 A { get; set; } public Big05 B { get; set; } }
[StructLayout(LayoutKind.Sequential, Size = 65536)] internal struct PaddedValue { public int A { get; set; } }
internal sealed class PaddedLayoutInput { public PaddedLayoutItem[] Items { get; set; } = null!; }
internal sealed class PaddedLayoutItem { public PaddedValue Value { get; set; } }
internal sealed class HiddenLayoutInput { public HiddenLayoutItem[] Items { get; set; } = null!; }
internal sealed class HiddenLayoutItem { private PaddedValue _hidden = default; public int Touch() => _hidden.A; }
internal sealed class IgnoredLayoutInput { public IgnoredLayoutItem[] Items { get; set; } = null!; }
internal sealed class IgnoredLayoutItem { [JsonIgnore] public PaddedValue Hidden { get; set; } }
internal sealed class InheritedLayoutInput { public InheritedLayoutItem[] Items { get; set; } = null!; }
internal class LayoutBase { private PaddedValue _hidden = default; public int Touch() => _hidden.A; }
internal sealed class InheritedLayoutItem : LayoutBase { public string? Name { get; set; } }
internal sealed class OffsetLayoutInput { public OffsetLayoutItem[] Items { get; set; } = null!; }
[StructLayout(LayoutKind.Explicit)] internal sealed class OffsetLayoutItem { [FieldOffset(65536)] private int _value; public int Value { get => _value; set => _value = value; } }
internal sealed class SizedLayoutInput { public SizedLayoutItem[] Items { get; set; } = null!; }
[StructLayout(LayoutKind.Sequential, Size = 65536)] internal sealed class SizedLayoutItem { public int Value { get; set; } }
internal struct SmallPair { public int Left { get; set; } public int Right { get; set; } }
[StructLayout(LayoutKind.Sequential, Size = 131072)] internal struct Box00 { public int Value { get; set; } }
internal struct Box01 { public Box00 Value { get; set; } }
internal struct Box02 { public Box01 Value { get; set; } }
internal struct Box03 { public Box02 Value { get; set; } }
internal struct Box04 { public Box03 Value { get; set; } }
internal struct Box05 { public Box04 Value { get; set; } }
internal struct Box06 { public Box05 Value { get; set; } }
internal struct Box07 { public Box06 Value { get; set; } }
internal struct Box08 { public Box07 Value { get; set; } }
internal struct Box09 { public Box08 Value { get; set; } }
internal struct Box10 { public Box09 Value { get; set; } }
internal struct Box11 { public Box10 Value { get; set; } }
internal struct Box12 { public Box11 Value { get; set; } }
internal struct Box13 { public Box12 Value { get; set; } }
[JsonSerializable(typeof(MappedLayoutInput))]
[JsonSerializable(typeof(PaddedLayoutInput))]
[JsonSerializable(typeof(HiddenLayoutInput))]
[JsonSerializable(typeof(IgnoredLayoutInput))]
[JsonSerializable(typeof(InheritedLayoutInput))]
[JsonSerializable(typeof(OffsetLayoutInput))]
[JsonSerializable(typeof(SizedLayoutInput))]
[JsonSerializable(typeof(SmallPair))]
[JsonSerializable(typeof(Box13))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class LayoutJson : JsonSerializerContext;
