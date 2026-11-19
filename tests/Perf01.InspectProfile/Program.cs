using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NativeWeave.Engines.SyncProof;
using NativeWeave.Interop;

namespace NativeWeave.Perf01.InspectProfile;

internal static class Program
{
    private const string NativeName = "libnativeweave_engine_syncproof.so";

    // Positional arguments keep this one-purpose profiler independent of other drivers.
    private static int Main(string[] args)
    {
        if (args.Length != 6)
        {
            Console.Error.WriteLine("InspectProbe BUILD_MANIFEST MANIFEST_SHA256 WARMUP_CALLS BATCHES CALLS_PER_BATCH NEW_RESULT_JSON");
            return 2;
        }
        string manifestPath = Path.GetFullPath(args[0]);
        string output = Path.GetFullPath(args[5]);
        int warmup = Count(args[2], 1, 1_000_000);
        int batches = Count(args[3], 1, 64);
        int calls = Count(args[4], 1, 1_000_000);
        Check((long)batches * calls <= 10_000_000, "At most ten million measured calls are allowed.");
        using var sink = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        // All measurement storage exists before the descriptor's first Inspect call.
        var result = new ProfileReceipt
        {
            StartedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ManifestPath = manifestPath, ManifestSha256 = args[1],
            WarmupCalls = warmup, Batches = batches, CallsPerBatch = calls,
            Samples = new Sample[batches], StopwatchFrequency = Stopwatch.Frequency,
            ManagedThreadId = Environment.CurrentManagedThreadId,
            ProcessId = Environment.ProcessId,
            Runtime = RuntimeInformation.FrameworkDescription,
            ServerGc = GCSettings.IsServerGC,
            GcLatencyMode = GCSettings.LatencyMode.ToString()
        };
        long completedWarmup = 0, completedMeasured = 0;
        try
        {
            Check(OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64,
                "This profile supports Linux x64 only.");
            Check(!RuntimeFeature.IsDynamicCodeSupported && !RuntimeFeature.IsDynamicCodeCompiled,
                "An actual Native AOT executable is required.");
            Check(Environment.Version == new Version(11, 0, 0), "Use the locked .NET 11.0.0 runtime.");
            Check(Hash(manifestPath) == args[1], "Build manifest hash differs.");
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
            JsonElement manifest = document.RootElement;
            Check(manifest.GetProperty("state").GetString() == "built", "The build did not complete.");
            string buildRoot = Path.GetDirectoryName(manifestPath)!;
            string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Missing executable path.");
            Check(Path.GetFullPath(executable) == Path.Combine(buildRoot, "aot", "NativeWeave.Perf01.InspectProbe"),
                "The running executable is outside its exact build directory.");
            result.ExecutableSha256 = Hash(executable);
            Check(result.ExecutableSha256 == manifest.GetProperty("artifacts")
                .GetProperty("aot/NativeWeave.Perf01.InspectProbe").GetString(), "Running AOT executable hash differs.");
            foreach (JsonProperty package in manifest.GetProperty("packages").EnumerateObject())
                Check(Hash(Path.Combine(buildRoot, "feed", package.Name)) == package.Value.GetString(),
                    "Actual retained package bytes differ.");
            result.ProductCommit = manifest.GetProperty("product_commit").GetString()!;
            result.ProfileSourceCommit = manifest.GetProperty("profile_source_commit").GetString()!;
            result.CodecManifestSha256 = manifest.GetProperty("codec_manifest_sha256").GetString()!;
            result.AffinityBefore = Affinity();
            result.GcEnvironment = GcEnvironment();
            using Process process = Process.GetCurrentProcess();
            // Prime only counters and driver infrastructure, without inspecting an engine.
            _ = CpuTicks(process); _ = Stopwatch.GetTimestamp();
            _ = GC.GetAllocatedBytesForCurrentThread(); _ = Collections();
            EngineDescriptor descriptor = ZlibCodec.Descriptor;
            Check(descriptor.EngineId == "nativeweave.engines.syncproof" &&
                descriptor.PackageId == "NativeWeave.Engines.SyncProof" && descriptor.PackageVersion == "0.1.0-proof.1",
                "Unexpected public descriptor.");

            Counters first = Start(process);
            EngineInfo expected = descriptor.Inspect(null);
            result.FirstInspect = Finish(process, first, 1);
            result.FirstInspectCalls = 1;
            Check(expected.EngineId == descriptor.EngineId && expected.PackageId == descriptor.PackageId &&
                expected.PackageVersion == descriptor.PackageVersion && expected.RuntimeIdentifier == "linux-x64" &&
                expected.AbiVersion == new AbiVersion(1, 0) && expected.ThreadingModel == EngineThreadingModel.Synchronous &&
                expected.Capabilities.Count == 1 && expected.Capabilities[0].Name == "codec.zlib.v1",
                "Returned engine metadata differs.");
            result.LoadedNativePath = LoadedNative(buildRoot);
            result.LoadedNativeSha256 = Hash(result.LoadedNativePath);
            Check(result.LoadedNativeSha256 == manifest.GetProperty("native_sha256").GetString(),
                "Actually mapped native library differs from the package producer.");

            // Hashing, metadata validation and first load are outside warmup/steady brackets.
            result.Warmup = Measure(process, descriptor, expected, warmup, ref completedWarmup);
            for (int index = 0; index < batches; index++)
            {
                result.Samples[index] = Measure(process, descriptor, expected, calls, ref completedMeasured);
                result.CompletedBatches = index + 1;
            }
            Check(completedWarmup == warmup && completedMeasured == (long)batches * calls,
                "Exact validated call counts differ.");
            Check(Environment.CurrentManagedThreadId == result.ManagedThreadId, "The calling thread changed.");
            result.AffinityAfter = Affinity();
            Check(result.AffinityAfter == result.AffinityBefore, "CPU affinity changed.");
            Check(Hash(manifestPath) == args[1] && Hash(executable) == result.ExecutableSha256 &&
                LoadedNative(buildRoot) == result.LoadedNativePath && Hash(result.LoadedNativePath) == result.LoadedNativeSha256,
                "Build or actually loaded executable/native identity changed.");
            result.Status = "passed";
        }
        catch (Exception error)
        {
            result.Status = "failed";
            result.Error = error.GetType().Name + ": " + error.Message;
        }
        result.CompletedWarmupCalls = completedWarmup;
        result.CompletedMeasuredCalls = completedMeasured;
        result.EndedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        JsonSerializer.Serialize(sink, result, ProfileJsonContext.Default.ProfileReceipt);
        sink.Flush(flushToDisk: true);
        return result.Status == "passed" ? 0 : 1;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Sample Measure(Process process, EngineDescriptor descriptor, EngineInfo expected, int calls, ref long completed)
    {
        Counters start = Start(process);
        for (int index = 0; index < calls; index++)
        {
            EngineInfo current = descriptor.Inspect(null);
            if (!ReferenceEquals(current, expected))
                throw new InvalidOperationException("Inspect did not return the previously validated cached metadata.");
            completed++;
        }
        return Finish(process, start, calls);
    }

    // Process/GC observations can allocate; they deliberately surround the allocation bracket.
    private static Counters Start(Process process)
    {
        long cpu = CpuTicks(process);
        GcCounts gc = Collections();
        long allocation = GC.GetAllocatedBytesForCurrentThread();
        long wall = Stopwatch.GetTimestamp();
        return new(cpu, gc, allocation, wall);
    }

    private static Sample Finish(Process process, Counters start, long calls)
    {
        long wall = Stopwatch.GetTimestamp();
        long allocation = GC.GetAllocatedBytesForCurrentThread();
        GcCounts gc = Collections();
        long cpu = CpuTicks(process);
        return new(calls, allocation - start.Allocation, wall - start.Wall, cpu - start.Cpu,
            gc.Gen0 - start.Gc.Gen0, gc.Gen1 - start.Gc.Gen1, gc.Gen2 - start.Gc.Gen2);
    }

    private static long CpuTicks(Process process) { process.Refresh(); return process.TotalProcessorTime.Ticks; }
    private static GcCounts Collections() => new(GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
    private static int Count(string value, int minimum, int maximum)
    {
        int count = int.Parse(value, CultureInfo.InvariantCulture);
        Check(count >= minimum && count <= maximum, "A call/batch count exceeds the finite profile bounds.");
        return count;
    }
    private static string Hash(string path)
    {
        using FileStream input = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(input));
    }
    private static string Affinity() => File.ReadLines("/proc/self/status")
        .Single(static line => line.StartsWith("Cpus_allowed_list:", StringComparison.Ordinal)).Split(':')[1].Trim();
    private static Dictionary<string, string?> GcEnvironment() => new[]
    {
        "DOTNET_GCHeapCount", "DOTNET_GCServer", "DOTNET_GCHeapHardLimit", "DOTNET_GCHeapHardLimitPercent",
        "COMPlus_GCHeapCount", "COMPlus_gcServer", "COMPlus_GCHeapHardLimit", "COMPlus_GCHeapHardLimitPercent"
    }.ToDictionary(static name => name, Environment.GetEnvironmentVariable);
    private static string LoadedNative(string buildRoot)
    {
        string[] paths = File.ReadLines("/proc/self/maps").Select(static line => line.Split(' ', 6, StringSplitOptions.RemoveEmptyEntries))
            .Where(static parts => parts.Length == 6 && parts[5].Contains(NativeName, StringComparison.Ordinal))
            .Select(static parts => parts[5]).Distinct(StringComparer.Ordinal).ToArray();
        Check(paths.Length == 1 && Path.GetFileName(paths[0]) == NativeName &&
            Path.GetFullPath(paths[0]).StartsWith(Path.Combine(buildRoot, "aot") + Path.DirectorySeparatorChar, StringComparison.Ordinal),
            "Expected exactly one mapped, undeleted codec module from this isolated AOT directory.");
        return paths[0];
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private readonly record struct GcCounts(int Gen0, int Gen1, int Gen2);
    private readonly record struct Counters(long Cpu, GcCounts Gc, long Allocation, long Wall);
}

internal readonly record struct Sample(long Calls, long CallingThreadAllocatedBytes, long WallStopwatchTicks,
    long ProcessCpuTimeSpanTicks, int Gen0Collections, int Gen1Collections, int Gen2Collections);

internal sealed class ProfileReceipt
{
    public string Task { get; set; } = "Perf-01";
    public string Kind { get; set; } = "Public EngineDescriptor.Inspect(null) profiling only";
    public string Status { get; set; } = "running";
    public string? Error { get; set; }
    public bool FormalTimingEligible { get; set; } = false;
    public int DurationCreditSeconds { get; set; } = 0;
    public string StartedUtc { get; set; } = "";
    public string EndedUtc { get; set; } = "";
    public string ManifestPath { get; set; } = "";
    public string ManifestSha256 { get; set; } = "";
    public string CodecManifestSha256 { get; set; } = "";
    public string ProductCommit { get; set; } = "";
    public string ProfileSourceCommit { get; set; } = "";
    public string ExecutableSha256 { get; set; } = "";
    public string LoadedNativePath { get; set; } = "";
    public string LoadedNativeSha256 { get; set; } = "";
    public string Runtime { get; set; } = "";
    public string AffinityBefore { get; set; } = "";
    public string AffinityAfter { get; set; } = "";
    public bool ServerGc { get; set; }
    public string GcLatencyMode { get; set; } = "";
    public Dictionary<string, string?> GcEnvironment { get; set; } = [];
    public int ProcessId { get; set; }
    public int ManagedThreadId { get; set; }
    public long StopwatchFrequency { get; set; }
    public int WarmupCalls { get; set; }
    public int Batches { get; set; }
    public int CallsPerBatch { get; set; }
    public int FirstInspectCalls { get; set; }
    public int CompletedBatches { get; set; }
    public long CompletedWarmupCalls { get; set; }
    public long CompletedMeasuredCalls { get; set; }
    public Sample FirstInspect { get; set; }
    public Sample Warmup { get; set; }
    public Sample[] Samples { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ProfileReceipt))]
internal partial class ProfileJsonContext : JsonSerializerContext { }
