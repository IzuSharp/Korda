using NativeWeave.Interop.Async;
using G = NativeWeave.Engines.AsyncProof.Generated;
using Abi = NativeWeave.Interop.Abi;

namespace NativeWeave.Engines.AsyncProof
{
    // A10's generated start/result hooks notify the actual A07 coordinator.
    // No second setup/terminal/retirement state machine is implemented here.
    internal sealed unsafe class OperationCalls(G.Owner_NwHttpOperation owner, bool read) : AsyncNativeCalls, G.IOperationSetup_Get, G.IOperationSetup_Read
    {
        internal G.Owner_NwHttpOperation Owner => owner;
        internal AsyncOperation Context { get; set; } = null!;
        internal override uint Start(nint pointer, in Abi.NwOperationCallbacksV1 callbacks)
        {
            OperationCalls? previous = G.BindingOperations.Current;
            G.BindingOperations.Current = this;
            try
            {
                fixed (Abi.NwOperationCallbacksV1* value = &callbacks)
                    return read ? G.Operation_Read.Start(owner, (G.NwOperationCallbacksV1*)value) : G.Operation_Get.Start(owner, (G.NwOperationCallbacksV1*)value);
            }
            finally { G.BindingOperations.Current = previous; }
        }
        internal override uint RequestCancel(nint pointer) => G.NativeMethods.nw_asyncproof_operation_cancel_v1(pointer);
        internal override uint TryRetire(nint pointer) => G.NativeMethods.nw_asyncproof_operation_retire_v1(pointer);
        internal override void Poison()
        {
            bool lease = false;
            try { G.NativeMethods.nw_asyncproof_operation_poison_v1(owner.Resource.Acquire(ref lease)); }
            finally { owner.Resource.Release(ref lease); }
        }
        public void StartExited(uint status, bool returned) => Context.Signal(); // A07 publishes exact setup outcome in its own finally
        public void MappingFailed() => Context.Dispose();
        public void RequestRetire() => Context.Dispose();
    }
}

namespace NativeWeave.Engines.AsyncProof.Generated
{
    internal static unsafe class BindingOperations
    {
        [ThreadStatic] internal static OperationCalls? Current;
        private static OperationCalls Prepare(Owner_NwHttpOperation owner)
        {
            OperationCalls current = Current ?? throw new InvalidOperationException("Generated operation start requires its A07 setup executor.");
            if (!ReferenceEquals(owner, current.Owner)) throw new InvalidOperationException("Generated setup has a different operation owner.");
            return current;
        }
        internal static IOperationSetup_Get Prepare_Get(Owner_NwHttpOperation owner, NwOperationCallbacksV1* callbacks) => Prepare(owner);
        internal static IOperationSetup_Read Prepare_Read(Owner_NwHttpOperation owner, NwOperationCallbacksV1* callbacks) => Prepare(owner);
    }
}
