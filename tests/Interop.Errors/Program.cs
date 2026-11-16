using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NativeWeave.Interop;
using NativeWeave.Interop.Abi;

internal static unsafe partial class Program
{
    private static int _assertions;
    private static void Check(bool condition, string name)
    {
        _assertions++;
        if (!condition) throw new InvalidOperationException(name);
    }
    private static void Throws<T>(Action action) where T : Exception
    {
        _assertions++;
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException("Missing " + typeof(T).Name);
    }

    private static void Main(string[] args)
    {
        if (args.Contains("--require-aot")) Check(!RuntimeFeature.IsDynamicCodeSupported, "actual Native AOT runtime");
        var cause = new InvalidOperationException("managed cause");
        foreach (var error in new[] { new NativeEngineException(), new NativeEngineException(null), new NativeEngineException("message", cause) })
            Check(error.EngineId is null && error.Operation is null && error.StatusCode is null && error.ErrorDomain is null && error.NativeCode is null && error.CorrelationId is null, "standard constructor invents no metadata");
        var unknown = new NativeEngineException("database.postgresql", "execute", uint.MaxValue, uint.MaxValue, int.MinValue, ulong.MaxValue, "redacted", cause);
        Check(unknown.StatusCode == uint.MaxValue && unknown.NativeCode == int.MinValue && unknown.CorrelationId == ulong.MaxValue && unknown.InnerException == cause, "exact native metadata");
        foreach (string invalid in new[] { "", "Database", "a..b", "a--b", "a.", ".a", "a_b", "aé", new string('a', 129) })
            Throws<ArgumentException>(() => new NativeEngineException(invalid, "execute", 9, 0, 0, 0, null));
        foreach (uint invalid in new uint[] { 0, 1 })
            Throws<ArgumentOutOfRangeException>(() => new NativeEngineException("engine", "execute", invalid, 0, 0, 0, null));
        Throws<ArgumentNullException>(() => new NativeEngineException(null!, "execute", 9, 0, 0, 0, null));
        Throws<ArgumentException>(() => new NativeEngineException("engine", "", 9, 0, 0, 0, null));
        foreach (EngineLoadFailure failure in Enum.GetValues<EngineLoadFailure>())
        {
            var error = new EngineLoadException(failure, "engine", "linux-x64", "Restore the matching native package.", cause);
            Check(error.Failure == failure && error.RuntimeIdentifier == "linux-x64" && error.EngineId == "engine" && error.Operation == "load" && error.StatusCode is null && error.ErrorDomain is null && error.NativeCode is null && error.CorrelationId is null, "pre-native identity without wire metadata");
        }
        Throws<ArgumentOutOfRangeException>(() => new EngineLoadException((EngineLoadFailure)0, "engine", "linux-x64", "invalid"));
        nuint owner = 0;
        Check(Create(&owner) == NativeStatus.Ok && owner != 0, "native fixture create");
        try
        {
            var record = new NwErrorInfoV1 { StructSize = 24 };
            Check(Run(owner, 1, &record) == NativeStatus.DomainError, "ordinary failure");
            Check(record.StructSize == 24 && record.Domain == 7 && record.Code == -42 && record.Flags == 0 && record.CorrelationId == 73, "native exact record");
            Check(Run(owner, 0, &record) == NativeStatus.Ok, "ordinary error does not poison");
            Check(Run(owner, 1, null) == NativeStatus.DomainError, "no-detail preserves status");
            for (uint capacity = 0; capacity < 24; capacity++)
            {
                record = new NwErrorInfoV1 { StructSize = capacity, Domain = 0xa5a5a5a5, Code = -42, Flags = uint.MaxValue, CorrelationId = ulong.MaxValue };
                Check(Run(owner, 1, &record) == NativeStatus.DomainError && record.StructSize == capacity && record.Domain == 0xa5a5a5a5 && record.Flags == uint.MaxValue, "short record unchanged");
            }
            record = new NwErrorInfoV1 { StructSize = 24 };
            Check(Run(owner, 2, &record) == NativeStatus.NativePanic, "native panic contained");
            var nativeError = NativeError.Create("engine", "execute", NativeStatus.NativePanic, record);
            Check(nativeError is NativeEngineException { StatusCode: NativeStatus.NativePanic }, "panic mapped outside callback");
            Check(Run(owner, 0, &record) == NativeStatus.EnginePoisoned, "new calls rejected after poison");
        }
        finally { Check(Release(owner) == NativeStatus.Ok, "trusted cleanup after poison"); }
        foreach (uint outcome in new uint[] { NativeStatus.Ok, NativeStatus.Canceled, NativeStatus.DomainError })
        {
            var latch = new CallbackFailureLatch(outcome);
            var handle = GCHandle.Alloc(latch);
            uint bridge = 0;
            try
            {
                Check(Notify(&OnTerminal, (nuint)GCHandle.ToIntPtr(handle), outcome, &bridge) == outcome, "callback fault preserves native terminal");
                Check(bridge == NativeStatus.CallbackFailed && latch.HasFailure && latch.CleanupSignals == 1 && latch.OriginalTerminalStatus == outcome, "callback fault latch and cleanup signal");
            }
            finally { handle.Free(); }
        }
        var valid = new NwErrorInfoV1 { StructSize = 24, Domain = 7, Code = -1, CorrelationId = 9 };
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Check(NativeError.Create("engine", "execute", NativeStatus.Canceled, valid, cancellation.Token) is OperationCanceledException e && e.CancellationToken == cancellation.Token, "exact cancellation token");
        Check(NativeError.Create("engine", "execute", NativeStatus.Timeout, valid) is TimeoutException, "timeout mapping");
        Check(NativeError.Create("engine", "execute", uint.MaxValue, valid) is NativeEngineException { StatusCode: uint.MaxValue, ErrorDomain: 7, NativeCode: -1, CorrelationId: 9 }, "unknown status remains failure");
        Console.WriteLine($"PASS A05 {_assertions} assertions; real native boundary; callback roots released.");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint OnTerminal(nuint cookie, uint terminal)
    {
        try
        {
            var latch = (CallbackFailureLatch)GCHandle.FromIntPtr((nint)cookie).Target!;
            return latch.Notify(static () => throw new InvalidOperationException("simulated managed bridge conversion failure"));
        }
        catch (Exception) { return NativeStatus.CallbackFailed; }
    }

    [LibraryImport("nativeweave_errors_fixture", EntryPoint = "nw_errors_create")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial uint Create(nuint* owner);
    [LibraryImport("nativeweave_errors_fixture", EntryPoint = "nw_errors_run")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial uint Run(nuint owner, uint mode, NwErrorInfoV1* error);
    [LibraryImport("nativeweave_errors_fixture", EntryPoint = "nw_errors_release")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial uint Release(nuint owner);
    [LibraryImport("nativeweave_errors_fixture", EntryPoint = "nw_errors_notify")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial uint Notify(delegate* unmanaged[Cdecl]<nuint, uint, uint> callback, nuint cookie, uint terminal, uint* bridge);
}
