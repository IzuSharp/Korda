using System.Buffers.Binary;
using System.Runtime.InteropServices;
using NativeWeave.Interop;
using NativeWeave.Interop.Abi;
using NativeWeave.Interop.Callbacks;
using NativeWeave.Interop.Extensibility;
using NativeWeave.Interop.Lifetime;

namespace NativeWeave.Verification.ExtensionProof;

/// <summary>Internal Linux owner-pump probe, reusing A02/A03/A06 instead of exposing a GUI API.</summary>
/// <remarks>Construct and pump on one actual OS thread. Dispose closes admission without waiting.
/// Actual native Rc destruction requires managed/native callback and control retirement followed by
/// an owner pump. An unprogressed owner remains ClosePending; no finalizer touches affine state.</remarks>
internal sealed class AffinityProbe : IDisposable
{
    private readonly CallbackCoordinator _coordinator;
    private readonly CallbackRegistration _registration;
    private readonly NativeResourceOwner _observer;
    private bool _observerDisposed;
    private static readonly Lazy<EngineDescriptor> Package = new(CreateDescriptor);
    internal static EngineDescriptor Descriptor => Package.Value;
    internal bool CallbacksRetired => _registration.Retirement.IsCompletedSuccessfully;
    /// <summary>Observes handler/bridge failure after its separate cleanup has actually completed.</summary>
    internal Task Completion => _registration.Completion;
    internal int RetainedCallbacks => _registration.Snapshot.RetainedItems;
    internal bool CallbackRootLive => _registration.Snapshot.RootLive;
    internal bool ClosePending => Snapshot.Retired == 0;

    /// <summary>Creates bounded native state on this thread; Start explicitly registers notifications.</summary>
    internal AffinityProbe(int capacity = 4, Func<ulong, ValueTask>? notification = null)
    {
        if (capacity is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(capacity));
        EngineInfo engine = Descriptor.Inspect();
        engine.Require(new EngineCapability("affinity.owner.v1"));
        _coordinator = new CallbackCoordinator(1);
        nint raw = 0, observerRaw = 0;
        ProbeHandle? handle = null, observerHandle = null;
        NativeResourceOwner? owner = null, observer = null;
        try
        {
            Require(Native.Create((uint)capacity, out raw, out observerRaw), "create");
            handle = new ProbeHandle(raw, observer: false); raw = 0;
            observerHandle = new ProbeHandle(observerRaw, observer: true); observerRaw = 0;
            owner = new NativeResourceOwner(handle, engine);
            observer = new NativeResourceOwner(observerHandle, engine);
            _registration = new CallbackRegistration(owner, new Calls(), _coordinator, 8, 64, 8,
                handler: (_, bytes) => notification is null ? ValueTask.CompletedTask : notification(BinaryPrimitives.ReadUInt64LittleEndian(bytes.Span)));
            _observer = observer;
        }
        catch
        {
            // No callback can have started here: Start is an explicit later call.
            if (owner is not null) owner.Dispose(); else handle?.Dispose();
            if (raw != 0) Native.Release(raw);
            nint progress = observerRaw != 0 ? observerRaw : observerHandle?.DangerousGetHandle() ?? 0;
            if (progress != 0) Native.Pump(progress, 1);
            if (observer is not null) observer.Dispose(); else observerHandle?.Dispose();
            if (observerRaw != 0) Native.ReleaseObserver(observerRaw);
            _coordinator.Dispose();
            throw;
        }
    }

