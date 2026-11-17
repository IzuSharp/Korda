using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NativeWeave.Server;

byte[] payload = "0123456789abcdef"u8.ToArray();
long allocated = GC.GetTotalAllocatedBytes(true);
if (args.Single() == "native")
{
    var options = new ServerOptions { MaxRequestBodyBytes = 65_536, MaxJsonBytes = 1_024, MaxHeaderBytes = 32_768, HeaderReadTimeout = TimeSpan.FromSeconds(15), KeepAliveTimeout = TimeSpan.FromSeconds(120) };
    options.Budget.MaxConnections = options.Budget.MaxActiveRequests = 128; options.Budget.MaxQueuedRequests = 128;
    var config = new ServerConfiguration(ServerConfiguration.Snapshot(options), [new(IPAddress.Loopback, 0, "bench", ServerProtocols.Http1, null)], [], null, null);
    await using var host = new NativeHost(config);
    var route = new RouteDefinition(1, new(null, ""), "/bench", ["GET"], null, null); route.Validate(config);
    var headers = new Dictionary<string, IReadOnlyList<string>> { ["content-length"] = new[] { "16" } };
    await host.StartAsync([route], async request => { await request.CommitHeadersAsync(200, headers, default); await request.Body.WriteAsync(payload, default); await request.Body.FinishAsync(default); }, default);
    Console.WriteLine(host.BoundAddresses[0]); await Console.In.ReadLineAsync();
}
else
{
    var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
    builder.WebHost.UseKestrel(o => { o.AddServerHeader = false; o.Limits.MaxConcurrentConnections = 128; o.Limits.MaxRequestBodySize = 65_536; o.Limits.MaxRequestHeadersTotalSize = 32_768; o.Limits.MaxRequestHeaderCount = 200; o.Limits.MaxRequestBufferSize = o.Limits.MaxResponseBufferSize = 32_768; o.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15); o.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(120); o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1); });
    await using var app = builder.Build(); app.Run(async context => { if (context.Request.Method != "GET" || context.Request.Path != "/bench") { context.Response.StatusCode = 404; return; } context.Response.ContentLength = 16; await context.Response.Body.WriteAsync(payload); });
    await app.StartAsync(); Console.WriteLine(app.Urls.Single()); await Console.In.ReadLineAsync(); await app.StopAsync();
}
Console.WriteLine("managed_allocated_total=" + (GC.GetTotalAllocatedBytes(true) - allocated));
