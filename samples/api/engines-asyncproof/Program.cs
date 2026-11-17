using System.Runtime.CompilerServices;
using System.Text;
using NativeWeave.Engines.AsyncProof;
using NativeWeave.Interop;

if (args.Contains("--require-aot") && RuntimeFeature.IsDynamicCodeSupported) throw new Exception("Actual Native AOT is required.");
await Minimal.Run();
await Advanced.Run();
Console.WriteLine("HTTP proof minimal and advanced consumers passed.");

internal static class Minimal
{
    internal static async Task Run()
    {
        await using var host = new HttpProofHost(static (route, _) => Task.FromResult(Encoding.UTF8.GetBytes($"Hello {route}")));
        using HttpProofResponse response = await host.GetAsync(42);
        using var output = new MemoryStream();
        while (await response.ReadChunkAsync() is { } chunk)
        {
            using (chunk) await chunk.CopyToAsync(output);
        }
        if (response.StatusCode != 200 || Encoding.UTF8.GetString(output.ToArray()) != "Hello 42") throw new Exception("Unexpected native HTTP response.");
    }
}
internal static class Advanced
{
    internal static async Task Run()
    {
        var host = new HttpProofHost(static (_, _) => Task.FromResult("owned native bytes"u8.ToArray()), maxStreams: 1, maxOperations: 4,
            retainedBytes: 8192, chunkBytes: 1024, frameDelayMilliseconds: 25);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using HttpProofResponse response = await host.GetAsync(7, cancellation.Token);
        NativeBuffer first = (await response.ReadChunkAsync(cancellation.Token))!;
        byte[] before = first.ToArray();
        response.Dispose(); // cancels the remaining Body, preserving this separate Bytes owner
        await host.CloseAsync(cancellation.Token); // actual native worker and two-sided cleanup join
        using (first)
        {
            if (!first.ToArray().SequenceEqual(before)) throw new Exception("Retained chunk changed after close.");
            HttpProofSnapshot state = host.GetSnapshot();
            if (state.LiveStreams != 0 || state.Bodies != 0 || state.RuntimeWorkers != 0 || state.RetainedBytes == 0)
                throw new Exception("Stream and retained-byte lifetimes were conflated.");
        }
    }
}
