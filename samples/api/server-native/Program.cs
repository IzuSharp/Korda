using System.Net;
using NativeWeave.Server;

// Maintainer sample verification uses the actual native asset. An installed
// NativeWeave.Server package supplies that asset; an application needs no Rust.
var builder = NativeServer.CreateBuilder();
builder.Listen(IPAddress.Loopback, 0);
await using var app = builder.Build();
app.MapGet("/hello/{name}", context => Results.Text("Hello " + context.Request.RouteValues.Get<string>("name")));
if (args.Contains("--advanced", StringComparer.Ordinal)) Advanced.Register(app);
await app.StartAsync();
using var client = new HttpClient { BaseAddress = app.BoundAddresses[0] };
if (await client.GetStringAsync("hello/native") != "Hello native") throw new InvalidOperationException("Minimal native request failed.");
if (args.Contains("--advanced", StringComparer.Ordinal))
{
    byte[] data = Enumerable.Range(0, 100_000).Select(i => (byte)i).ToArray();
    using var response = await client.PostAsync("echo", new ByteArrayContent(data));
    if (!(await response.Content.ReadAsByteArrayAsync()).SequenceEqual(data)) throw new InvalidOperationException("Bounded duplex failed.");
    string events = await client.GetStringAsync("events");
    if (!events.Contains("data: second\n", StringComparison.Ordinal)) throw new InvalidOperationException("SSE formatting failed.");
}
Console.WriteLine("PASS actual native Server sample " + (args.Contains("--advanced", StringComparer.Ordinal) ? "advanced" : "minimal"));
