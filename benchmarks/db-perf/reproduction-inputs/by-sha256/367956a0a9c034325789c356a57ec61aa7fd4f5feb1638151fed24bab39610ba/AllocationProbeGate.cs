using System.Text.Json;

// This gate covers only the warmed synchronous numeric rows already inside a
// parsed batch. It deliberately does not assert zero allocation for opening,
// fetching, parsing, completing or disposing a public provider query.
internal static class AllocationProbeGate
{
    internal static async Task RunAsync(string output)
    {
        const int rounds = 16, rows = 4096;
        await AllocationProbe.RunAsync(output,
        [
            "--probe-lane=sync-inbatch", "--probe-rounds=16", "--probe-rows=4096",
            "--probe-label=sync-inbatch-zero-regression",
        ]);
        using var file = File.OpenRead(Path.Combine(output, "allocation-probe-sync-inbatch.json"));
        using var document = JsonDocument.Parse(file);
        var root = document.RootElement;
        long rawBytes = root.GetProperty("managedAllocatedBytesRaw").GetInt64();
        if (rawBytes != 0)
            throw new InvalidOperationException($"Warmed in-batch numeric rows allocated {rawBytes} managed bytes; expected exactly zero raw bytes.");
        if (root.GetProperty("completedOperations").GetInt64() != rounds
            || root.GetProperty("completedRows").GetInt64() != rounds * (long)(rows - 1)
            || root.GetProperty("fetchesInMeasuredBoundary").GetInt64() != 0
            || root.GetProperty("observedIncompleteMovesOrFetches").GetInt64() != 0
            || root.GetProperty("driverLiveBatchesAfterDrain").GetInt64() != 0
            || root.GetProperty("driverPendingCompletionsAfterDrain").GetInt64() != 0
            || root.GetProperty("businessOutputAllocatedBytes").GetInt64() != 0
            || root.GetProperty("publicProviderAcceptance").GetBoolean())
            throw new InvalidOperationException("The isolated in-batch allocation regression boundary changed.");
        foreach (var window in root.GetProperty("windows").EnumerateArray())
            if (window.GetProperty("allocatedBytes").GetInt64() != 0)
                throw new InvalidOperationException("An in-batch allocation window did not contain exactly zero raw bytes.");
        Console.WriteLine($"PASS allocation gate: rawBytes={rawBytes}, rows={rounds * (long)(rows - 1)}, windows={rounds}; isolated synchronous in-batch numeric mapping only.");
    }
}
