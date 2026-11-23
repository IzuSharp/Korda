using System.Runtime.InteropServices;
using Korda.Interop.Memory;

namespace Korda.Interop.Extensibility;

/// <summary>A trusted engine-maintainer seam for adopting immutable native results.</summary>
/// <remarks>Ordinary applications receive buffers from typed domain operations. The caller guarantees creator/allocator pairing, one sole SafeHandle owner, stable initialized bytes, a negotiated per-result bound and nonthrowing, nonblocking free-threaded destruction. Shape validation cannot establish accessibility of an arbitrary address. The native module remains resident under the loader contract.</remarks>
public static class NativeBufferFactory
{
    /// <summary>Adopts the creating module's sole SafeHandle after all validation and wrapper allocation succeed.</summary>
    /// <param name="engine">Validated metadata for the owner’s creating module.</param>
    /// <param name="owner">An open valid unique SafeHandle containing the exact creator destructor.</param>
    /// <param name="address">The stable immutable byte address; zero is permitted only for empty data.</param>
    /// <param name="length">A nonnegative Int32-bounded length already checked against the domain's negotiated limit.</param>
    /// <returns>A buffer that assumes ownership on successful return; failure leaves ownership with the caller.</returns>
    /// <exception cref="ArgumentNullException">The engine or owner is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The byte length is negative.</exception>
    /// <exception cref="ArgumentException">The handle is closed/invalid or the contiguous address extent is malformed.</exception>
    /// <exception cref="OutOfMemoryException">Managed wrapper allocation fails; the caller still owns the supplied handle.</exception>
    /// <remarks>No byte copy, native allocation or allocator inference. Caller must not dispose or adopt the supplied SafeHandle again after success. On failure caller must release it. This seam cannot adopt mutable, thread-affine or asynchronously destroyed resources.</remarks>
    public static NativeBuffer Adopt(EngineInfo engine, SafeHandle owner, nint address, int length)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (owner.IsClosed || owner.IsInvalid) throw new ArgumentException("An open, valid, uniquely owned SafeHandle is required.", nameof(owner));
        if (!ExtentValidator.TryValidate((ulong)(nuint)address, (ulong)length, (ulong)length, 1, 0, (ulong)length, int.MaxValue, 1, out _))
            throw new ArgumentException("The contiguous native byte extent is invalid.", nameof(address));
        return new NativeBuffer(engine, owner, address, length);
    }
}
