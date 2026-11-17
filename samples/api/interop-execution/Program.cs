using System.Runtime.CompilerServices;

if (args.Contains("--require-aot") && RuntimeFeature.IsDynamicCodeSupported)
    throw new Exception("This sample requires an actual Native AOT executable.");

// Maintainer-only concrete adapter example. Ordinary application APIs expose typed
// engine operations; no application constructs a generic Core scheduler.
await using (var engine = new FixtureEngine())
{
    var operation = engine.NewJob(bytes: [3, 4, 5]);
    ulong value = await operation.Start();
    await operation.Retirement;
    if (value != 12) throw new Exception("Native result changed.");
    Console.WriteLine($"Native result: {value}; async disposal observes actual worker join.");
}

await using (var engine = new FixtureEngine())
{
    var operation = engine.NewJob(mode: 1, bytes: new byte[8]);
    var result = operation.Start();
    long deadline = Environment.TickCount64 + 15_000;
    while (engine.Read(10) != 1)
    {
        if (Environment.TickCount64 > deadline) throw new TimeoutException("Blocking work did not start.");
        await Task.Delay(1);
    }
    using var wait = new CancellationTokenSource(TimeSpan.FromMilliseconds(10));
    try { await engine.Lifetime.CloseAsync(wait.Token); throw new Exception("Controlled work closed prematurely."); }
    catch (OperationCanceledException) when (wait.IsCancellationRequested)
    {
        if (engine.Read(3) != 8 || engine.Read(1) != 0) throw new Exception("Timeout released active resources.");
        Console.WriteLine("Close wait canceled; the same native work, bytes and cleanup root remain owned.");
    }
    engine.OpenGate();
    try { await result; throw new Exception("Closing work did not observe cancellation."); }
    catch (OperationCanceledException) { }
    var joined = await engine.Lifetime.CloseAsync();
    if (!joined.Joined || joined.WorkersStarted != joined.WorkersStopped) throw new Exception("Actual worker join is missing.");
    Console.WriteLine($"Workers actually joined: {joined.WorkersStopped}; outstanding bytes: {joined.ReservedBytes}.");
}
