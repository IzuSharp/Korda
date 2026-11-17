using NativeWeave.Server;

internal static class Advanced
{
    internal static void Register(NativeServerApplication app)
    {
        if ((app.Capabilities & ServerCapabilities.ServerSentEvents) == 0) throw new NotSupportedException("This sample requires the native SSE capability.");
        app.MapPost("/echo", context => Results.Stream(async (output, cancellation) =>
        {
            while (await context.Request.ReadChunkAsync(cancellation) is { } chunk)
            {
                using (chunk) await output.WriteAsync(chunk.ToArray(), cancellation);
            }
        }));
        app.MapGet("/events", _ => Results.ServerSentEvents(Events()));
    }
    private static async IAsyncEnumerable<ServerEvent> Events()
    {
        yield return new("first", "sample", "1");
        await Task.CompletedTask;
        yield return new("second", "sample", "2");
    }
}
