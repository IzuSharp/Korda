using System.Runtime.InteropServices;
using NativeWeave.Interop.Lifetime;

namespace NativeWeave.Server;

/// <summary>Owns one immutable native request-body chunk independently of its request context.</summary>
/// <remarks>Dispose every chunk. The creating native module retains its full backing allocation and
/// request/global credit until the last native owner is released. Copies use Core logical admission
/// and SafeHandle leases; a concurrent Dispose closes new copies without invalidating an admitted
/// one. No pointer, borrowed span or native-backed Memory escapes. Chunk ownership may outlive
/// dispatch; it does not extend access to request services, borrowed streams or metadata.</remarks>
public sealed class BodyChunk : IDisposable
{
    private readonly BodyChunkOwner _owner;

    internal BodyChunk(BodyChunkOwner owner) => _owner = owner ?? throw new ArgumentNullException(nameof(owner));

    /// <summary>Gets the positive visible byte length while this chunk remains open.</summary>
    /// <exception cref="ObjectDisposedException">The chunk has been disposed.</exception>
    /// <remarks>O(1), no payload allocation. A slice can keep a larger backing allocation charged.</remarks>
    public int Length => _owner.Length;

    /// <summary>Copies this complete chunk into the start of caller-owned memory.</summary>
    /// <param name="destination">At least Length bytes; an unused tail is unchanged.</param>
    /// <exception cref="ObjectDisposedException">New copy admission is closed.</exception>
    /// <exception cref="ArgumentException">The destination is too short; no bytes are written.</exception>
    /// <remarks>O(Length), one bounded copy, no new framework payload allocation. The caller controls
    /// the destination's retention. Native ownership remains valid for the entire synchronous call.</remarks>
    public void CopyTo(Span<byte> destination) => _owner.CopyTo(destination);

    /// <summary>Copies the chunk into a new, independently owned managed array.</summary>
    /// <returns>A caller-owned array, valid after the chunk is disposed.</returns>
    /// <exception cref="ObjectDisposedException">New copy admission is closed.</exception>
    /// <exception cref="InvalidOperationException">The engine rejects the bounded copy reservation.</exception>
    /// <exception cref="OutOfMemoryException">Array allocation fails; all temporary credits still release.</exception>
    /// <remarks>Acquires managed request/global credit without waiting before allocating. Native and
    /// managed copies are both charged during overlap, including after dispatch has ended. The credit
    /// is released when the array transfers to the caller; arbitrary caller retention is outside the
    /// framework ledger. O(Length), one array and one copy.</remarks>
    public byte[] ToArray() => _owner.ToArray();

    /// <summary>Closes new copy admission and releases the creating module's owner exactly once.</summary>
    /// <remarks>Thread-safe, nonblocking and idempotent. An admitted copy keeps the owner alive until
    /// its finally path releases the Core lease. This does not dispose the parent request or server.</remarks>
    public void Dispose() => _owner.Dispose();
}

/// <summary>Engine-owned chunk seam; concrete implementations invoke the creating module's ABI.</summary>
internal abstract class BodyChunkOwner : IDisposable
{
    private readonly NativeResourceOwner _owner;
    private readonly int _length;

    protected BodyChunkOwner(SafeHandle handle, int length, object? lifetimeRoot = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        _length = length;
        _owner = new NativeResourceOwner(handle, lifetimeRoot);
    }

    internal int Length
    {
        get
        {
            bool admitted = false;
            try { _ = _owner.Acquire(ref admitted); return _length; }
            finally { _owner.Release(ref admitted); }
        }
    }

    internal void CopyTo(Span<byte> destination)
    {
        bool admitted = false;
        try
        {
            nint pointer = _owner.Acquire(ref admitted);
            if (destination.Length < _length) throw new ArgumentException("Destination is shorter than the body chunk.", nameof(destination));
            CopyLeased(pointer, destination[.._length]);
        }
        finally { _owner.Release(ref admitted); }
    }

    internal byte[] ToArray()
    {
        bool admitted = false;
        try
        {
            nint pointer = _owner.Acquire(ref admitted);
            using IDisposable credit = AcquireManagedCreditLeased(pointer, _length);
            byte[] destination = new byte[_length];
            CopyLeased(pointer, destination);
            return destination;
        }
        finally { _owner.Release(ref admitted); }
    }

    protected abstract void CopyLeased(nint pointer, Span<byte> destination);
    protected abstract IDisposable AcquireManagedCreditLeased(nint pointer, int capacity);
    public void Dispose() => _owner.Dispose();
}
