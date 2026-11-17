using System.Diagnostics;
using System.Runtime.CompilerServices;
using NativeWeave.Interop.Abi;
using NativeWeave.Server;

internal static class Program
{
    private static int s_checks;
    private static void Check(bool condition, string message)
    {
        Interlocked.Increment(ref s_checks);
        if (!condition) throw new Exception(message);
    }
    private static T Throws<T>(Action call) where T : Exception
    {
        try { call(); } catch (T error) { Check(true, typeof(T).Name); return error; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private static async Task<T> ThrowsAsync<T>(Func<Task> call) where T : Exception
    {
        try { await call().WaitAsync(TimeSpan.FromSeconds(10)); } catch (T error) { Check(true, typeof(T).Name); return error; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private static byte[] Bytes(int length) => Enumerable.Range(0, length).Select(value => (byte)(value % 251)).ToArray();
    private static void Empty(FixtureTransport transport)
    {
        Check(transport.Snapshot.All(value => value == 0), "Native request/global payload credits remain.");
        Check(Fixture.Live(2) == 0, "Managed reservation handles remain.");
    }

    private static async Task ChunksAndCopyBudgets()
    {
        byte[] input = Bytes(32_768);
        await using var transport = new FixtureTransport(input);
        using BodyChunk chunk = (await transport.ReadChunkAsync(32_768, default))!;
        await transport.RetireAsync();
        Check(chunk.Length == input.Length && Fixture.Live(0) == 0, "Taken buffer depends on operation lifetime.");
        byte[] destination = Enumerable.Repeat((byte)0xee, input.Length + 4).ToArray();
        chunk.CopyTo(destination);
        Check(destination.AsSpan(0, input.Length).SequenceEqual(input) && destination[^1] == 0xee, "Copy changed bytes/tail.");
        byte[] shortDestination = [0xa5];
        Throws<ArgumentException>(() => chunk.CopyTo(shortDestination));
        Check(shortDestination[0] == 0xa5, "Short destination was partially written.");
        transport.CloseDispatch();
        Check(chunk.ToArray().SequenceEqual(input), "Owned buffer failed after request dispatch closed.");
        Check(transport.Snapshot[0] == 32_768 && transport.Snapshot[1] == 0, "Array transfer did not return managed overlap credit.");
        transport.LastOwner!.FailCopy = true;
        await ThrowsAsync<IOException>(() => Task.Run(() => chunk.ToArray()));
        Check(transport.Snapshot[1] == 0 && Fixture.Live(2) == 0, "Throwing copy leaked managed credit.");
        transport.LastOwner.FailCopy = false;
        chunk.Dispose(); chunk.Dispose();
        Throws<ObjectDisposedException>(() => _ = chunk.Length);
        Throws<ObjectDisposedException>(() => chunk.CopyTo(destination));
        Throws<ObjectDisposedException>(() => chunk.ToArray());
        Empty(transport);
    }

    private static async Task CopyCreditDenialAndConcurrentDispose()
    {
        await using (var limited = new FixtureTransport([1, 2, 3, 4], native: 8, managed: 2, perRequest: 8))
        {
            using BodyChunk chunk = (await limited.ReadChunkAsync(4, default))!;
            Check(Throws<InvalidOperationException>(() => chunk.ToArray()).Message == "resource_limit", "Managed budget did not fail promptly.");
            byte[] destination = new byte[4]; chunk.CopyTo(destination);
            Check(destination.SequenceEqual(new byte[] { 1, 2, 3, 4 }), "Caller-owned destination incorrectly requires framework payload credit.");
            Check(limited.Snapshot[1] == 0, "Rejected managed credit changed ledger.");
        }
        await using var transport = new FixtureTransport(Bytes(256));
        BodyChunk retained = (await transport.ReadChunkAsync(256, default))!;
        using var entered = new ManualResetEventSlim(); using var proceed = new ManualResetEventSlim();
        transport.LastOwner!.CopyEntered = () => { entered.Set(); if (!proceed.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); };
        byte[] output = new byte[256];
        Task copy = Task.Run(() => retained.CopyTo(output));
        Check(entered.Wait(TimeSpan.FromSeconds(10)), "Copy did not enter under its lease.");
        retained.Dispose();
        Check(transport.Snapshot[0] == 256, "Concurrent Dispose reclaimed an admitted copy.");
        Throws<ObjectDisposedException>(() => retained.CopyTo(output));
        proceed.Set(); await copy;
        Check(output.SequenceEqual(Bytes(256)), "Concurrent release changed admitted bytes.");
        Empty(transport);
    }

    private static async Task ReadStreamContract()
    {
        byte[] input = Bytes(70_003);
        await using var transport = new FixtureTransport(input);
        var gate = new ServerBodyReadGate();
        using var stream = new ServerReadStream(transport, gate);
        Check(stream.CanRead && !stream.CanWrite && !stream.CanSeek, "Borrowed read capabilities changed.");
        Throws<NotSupportedException>(() => Check(stream.Read(new byte[1], 0, 1) == 0, "Synchronous read unexpectedly returned."));
        Throws<NotSupportedException>(() => _ = stream.Length);
        Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
        using var output = new MemoryStream();
        await stream.CopyToAsync(output, 4096, default);
        Check(output.ToArray().SequenceEqual(input), "Bounded stream copy changed payload.");
        Check(stream.EndObserved, "Body end observation is unavailable to trailer access.");
        Check(await stream.ReadAsync(new byte[8], default) == 0, "EOF must remain stable.");
        Throws<InvalidOperationException>(() => gate.Select(2));
        await transport.RetireAsync(); Empty(transport);
        stream.Dispose();
        Throws<ObjectDisposedException>(() => stream.ReadAsync(new byte[1], default));
        Check(!stream.CanRead, "Disposed facade advertises read admission.");
        Throws<ObjectDisposedException>(() => gate.Select(3));
    }

    private static async Task StreamCancellationFailuresAndModes()
    {
        await using (var transport = new FixtureTransport(Bytes(64)))
        {
            var gate = new ServerBodyReadGate(); using var stream = new ServerReadStream(transport, gate);
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            OperationCanceledException failure = await ThrowsAsync<OperationCanceledException>(() => stream.ReadAsync(new byte[4], cancel.Token).AsTask());
            Check(failure.CancellationToken == cancel.Token && transport.Snapshot[0] == 0, "Precancel allocated/read payload.");
            Check(await stream.ReadAsync(new byte[4], default) == 4, "Precancel stranded read gate.");
        }
        await using (var transport = new FixtureTransport(Bytes(64)) { ManualRead = true })
        {
            using var stream = new ServerReadStream(transport); using var cancel = new CancellationTokenSource();
            Task<int> first = stream.ReadAsync(new byte[16], cancel.Token).AsTask();
            Throws<InvalidOperationException>(() => stream.ReadAsync(new byte[1], default));
            cancel.Cancel(); await ThrowsAsync<OperationCanceledException>(() => first);
            await transport.RetireAsync(); Empty(transport);
        }
        await using (var transport = new FixtureTransport(Bytes(64)))
        {
            using var stream = new ServerReadStream(transport);
            await ThrowsAsync<IOException>(() => stream.CopyToAsync(new FailingDestination(), 16, default));
            await transport.RetireAsync(); Empty(transport);
        }
        await using (var transport = new FixtureTransport(Bytes(64)) { FailMapping = true })
        {
            using var stream = new ServerReadStream(transport);
            await ThrowsAsync<IOException>(() => stream.ReadAsync(new byte[16], default).AsTask());
            await transport.RetireAsync(); Empty(transport);
        }
        await using (var transport = new FixtureTransport(Bytes(64), managed: 1))
        {
            using var stream = new ServerReadStream(transport);
            Check((await ThrowsAsync<InvalidOperationException>(() => stream.CopyToAsync(new MemoryStream(), 16, default))).Message == "resource_limit", "Staging copy ignored managed quota.");
            Empty(transport);
        }
        var gateOnly = new ServerBodyReadGate(); gateOnly.Select(3);
        Throws<InvalidOperationException>(() => gateOnly.Enter(1));
    }

    private static async Task WriteStreamContract()
    {
        await using var transport = new FixtureTransport();
        int responseStarts = 0;
        using var stream = new ServerWriteStream(transport, async (bytes, token) => { responseStarts++; await transport.WriteAsync(bytes, token); });
        Check(!stream.CanRead && stream.CanWrite && !stream.CanSeek, "Borrowed output capabilities changed.");
        Throws<NotSupportedException>(() => stream.Write([1], 0, 1));
        await stream.WriteAsync(new byte[100_000], default);
        Check(responseStarts == 1 && transport.Writes == 1, "Output facade bypassed application response-start pipeline.");
        await stream.FlushAsync(default); Check(transport.Flushes == 1, "Flush did not reach local native flow-control seam.");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => stream.WriteAsync(new byte[1], cancel.Token).AsTask());
        Check(transport.Writes == 1, "Precanceled write reached native adapter.");
        transport.WriteBlock = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task pending = stream.WriteAsync(new byte[8], default).AsTask();
        Throws<InvalidOperationException>(() => stream.WriteAsync(new byte[1], default));
        Throws<InvalidOperationException>(() => stream.FlushAsync(default));
        stream.Dispose();
        Throws<ObjectDisposedException>(() => stream.WriteAsync(new byte[1], default));
        transport.WriteBlock.SetResult(); await pending;
        Check(transport.Finishes == 0, "Disposing borrowed output incorrectly finished/disposed the request.");
        Throws<ObjectDisposedException>(() => transport.WriteAsync(new byte[1], default));
    }

    private static async Task RetainedLimitAndFinalizer()
    {
        byte[] source = Bytes(4_194_305);
        await using (var transport = new FixtureTransport(source))
        {
            var retained = new List<BodyChunk>(128);
            try
            {
                for (int i = 0; i < 128; i++) retained.Add((await transport.ReadChunkAsync(32_768, default))!);
                await transport.RetireAsync();
                Check(transport.Snapshot[0] == 4_194_304, "128 input chunks do not hold full backing capacity.");
                var clock = Stopwatch.StartNew();
                Check(Throws<InvalidOperationException>(() => transport.ReadChunkAsync(1, default)).Message == "resource_limit", "Full input quota did not reject another read.");
                Check(clock.Elapsed < TimeSpan.FromSeconds(1), "Payload reservation waited for retained chunks.");
                retained[0].Dispose();
                using BodyChunk last = (await transport.ReadChunkAsync(1, default))!;
                Check(last.Length == 1, "Explicit release/retry did not reopen payload capacity.");
            }
            finally { foreach (BodyChunk chunk in retained) chunk.Dispose(); }
            await transport.RetireAsync(); Empty(transport);
        }
        await using (var transport = new FixtureTransport([1, 2, 3]))
        {
            // A never-started native operation owns a grant which must roll back on release.
            Fixture.Check(Fixture.CreateRead(transport.Session, new byte[] { 8, 9 }, false, out nint pointer));
            Check(transport.Snapshot[0] == 2, "Never-started reservation absent.");
            Fixture.Release(pointer); Empty(transport);
        }
    }

    private static async Task CompiledConsumers()
    {
        byte[] input = Bytes(80);
        await using var transport = new FixtureTransport(input);
        BodyChunk first = (await transport.ReadChunkAsync(16, default))!;
        Check(StreamingExamples.CopyAndRelease(first).SequenceEqual(input[..16]), "Minimal consumer copy differs.");
        Throws<ObjectDisposedException>(() => _ = first.Length);
        async IAsyncEnumerable<BodyChunk> Chunks()
        {
            while (await transport.ReadChunkAsync(16, default) is BodyChunk chunk) yield return chunk;
        }
        using var destination = new MemoryStream();
        await StreamingExamples.CopyOwnedChunksAsync(Chunks(), destination);
        Check(destination.ToArray().SequenceEqual(input[16..]), "Advanced consumer changed chunks.");
        await transport.RetireAsync(); Empty(transport);
    }

    public static async Task Main(string[] args)
    {
        if (args.Contains("--require-aot")) Check(!RuntimeFeature.IsDynamicCodeSupported, "Expected actual Native AOT executable.");
        await ChunksAndCopyBudgets();
        await CopyCreditDenialAndConcurrentDispose();
        await ReadStreamContract();
        await StreamCancellationFailuresAndModes();
        await WriteStreamContract();
        await RetainedLimitAndFinalizer();
        await CompiledConsumers();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Check(Fixture.Live(0) == 0 && Fixture.Live(1) == 0 && Fixture.Live(2) == 0, "Final native operations/buffers/credits did not return to zero.");
        Console.WriteLine($"B16 Linux streaming: {s_checks} assertions; native operations/buffers/managed-credit handles=0; AOT={!RuntimeFeature.IsDynamicCodeSupported}.");
    }
    private sealed class FailingDestination : Stream
    {
        public override bool CanRead => false;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("Injected destination failure.");
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromException(new IOException("Injected destination failure."));
    }
}
