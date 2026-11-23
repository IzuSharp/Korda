using System.Runtime.InteropServices;
using Korda.Interop.Lifetime;

namespace Korda.Interop;

/// <summary>Reads immutable native bytes synchronously while their creating owner is leased.</summary>
/// <param name="bytes">A read-only view valid only until this delegate returns.</param>
/// <remarks>The delegate runs on the calling thread outside admission locks. The span must not escape or cross an await. A cached static delegate avoids closure allocation.</remarks>
public delegate void NativeBufferReader(ReadOnlySpan<byte> bytes);

/// <summary>Projects a synchronous native read into independently owned managed data.</summary>
/// <typeparam name="TResult">A non-ref-like result that cannot retain the borrowed span in safe code.</typeparam>
/// <param name="bytes">A read-only view valid only until this delegate returns.</param>
/// <returns>A scalar or independently owned result.</returns>
/// <remarks>The callback executes outside admission locks with one owner lease. Exceptions propagate after that lease is released. No native callback or asynchronous execution is involved.</remarks>
public delegate TResult NativeBufferReader<TResult>(ReadOnlySpan<byte> bytes);

/// <summary>Owns immutable, address-stable native bytes through the creating module's destructor.</summary>
/// <remarks>
/// Concurrent reads are safe; callers synchronize access to shared destinations.
/// Reads use A03 logical admission and SafeHandle references without per-call
/// lease allocation. Disposal closes new admission but existing reads remain valid.
/// No public pointer, escaping span, native-backed Memory or generic allocator is
/// exposed. Native destruction must be nonblocking and safe on any release thread.
/// This BCL-only surface is AOT compatible; domain engines enforce their result limit.
/// </remarks>
public sealed class NativeBuffer : IDisposable
{
    private readonly NativeResourceOwner _owner;
    private readonly nint _address;

    internal NativeBuffer(EngineInfo engine, SafeHandle handle, nint address, int length)
    {
        // All validation and allocation must finish before the caller transfers
        // its sole SafeHandle responsibility on this constructor's successful return.
        _address = address;
        Length = length;
        _owner = new NativeResourceOwner(handle, engine);
    }

    /// <summary>Gets the immutable byte length, including after logical disposal.</summary>
    /// <remarks>O(1), no native call or allocation. Length is in [0, Int32.MaxValue].</remarks>
    public int Length { get; }
    /// <summary>Gets whether new reads are closed; this does not assert physical reclamation.</summary>
    /// <remarks>Thread-safe, O(1), no native call or allocation.</remarks>
    public bool IsDisposed => _owner.IsClosing;

    /// <summary>Copies all bytes into the beginning of a sufficiently large destination.</summary>
    /// <param name="destination">A destination with at least Length bytes; its remaining tail is unchanged.</param>
    /// <exception cref="ArgumentException">The destination is too short; nothing is written.</exception>
    /// <exception cref="ObjectDisposedException">New read admission is closed.</exception>
    /// <remarks>O(Length) overlap-safe copy, no managed allocation; one synchronous owner lease protects the entire copy.</remarks>
    public void CopyTo(Span<byte> destination)
    {
        if (destination.Length < Length) throw new ArgumentException("Destination is shorter than the native buffer.", nameof(destination));
        bool admitted = false;
        try { _ = _owner.Acquire(ref admitted); CopyLeased(0, destination[..Length]); }
        finally { _owner.Release(ref admitted); }
    }

    /// <summary>Copies a range whose byte count equals the destination length.</summary>
    /// <param name="sourceOffset">A zero-based byte offset; Length is valid for an empty destination.</param>
    /// <param name="destination">The exact requested range destination.</param>
    /// <exception cref="ArgumentOutOfRangeException">The offset or complete range is outside this buffer.</exception>
    /// <exception cref="ObjectDisposedException">New read admission is closed.</exception>
    /// <remarks>Checks range before admission without overflowing offset+length. O(destination.Length), overlap-safe, no allocation.</remarks>
    public void CopyTo(int sourceOffset, Span<byte> destination)
    {
        if ((uint)sourceOffset > (uint)Length || destination.Length > Length - sourceOffset)
            throw new ArgumentOutOfRangeException(nameof(sourceOffset), "The complete requested range must fit the buffer.");
        bool admitted = false;
        try { _ = _owner.Acquire(ref admitted); CopyLeased(sourceOffset, destination); }
        finally { _owner.Release(ref admitted); }
    }

    /// <summary>Copies all bytes only when the destination has sufficient capacity.</summary>
    /// <param name="destination">The caller-owned destination.</param>
    /// <returns>False only for a short destination, which is left untouched; otherwise true.</returns>
    /// <exception cref="ObjectDisposedException">New read admission is closed, even when the destination is short.</exception>
    /// <remarks>Always acquires admission before checking capacity. O(Length) on success, no allocation; native or contract faults are not swallowed.</remarks>
    public bool TryCopyTo(Span<byte> destination)
    {
        bool admitted = false;
        try
        {
            _ = _owner.Acquire(ref admitted);
            if (destination.Length < Length) return false;
            CopyLeased(0, destination[..Length]);
            return true;
        }
        finally { _owner.Release(ref admitted); }
    }

