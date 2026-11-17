using NativeWeave.Server;

internal static class StreamingExamples
{
    // Minimal owned result: its copied array belongs to the application after return.
    internal static byte[] CopyAndRelease(BodyChunk chunk)
    {
        using (chunk) return chunk.ToArray();
    }

    // Advanced: cancellation and a failing destination still release each native owner.
    // Each array is an explicit application-owned copy, not a zero-copy claim.
    internal static async Task CopyOwnedChunksAsync(IAsyncEnumerable<BodyChunk> chunks,
        Stream destination, CancellationToken cancellationToken = default)
    {
        await foreach (BodyChunk chunk in chunks.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            using (chunk)
            {
                byte[] owned = chunk.ToArray();
                await destination.WriteAsync(owned, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // Request.Body is borrowed: the request owns it. Do not retain or dispose it here.
    internal static Task CopyBorrowedAsync(Stream requestBody, Stream destination,
        CancellationToken cancellationToken = default) => requestBody.CopyToAsync(destination, 16_384, cancellationToken);
}
