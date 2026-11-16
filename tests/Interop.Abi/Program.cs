using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NativeWeave.Interop.Abi;

internal static unsafe partial class Program
{
    private static int assertions;

    [LibraryImport("nw_abi_conformance", EntryPoint = "nw_abi_conformance_get_abi_v1")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial uint GetAbi(uint requestedMajor, uint outCapacity, NwAbiInfoV1* output);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Interlocked.Increment(ref assertions);
    }

    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--layout")
        {
            Console.WriteLine(LayoutProbe.Read());
            return 0;
        }
        if (args.Length == 2 && args[0] == "--surface")
        {
            using var input = File.OpenRead(args[1]);
            using var pe = new PEReader(input);
            MetadataReader reader = pe.GetMetadataReader();
            foreach (TypeDefinitionHandle handle in reader.TypeDefinitions)
            {
                TypeDefinition type = reader.GetTypeDefinition(handle);
                TypeAttributes visibility = type.Attributes & TypeAttributes.VisibilityMask;
                Check(visibility is not TypeAttributes.Public and not TypeAttributes.NestedPublic,
                    "ABI plumbing must not be public: " + reader.GetString(type.Name));
            }
            Console.WriteLine("{\"public_types\":[],\"public_members\":[],\"raw_application_handles\":false}");
            return 0;
        }
        bool requireAot = args.Contains("--require-aot", StringComparer.Ordinal);
        Check(!requireAot || !RuntimeFeature.IsDynamicCodeSupported, "Actual Native AOT execution required.");
        Check(IntPtr.Size == 8 && BitConverter.IsLittleEndian, "This executed profile is little-endian x64.");
        Check(!RuntimeHelpers.IsReferenceOrContainsReferences<NwAbiInfoV1>(), "ABI info is blittable.");
        Check(!RuntimeHelpers.IsReferenceOrContainsReferences<NwErrorInfoV1>(), "Error info is blittable.");
        Check(!RuntimeHelpers.IsReferenceOrContainsReferences<NwBytesViewV1>(), "Bytes view is blittable.");
        Check(!RuntimeHelpers.IsReferenceOrContainsReferences<NwOperationCallbacksV1>(), "Operation callbacks are blittable.");
        Check(!RuntimeHelpers.IsReferenceOrContainsReferences<NwCallbacksV1>(), "Callbacks are blittable.");
        Check(sizeof(NwAbiInfoV1) == 32 && sizeof(NwErrorInfoV1) == 24 && sizeof(NwBytesViewV1) == 16 &&
            sizeof(NwOperationCallbacksV1) == 24 && sizeof(NwCallbacksV1) == 24, "Complete record sizes.");
        BootstrapCases();
        ValueCases();
        Parallel.For(0, 128, static _ =>
        {
            NwAbiInfoV1 info = default;
            Check(GetAbi(1, 32, &info) == NativeStatus.Ok, "Concurrent stateless bootstrap.");
            Check(AbiBootstrap.Validate(info, 32, 1, 0, 1) == NativeStatus.Ok, "Concurrent complete record.");
        });
        Console.WriteLine($"{{\"passed\":true,\"assertions\":{assertions},\"native_aot\":{(!RuntimeFeature.IsDynamicCodeSupported).ToString().ToLowerInvariant()},\"native_fixture\":\"test-only engine over product ABI writer\",\"domain_owners_created\":0}}");
        return 0;
    }

    private static void BootstrapCases()
    {
        ulong* aligned = stackalloc ulong[8];
        Span<byte> storage = new(aligned, 64);
        for (uint capacity = 0; capacity < 32; capacity++)
        {
            storage.Fill(0xa5);
            Check(GetAbi(1, capacity, (NwAbiInfoV1*)aligned) == NativeStatus.BufferTooSmall, "Short prefix rejected.");
            Check(storage.IndexOfAnyExcept((byte)0xa5) < 0, "Short capacity writes nothing.");
        }
        foreach (uint major in new uint[] { 0, 2, uint.MaxValue })
        {
            storage.Fill(0xa5);
            Check(GetAbi(major, 64, (NwAbiInfoV1*)aligned) == NativeStatus.NotSupported, "Major rejected.");
            Check(storage.IndexOfAnyExcept((byte)0xa5) < 0, "Unsupported major writes nothing.");
        }
        Check(GetAbi(1, 32, null) == NativeStatus.InvalidArgument, "Null writes nothing.");
        storage.Fill(0xa5);
        Check(GetAbi(1, 32, (NwAbiInfoV1*)((byte*)aligned + 1)) == NativeStatus.InvalidArgument, "Valid-memory misalignment rejected.");
        Check(storage.IndexOfAnyExcept((byte)0xa5) < 0, "Misalignment writes nothing.");
        foreach (uint capacity in new uint[] { 32, 33, 64 })
        {
            storage.Fill(0xa5);
            Check(GetAbi(1, capacity, (NwAbiInfoV1*)aligned) == NativeStatus.Ok, "Complete prefix accepted.");
            NwAbiInfoV1 info = *(NwAbiInfoV1*)aligned;
            Check(info.StructSize == 32 && info.AbiMajor == 1 && info.AbiMinor == 0 && info.PointerWidthBits == 64,
                "Reported complete version/profile.");
            Check(info.Capabilities == 0x8000_0000_0000_0001 && info.Reserved == 0, "Capabilities and reserved bytes.");
            Check(storage[32..].IndexOfAnyExcept((byte)0xa5) < 0, "Larger capacity leaves unknown tail untouched.");
            Check(AbiBootstrap.Validate(info, capacity, 1, 0, 1) == NativeStatus.Ok, "Actual record validated.");
            Check(AbiBootstrap.Validate(info, capacity, 1, 0, 2) == NativeStatus.NotSupported, "Unknown required capability fails before owner publication.");
            Check(AbiBootstrap.Validate(info, capacity, 1, 1, 0) == NativeStatus.NotSupported, "Insufficient minor rejected.");
            Check(AbiBootstrap.Validate(info, capacity, 2, 0, 0) == NativeStatus.NotSupported, "Caller incompatible major rejected.");
            info.PointerWidthBits = 32;
            Check(AbiBootstrap.Validate(info, capacity, 1, 0, 0) == NativeStatus.NotSupported, "Different architecture rejected.");
            info.PointerWidthBits = 64;
            info.AbiMajor = 2;
            Check(AbiBootstrap.Validate(info, capacity, 1, 0, 0) == NativeStatus.NotSupported, "Provider incompatible major rejected.");
            info.AbiMajor = 1;
            info.Reserved = 1;
            Check(AbiBootstrap.Validate(info, capacity, 1, 0, 0) == NativeStatus.ContractViolation, "Nonzero reserved rejected.");
            info.Reserved = 0;
            info.StructSize = 31;
            Check(AbiBootstrap.Validate(info, capacity, 1, 0, 0) == NativeStatus.ContractViolation, "Absent complete prefix rejected.");
            info.StructSize = 32;
            Check(AbiBootstrap.Validate(info, 31, 1, 0, 0) == NativeStatus.ContractViolation, "Short caller capacity rejected.");
            info.StructSize = 40;
            Check(AbiBootstrap.Validate(info, capacity, 1, 0, 1) == NativeStatus.Ok, "Known complete prefix of future provider allowed.");
        }
    }

    private static void ValueCases()
    {
        ReadOnlySpan<uint> statuses = [NativeStatus.Ok, NativeStatus.Pending, NativeStatus.Canceled,
            NativeStatus.InvalidArgument, NativeStatus.InvalidState, NativeStatus.OutOfMemory,
            NativeStatus.NotSupported, NativeStatus.BufferTooSmall, NativeStatus.CallbackFailed,
            NativeStatus.NativePanic, NativeStatus.EnginePoisoned, NativeStatus.ContractViolation,
            NativeStatus.Timeout, NativeStatus.DomainError];
        for (int index = 0; index < statuses.Length; index++)
            Check(statuses[index] == (uint)index, "Every canonical D08 scalar status number.");
        NwAbiInfoV1 abi = default;
        NwErrorInfoV1 error = default;
        NwOperationCallbacksV1 operation = default;
        NwCallbacksV1 callbacks = default;
        Check(WireReservedFields.AreZero(abi) && WireReservedFields.AreZero(error) &&
            WireReservedFields.AreZero(operation) && WireReservedFields.AreZero(callbacks), "Zero reserved fields accepted.");
        abi.Reserved = ulong.MaxValue;
        error.Flags = uint.MaxValue;
        operation.Flags = 1;
        callbacks.Flags = 0x8000_0000;
        Check(!WireReservedFields.AreZero(abi) && !WireReservedFields.AreZero(error) &&
            !WireReservedFields.AreZero(operation) && !WireReservedFields.AreZero(callbacks), "Unknown reserved bits rejected in every record.");
        for (int value = 0; value <= byte.MaxValue; value++)
            Check(AbiBootstrap.IsBooleanValid((byte)value) == (value is 0 or 1), "Fixed Boolean storage policy.");
        for (uint status = 0; status <= NativeStatus.DomainError; status++)
        {
            Check(AbiBootstrap.IsStatusValid(status, true), "Known try status recognized.");
            Check(AbiBootstrap.IsStatusValid(status) == (status != NativeStatus.Pending), "Unexpected PENDING rejected.");
        }
        foreach (uint status in new uint[] { 14, 255, uint.MaxValue })
            Check(!AbiBootstrap.IsStatusValid(status, true), "Unknown status is never success.");
    }
}