    /// <summary>Copies all native bytes into an independently owned managed array.</summary>
    /// <returns>A managed array unaffected by this buffer's later disposal; empty results may share Array.Empty.</returns>
    /// <exception cref="ObjectDisposedException">New read admission is closed.</exception>
    /// <exception cref="OutOfMemoryException">The managed array cannot be allocated; the lease is still released.</exception>
    /// <remarks>Admission precedes allocation. O(Length), one array allocation for nonempty data and one copy.</remarks>
    public byte[] ToArray()
    {
        bool admitted = false;
        try
        {
            _ = _owner.Acquire(ref admitted);
            byte[] result = Length == 0 ? Array.Empty<byte>() : new byte[Length];
            CopyLeased(0, result);
            return result;
        }
        finally { _owner.Release(ref admitted); }
    }

    /// <summary>Invokes a reader synchronously under one native-owner lease.</summary>
    /// <param name="reader">A non-null synchronous reader that must not retain its span.</param>
    /// <exception cref="ArgumentNullException">The reader is null.</exception>
    /// <exception cref="ObjectDisposedException">New read admission is closed.</exception>
    /// <remarks>No copy or native callback. Runs outside admission locks; the reader may Dispose this buffer and continue its current view. Reader exceptions propagate after release.</remarks>
    public void Read(NativeBufferReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        bool admitted = false;
        try { _ = _owner.Acquire(ref admitted); reader(LeasedSpan()); }
        finally { _owner.Release(ref admitted); }
    }

    /// <summary>Projects a synchronous leased read into a scalar or independently owned result.</summary>
    /// <typeparam name="TResult">A non-ref-like result; borrowed bytes cannot escape in safe code.</typeparam>
    /// <param name="reader">A non-null synchronous projection, invoked outside admission locks.</param>
    /// <returns>The reader's independently owned result.</returns>
    /// <exception cref="ArgumentNullException">The reader is null.</exception>
    /// <exception cref="ObjectDisposedException">New read admission is closed.</exception>
    /// <remarks>No byte copy is imposed. A cached static reader avoids delegate allocation; captured readers may allocate. Exceptions release the lease.</remarks>
    public TResult Read<TResult>(NativeBufferReader<TResult> reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        bool admitted = false;
        try { _ = _owner.Acquire(ref admitted); return reader(LeasedSpan()); }
        finally { _owner.Release(ref admitted); }
    }

    /// <summary>Copies through one bounded staging array while retaining the native source until all writes finish.</summary>
    /// <param name="destination">A writable stream, left open after success, failure or cancellation.</param>
    /// <param name="cancellationToken">Cancels the copy without undoing bytes already written.</param>
    /// <returns>A task for completion and the end of source/staging-buffer use.</returns>
    /// <exception cref="ArgumentNullException">The destination is null; thrown synchronously.</exception>
    /// <exception cref="ArgumentException">The destination is not writable; thrown synchronously.</exception>
    /// <exception cref="ObjectDisposedException">Source admission is closed; thrown synchronously after destination validation.</exception>
    /// <exception cref="OperationCanceledException">The returned task is canceled with the supplied token.</exception>
    /// <remarks>Validation is null, CanWrite, then source admission. Pre-cancellation returns a canceled task after admission, including for empty input, with no copy/write/staging allocation. An uncanceled empty input completes without writing. Nonempty work allocates exactly min(Length,65536) bytes once, copies each chunk synchronously, and awaits WriteAsync before reuse. One source lease spans the operation; no call-scoped span crosses await. Stream errors and cancellation can follow partial output and fault/cancel the task.</remarks>
    public Task CopyToAsync(Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite) throw new ArgumentException("The destination must be writable.", nameof(destination));
        bool admitted = false;
        try
        {
            _ = _owner.Acquire(ref admitted);
            if (cancellationToken.IsCancellationRequested) return Task.FromCanceled(cancellationToken);
            if (Length == 0) return Task.CompletedTask;
            Task copy = CopyToAsyncCore(destination, cancellationToken);
            admitted = false; // the async method owns the same single admission
            return copy;
        }
        finally { _owner.Release(ref admitted); }
    }

    private async Task CopyToAsyncCore(Stream destination, CancellationToken cancellationToken)
    {
        bool admitted = true;
        try
        {
            byte[] staging = new byte[Math.Min(Length, 65536)];
            int offset = 0;
            while (offset < Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = Math.Min(staging.Length, Length - offset);
                CopyLeased(offset, staging.AsSpan(0, count));
                await destination.WriteAsync(staging.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                offset += count;
            }
        }
        finally { _owner.Release(ref admitted); }
    }

    private unsafe ReadOnlySpan<byte> LeasedSpan() => Length == 0 ? ReadOnlySpan<byte>.Empty : new((void*)_address, Length);
    private void CopyLeased(int offset, Span<byte> destination) => LeasedSpan().Slice(offset, destination.Length).CopyTo(destination);

    /// <summary>Closes new read admission and drops the original creator-owner reference.</summary>
    /// <remarks>Thread-safe, nonblocking and idempotent. Already admitted reads or writes keep native memory alive until their own finally paths finish. The private SafeHandle supplies only a bounded fallback finalizer.</remarks>
    public void Dispose() => _owner.Dispose();
}
