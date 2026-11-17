using System.Runtime.InteropServices;

namespace NativeWeave.Generated.Conformance;

// Fixed generator conformance fixture. It does not implement the A07 runtime,
// callback registration/drain races, queues, timers or an application Host.
internal sealed class CallbackContext
{
    internal bool ThrowDispatch, ThrowWake, CompletedBeforeStartReturn;
    internal uint OriginalNativeStatus;
    internal int Faults, CleanupSignals;
    internal FixtureSetup? Setup;
}

internal static class BindingCallbacks
{
    private static CallbackContext Context(nuint cookie) => (CallbackContext)GCHandle.FromIntPtr((nint)cookie).Target!;
    internal static uint Dispatch_nw_exports_notification_v1(nuint cookie, uint status)
    {
        var context = Context(cookie);
        context.CompletedBeforeStartReturn = !context.Setup!.StartReturned && context.Setup.Owner.Resource.ActiveLeases == 2;
        if (status != context.OriginalNativeStatus) throw new InvalidOperationException("Native terminal status drift.");
        if (context.ThrowDispatch) throw new InvalidOperationException("Injected bridge mapping failure.");
        return 0;
    }
    internal static void Fault(nuint cookie) { Context(cookie).Faults++; }
    internal static void Wake(nuint cookie)
    {
        var context = Context(cookie);
        context.CleanupSignals++;
        if (context.ThrowWake) throw new InvalidOperationException("Injected wake adapter failure.");
    }
}

internal static class BindingOperations
{
    internal static IOperationSetup_fixture Prepare_fixture(Owner_NwFixtureOwner owner, nuint callback, nuint cookie, uint status)
    {
        if (callback == 0) throw new ArgumentException("Fixed fixture callback is required.", nameof(callback));
        var context = (CallbackContext)GCHandle.FromIntPtr((nint)cookie).Target!;
        if (context.OriginalNativeStatus != status) throw new ArgumentException("Mismatched notification status.", nameof(status));
        var setup = new FixtureSetup(owner);
        context.Setup = setup;
        return setup;
    }
}

internal sealed class FixtureSetup : IOperationSetup_fixture
{
    internal readonly Owner_NwFixtureOwner Owner;
    private bool retained;
    private readonly nint address;
    internal bool StartReturned, Retired;
    internal int MapperFailures, RetirementRequests;
    internal Owner_NwFixtureOwner? Unpublished;
    internal FixtureSetup(Owner_NwFixtureOwner owner)
    {
        Owner = owner;
        address = owner.Resource.Acquire(ref retained);
    }
    public void StartExited(uint status, bool returned)
    {
        StartReturned = returned;
        if (!returned || status is not (0 or 8)) throw new InvalidOperationException("Unexpected fixed start outcome.");
    }
    public void MappingFailed()
    {
        MapperFailures++;
        Unpublished?.Dispose();
        Unpublished = null;
    }
    public void RequestRetire()
    {
        RetirementRequests++;
        if (!StartReturned || NativeMethods.nw_exports_close_v1(address) != 0 || NativeMethods.nw_exports_drain_v1(address) != 0)
            throw new InvalidOperationException("Fixed fixture has not proved its synchronous quiet point.");
        Unpublished?.Dispose();
        Unpublished = null;
        Owner.Dispose();
        Owner.Resource.Release(ref retained);
        Retired = Owner.Resource.Retirement.IsCompletedSuccessfully;
    }
}
