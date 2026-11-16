using System.Runtime.CompilerServices;

namespace NativeWeave.Interop.Abi;

/// <summary>Checks the copied common bootstrap before any domain owner is created.</summary>
/// <remarks>
/// This is an internal wire check, not A02's loader or public capability API. It
/// neither loads a module nor allocates, creates a runtime or transfers ownership.
/// The caller must first check the scalar status and copy a complete initialized
/// v1 prefix from valid storage. Unknown extra capabilities remain uninterpreted.
/// </remarks>
internal static class AbiBootstrap
{
    /// <summary>Validates a complete v1 prefix against the caller's requirements.</summary>
    /// <param name="info">An initialized known prefix of the provider's record.</param>
    /// <param name="outCapacity">The actual caller output capacity used for the call.</param>
    /// <param name="requestedMajor">The required major; only v1 is understood here.</param>
    /// <param name="minimumMinor">The minimum compatible minor revision.</param>
    /// <param name="requiredCapabilities">Required engine-local capability bits.</param>
    /// <returns>OK on agreement, NOT_SUPPORTED on an incompatible profile or requirement, otherwise CONTRACT_VIOLATION.</returns>
    internal static uint Validate(in NwAbiInfoV1 info, uint outCapacity, uint requestedMajor,
        uint minimumMinor, ulong requiredCapabilities)
    {
        if (outCapacity < Unsafe.SizeOf<NwAbiInfoV1>() || info.StructSize < Unsafe.SizeOf<NwAbiInfoV1>())
            return NativeStatus.ContractViolation;
        if (requestedMajor != 1 || info.AbiMajor != requestedMajor || info.AbiMinor < minimumMinor ||
            info.PointerWidthBits != 64 || info.PointerWidthBits != IntPtr.Size * 8 || !BitConverter.IsLittleEndian ||
            (info.Capabilities & requiredCapabilities) != requiredCapabilities)
            return NativeStatus.NotSupported;
        return info.Reserved == 0 ? NativeStatus.Ok : NativeStatus.ContractViolation;
    }

    /// <summary>Checks a v1 status without treating unknown nonzero values as success.</summary>
    /// <param name="status">The unmodified scalar returned by native code.</param>
    /// <param name="permitsPending">Whether the exact operation is a declared try/poll operation.</param>
    /// <returns>True for a known status permitted by that operation's shape.</returns>
    internal static bool IsStatusValid(uint status, bool permitsPending = false) =>
        status <= NativeStatus.DomainError && (permitsPending || status != NativeStatus.Pending);

    /// <summary>Rejects wire Boolean values other than zero and one.</summary>
    internal static bool IsBooleanValid(byte value) => value <= 1;
}
