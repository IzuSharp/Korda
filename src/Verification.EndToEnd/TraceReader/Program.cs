using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing;

if (args.Length == 1 && args[0] == "--runtime-identity")
{
    Console.WriteLine(JsonSerializer.Serialize(new { runtimeDirectory = RuntimeEnvironment.GetRuntimeDirectory(),
        runtimeVersion = Environment.Version.ToString(), framework = RuntimeInformation.FrameworkDescription,
        runtimeIdentifier = RuntimeInformation.RuntimeIdentifier, processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
        processPath = Environment.ProcessPath, dynamicCodeSupported = RuntimeFeature.IsDynamicCodeSupported }));
    return 0;
}

if (args.Length is < 2 or > 3)
{
    Console.Error.WriteLine("Usage: TraceReader <actual.nettrace> <receipt.json> [known-handle-control.jsonl]");
    return 2;
}

var live = new Dictionary<ulong, int>();
var created = new HashSet<ulong>();
var destroyed = new HashSet<ulong>();
var expected = new HashSet<ulong>();
if (args.Length == 3)
{
    foreach (string line in File.ReadLines(args[2]))
    {
        using var control = JsonDocument.Parse(line);
        foreach (var value in control.RootElement.GetProperty("handles").EnumerateArray())
        {
            ulong id = ulong.Parse(value.GetString()!, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            expected.Add(id); expected.Add(id & ~1UL);
        }
    }
    if (expected.Count > 256) throw new ArgumentException("Known handle controls exceed the observation bound.");
}
var countsByKind = new Dictionary<int, long>();
string samplesPath = args[1] + ".samples.jsonl";
using var sampleOutput = new StreamWriter(new FileStream(samplesPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read));
long sampleCount = 0;
long sets = 0, destroys = 0, unknownDestroys = 0, peak = 0, reusedWhileLive = 0;
long heapSamples = 0;
double lastSample = -1000;
long? latestRuntimeGcHandles = null;
bool controlActive = false;
var controlPhases = new List<string>(3);
using var trace = new EventPipeEventSource(args[0]);
trace.Dynamic.All += value =>
{
    if (value.ProviderName == "NativeWeave.B22" && value.EventName == "Observation")
    {
        sampleOutput.WriteLine(JsonSerializer.Serialize(new { milliseconds = value.TimeStampRelativeMSec,
            controlSequence = (long)value.PayloadByName("sequence"), phase = (string)value.PayloadByName("phase"),
            runtimeGcHandlesAtLastGc = latestRuntimeGcHandles, lastGcMilliseconds = lastSample,
            trackedLiveHandles = live.Count, trackedLiveByKind = new Dictionary<int, long>(countsByKind) }));
        sampleCount++;
        return;
    }
    if (value.ProviderName != "NativeWeave.B22" || value.EventName != "Phase") return;
    string phase = (string)value.PayloadByName("phase");
    if (controlPhases.Count >= 3) throw new InvalidOperationException("Unexpected repeated handle-control window.");
    controlPhases.Add(phase);
    if (phase == "begin") controlActive = true;
    if (phase == "end") controlActive = false;
};
trace.Clr.GCSetGCHandle += value =>
{
    ulong handle = value.HandleID;
    int kind = (int)value.Kind;
    if (live.Count >= 1_000_000) throw new InvalidOperationException("The trace exceeded its finite live-handle observation bound.");
    if (live.TryGetValue(handle, out int previous)) { countsByKind[previous]--; reusedWhileLive++; }
    live[handle] = kind; countsByKind[kind] = countsByKind.GetValueOrDefault(kind) + 1;
    if (controlActive && expected.Contains(handle)) created.Add(handle);
    sets++; peak = Math.Max(peak, live.Count);
};
// TraceEvent4.0.0 retains this historical public event spelling.
trace.Clr.GCDestoryGCHandle += value =>
{
    ulong handle = value.HandleID;
    if (live.Remove(handle, out int kind)) countsByKind[kind]--;
    else unknownDestroys++;
    if (controlActive && expected.Contains(handle)) destroyed.Add(handle);
    destroys++;
};
trace.Clr.GCHeapStats += value =>
{
    heapSamples++;
    // Preserve actual GC boundaries, including both known-control collections.
    // A runtime total anchors handles that predate trace startup.
    lastSample = value.TimeStampRelativeMSec;
    latestRuntimeGcHandles = value.GCHandleCount;
    sampleOutput.WriteLine(JsonSerializer.Serialize(new { milliseconds = lastSample, runtimeGcHandles = value.GCHandleCount,
        runtimePinnedObjects = value.PinnedObjectCount, trackedLiveHandles = live.Count,
        trackedLiveByKind = new Dictionary<int, long>(countsByKind) }));
    sampleCount++;
};
trace.Process();
sampleOutput.Flush();
var controls = new List<object>();
bool controlsPassed = args.Length == 3;
if (args.Length == 3)
{
    var lines = File.ReadAllLines(args[2]).Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
    if (lines.Length != 2) controlsPassed = false;
    else
    {
        using var allocated = JsonDocument.Parse(lines[0]);
        using var freed = JsonDocument.Parse(lines[1]);
        int normal = allocated.RootElement.GetProperty("normal").GetInt32();
        var handles = allocated.RootElement.GetProperty("handles").EnumerateArray().Select(value => ulong.Parse(value.GetString()!, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
        controlsPassed &= allocated.RootElement.GetProperty("phase").GetString() == "allocated" && freed.RootElement.GetProperty("phase").GetString() == "freed" &&
            normal > 0 && handles.Length > normal && allocated.RootElement.GetProperty("handles").GetRawText() == freed.RootElement.GetProperty("handles").GetRawText();
        for (int index = 0; index < handles.Length; index++)
        {
            ulong id = handles[index];
            // GCHandle's public pinned cookie has its low pin flag set; the
            // runtime event names the underlying handle-table slot. Retain which
            // exact public-cookie/event mapping was actually observed.
            ulong eventId = created.Contains(id) ? id : index >= normal ? id & ~1UL : id;
            bool matched = created.Contains(eventId) && destroyed.Contains(eventId);
            controlsPassed &= matched;
            controls.Add(new { kind = index < normal ? "normal" : "pinned", observedSetAndDestroy = matched,
                publicCookieEqualsEventHandle = id == eventId, slotReusedAfterControl = live.ContainsKey(eventId) });
        }
    }
}
controlsPassed &= controlPhases.SequenceEqual(["begin", "allocated", "end"]);
bool usable = trace.EventsLost == 0 && sets > 0 && destroys > 0 && heapSamples > 0 && controlsPassed;
var result = new
{
    schemaVersion = 1, task = "B22", status = usable ? "observed-with-positive-control" : "unavailable-or-incomplete",
    tracePath = Path.GetFullPath(args[0]), traceBytes = new FileInfo(args[0]).Length,
    eventsLost = trace.EventsLost, handleSets = sets, handleDestroys = destroys,
    trackedLiveAtEnd = live.Count, peakTrackedLive = peak, unknownInitialHandleDestroys = unknownDestroys,
    handleUpdatesWhileLive = reusedWhileLive, heapStatsEvents = heapSamples,
    knownControlsPassed = controlsPassed, knownControls = controls, controlPhases,
    sampleCount, samplesPath = Path.GetFullPath(samplesPath), limitations = new[] { "Runtime GCHandle totals are process observations, not counts attributed exclusively to NativeWeave.",
        "Weak/strong/pinned/dependent kinds remain distinct; neither this trace nor RSS counts every managed object graph root.",
        "An unknown initial destroy is recorded, not silently inserted into a zero startup baseline.",
        "Missing events or a failed known-handle control invalidate root-count acceptance." }
};
File.WriteAllText(args[1], JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }) + "\n");
Console.WriteLine(JsonSerializer.Serialize(new { result.status, result.eventsLost, result.handleSets, result.handleDestroys, result.heapStatsEvents, result.knownControlsPassed }));
return usable ? 0 : 1;