    internal void Start() => _registration.Start();
    internal AffinitySnapshot Snapshot
    {
        get
        {
            bool lease = false;
            try { Require(Native.Snapshot(_observer.Acquire(ref lease), out AffinitySnapshot value), "snapshot"); return value; }
            finally { _observer.Release(ref lease); }
        }
    }
    internal uint Read(out ulong value)
    {
        bool lease = false;
        try { return Native.Read(_observer.Acquire(ref lease), out value); }
        finally { _observer.Release(ref lease); }
    }
    internal uint Pump(uint budget = 1)
    {
        bool lease = false;
        try { return Native.Pump(_observer.Acquire(ref lease), budget); }
        finally { _observer.Release(ref lease); }
    }
    /// <summary>Only OK transfers the work; full or closed preserves the caller's original owner.</summary>
    internal uint TryPost(OwnedWork work)
    {
        bool lease = false;
        try { return work.TryTransfer(_registration.AcquireControl(ref lease)); }
        catch (ObjectDisposedException) { return NativeStatus.InvalidState; }
        finally { _registration.ReleaseControl(ref lease); }
    }
    /// <summary>Test seam holds the actual A03/A06 control lease outside both admission gates.</summary>
    internal void HoldControl(Action action)
    {
        bool lease = false;
        try { _registration.AcquireControl(ref lease); action(); }
        finally { _registration.ReleaseControl(ref lease); }
    }
    /// <summary>Logical close only. The explicit owner loop and last handler retain cleanup obligations.</summary>
    public void Dispose() { _registration.Dispose(); _coordinator.Dispose(); }
    /// <summary>Releases the Send/Sync observer after actual affine destruction; no force-free path exists.</summary>
    internal bool TryFinishClose()
    {
        if (_observerDisposed) return true;
        if (!CallbacksRetired || Snapshot.Retired == 0) return false;
        _observer.Dispose(); _observerDisposed = true; return true;
    }
    internal static void Require(uint status, string operation)
    {
        if (status != NativeStatus.Ok) throw new NativeEngineException("nativeweave.verification.extensionproof", operation, status, 0, 0, 0, "The internal affinity probe rejected this operation.");
    }
    private static EngineDescriptor CreateDescriptor()
    {
        using Stream stream = typeof(AffinityProbe).Assembly.GetManifestResourceStream("NativeWeave.Verification.ExtensionProof.native-sha256")
            ?? throw new InvalidOperationException("Missing prebuilt extension identity.");
        using var reader = new StreamReader(stream);
        return EnginePackage.CreateDescriptor(new EnginePackageManifest("nativeweave.verification.extensionproof", "NativeWeave.Verification.ExtensionProof", "0.1.0-proof.1",
            new AbiVersion(1, 0), "nw_extension_get_abi_v1", EngineThreadingModel.ExternalLoop,
            [new NativeAssetDescriptor("linux-x64", "runtimes/linux-x64/native/libnativeweave_engine_extensionproof.so", reader.ReadToEnd().Trim().ToLowerInvariant(), "x86-64")],
            [new EngineCapabilityDefinition(new EngineCapability("affinity.owner.v1"), 0)]), typeof(AffinityProbe).Assembly);
    }
    private sealed unsafe class Calls : CallbackNativeCalls
    {
        internal override uint Start(nint subscription, in NwCallbacksV1 callbacks) => Native.Start(subscription, in callbacks);
        internal override uint RequestStop(nint subscription) => Native.Stop(subscription);
        internal override uint TryRetire(nint subscription) => Native.Retire(subscription);
    }
}

/// <summary>Physical Send/Sync control references; finalizer release only requests native owner progress.</summary>
internal sealed class ProbeHandle : SafeHandle
{
    private readonly bool _observer;
    internal ProbeHandle(nint pointer, bool observer) : base(0, ownsHandle: true) { _observer = observer; SetHandle(pointer); }
    public override bool IsInvalid => handle == 0;
    protected override bool ReleaseHandle() { if (_observer) Native.ReleaseObserver(handle); else Native.Release(handle); return true; }
}

/// <summary>A fixed native command with one success-only ownership transfer and A03 physical leases.</summary>
internal sealed class OwnedWork : IDisposable
{
    private readonly WorkHandle _handle;
    private readonly NativeResourceOwner _owner;
    private int _transfer;
    internal bool Transferred => Volatile.Read(ref _transfer) == 2;
    internal OwnedWork(ulong value)
    {
        _ = AffinityProbe.Descriptor.Inspect();
        AffinityProbe.Require(Native.NewWork(value, out nint raw), "create_work");
        WorkHandle? handle = null;
        try { handle = new(raw); _owner = new NativeResourceOwner(handle); _handle = handle; }
        catch { if (handle is null) Native.ReleaseWork(raw); else handle.Dispose(); throw; }
    }
    internal uint TryTransfer(nint target)
    {
        if (Interlocked.CompareExchange(ref _transfer, 1, 0) != 0) return NativeStatus.InvalidState;
        bool lease = false;
        try
        {
            uint status = Native.Post(target, _owner.Acquire(ref lease));
            if (status == NativeStatus.Ok)
            {
                _handle.SetHandleAsInvalid(); // Native queue now owns the only destruction obligation.
                Volatile.Write(ref _transfer, 2);
                _owner.Dispose();
            }
            return status;
        }
        finally { _owner.Release(ref lease); Interlocked.CompareExchange(ref _transfer, 0, 1); }
    }
    public void Dispose() => _owner.Dispose();
    private sealed class WorkHandle : SafeHandle
    {
        internal WorkHandle(nint value) : base(0, true) => SetHandle(value);
        public override bool IsInvalid => handle == 0;
        protected override bool ReleaseHandle() { Native.ReleaseWork(handle); return true; }
    }
}
