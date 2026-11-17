using System.Net;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NativeWeave.Server;
using NativeWeave.Server.Hosting;

bool selfTest = args.Contains("--self-test", StringComparer.Ordinal);
bool minimal = args.Contains("--minimal", StringComparer.Ordinal);
var versions = selfTest ? new[] { HttpVersion.Version11, HttpVersion.Version20 } : new[] { HttpVersion.Version11 };
foreach (var version in versions)
{
    var builder = NativeServer.CreateBuilder();
    builder.Listen(IPAddress.Loopback, selfTest ? 0 : 5000, listener => listener.Protocols = version == HttpVersion.Version20 ? ServerProtocols.Http2 : ServerProtocols.Http1);
    builder.Options.RequestTimeout = TimeSpan.FromSeconds(10);
    builder.UseJson(SampleJson.Default);
    builder.Services.AddScoped(_ => new RequestLabel("typed C#"));
    await using var app = builder.Build();
    app.MapGet("/hello", static () => Results.Text("Hello from C# and Rust."));
    if (!minimal)
    {
        SampleEndpoints.Map(app);
        app.MapPost<Greeting, Receipt>("/greet", static (input, _) => Task.FromResult(new Receipt(input.Name.ToUpperInvariant(), input.Number + 1)));
        app.MapRaw("/accepted", ["POST"], static (context, _) => { context.Response.StatusCode = 202; return Task.CompletedTask; });
    }
    if (!selfTest) { await app.RunAsync(); continue; }
    // The optional integration manages this same application and Rust Host.
    var services = new ServiceCollection();
    services.AddNativeWeaveServer(_ => app);
    await using var provider = services.BuildServiceProvider();
    var service = provider.GetRequiredService<IHostedService>();
    await service.StartAsync(CancellationToken.None);
    using var client = new HttpClient { BaseAddress = app.BoundAddresses.Single(), DefaultRequestVersion = version, DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact };
    if (await client.GetStringAsync("hello") != "Hello from C# and Rust.") throw new InvalidOperationException("Minimal Handler failed.");
    if (!minimal)
    {
        using var response = await client.GetAsync("receipt/7?offset=3");
        if (response.Version != version || !(await response.Content.ReadAsStringAsync()).Contains("typed C#:10", StringComparison.Ordinal)) throw new InvalidOperationException("Static route/query/service/DTO mapping failed.");
        using var input = new StringContent("{\"name\":\"linux\",\"number\":4}", Encoding.UTF8, "application/json");
        using var typed = await client.PostAsync("greet", input);
        if (!(await typed.Content.ReadAsStringAsync()).Contains("LINUX", StringComparison.Ordinal)) throw new InvalidOperationException("Typed JSON failed.");
        using var empty = await client.PostAsync("accepted", new ByteArrayContent([]));
        if ((int)empty.StatusCode != 202 || (await empty.Content.ReadAsByteArrayAsync()).Length != 0) throw new InvalidOperationException("Raw final response failed.");
    }
    await service.StopAsync(CancellationToken.None);
    if (app.State != ServerState.Stopped) throw new InvalidOperationException("Host did not close.");
    Console.WriteLine($"PASS {(minimal ? "minimal" : "advanced")} HTTP/{version}; dynamic={System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported}");
}


internal sealed record Greeting(string Name, int Number);
internal sealed record Receipt(string Label, int Number);
internal sealed record RequestLabel(string Text);
[JsonSerializable(typeof(Greeting)), JsonSerializable(typeof(Receipt))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
internal partial class SampleJson : JsonSerializerContext { }
[ServerEndpoints]
internal static partial class SampleEndpoints
{
    [ServerRoute("GET", "/receipt/{id}")]
    private static Receipt Read([RouteParameter] int id, [QueryParameter] int offset, [ServiceParameter] RequestLabel service) => new(service.Text + ":" + (id + offset), id);
}
