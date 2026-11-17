# NativeWeave.Interop buffers API reference

Generated from the compiled product assembly and its XML comments by `tests/Interop.Memory/ApiCheck`. A04 immutable ownership, scoped reads and bounded asynchronous copies; native allocation/destruction belongs to the creating module.

## `M:NativeWeave.Interop.Extensibility.NativeBufferFactory.Adopt(NativeWeave.Interop.EngineInfo,System.Runtime.InteropServices.SafeHandle,System.IntPtr,System.Int32)`

```csharp
public static NativeWeave.Interop.NativeBuffer NativeWeave.Interop.Extensibility.NativeBufferFactory.Adopt(NativeWeave.Interop.EngineInfo engine, System.Runtime.InteropServices.SafeHandle owner, nint address, int length)
```

**summary:** Adopts the creating module's sole SafeHandle after all validation and wrapper allocation succeed.

**param engine:** Validated metadata for the owner’s creating module.

**param owner:** An open valid unique SafeHandle containing the exact creator destructor.

**param address:** The stable immutable byte address; zero is permitted only for empty data.

**param length:** A nonnegative Int32-bounded length already checked against the domain's negotiated limit.

**returns:** A buffer that assumes ownership on successful return; failure leaves ownership with the caller.

**exception:** The engine or owner is null.

**exception:** The byte length is negative.

**exception:** The handle is closed/invalid or the contiguous address extent is malformed.

**exception:** Managed wrapper allocation fails; the caller still owns the supplied handle.

**remarks:** No byte copy, native allocation or allocator inference. Caller must not dispose or adopt the supplied SafeHandle again after success. On failure caller must release it. This seam cannot adopt mutable, thread-affine or asynchronously destroyed resources.

## `M:NativeWeave.Interop.NativeBuffer.CopyTo(System.Int32,System.Span{System.Byte})`

```csharp
public void NativeWeave.Interop.NativeBuffer.CopyTo(int sourceOffset, System.Span<byte> destination)
```

**summary:** Copies a range whose byte count equals the destination length.

**param sourceOffset:** A zero-based byte offset; Length is valid for an empty destination.

**param destination:** The exact requested range destination.

**exception:** The offset or complete range is outside this buffer.

**exception:** New read admission is closed.

**remarks:** Checks range before admission without overflowing offset+length. O(destination.Length), overlap-safe, no allocation.

## `M:NativeWeave.Interop.NativeBuffer.CopyTo(System.Span{System.Byte})`

```csharp
public void NativeWeave.Interop.NativeBuffer.CopyTo(System.Span<byte> destination)
```

**summary:** Copies all bytes into the beginning of a sufficiently large destination.

**param destination:** A destination with at least Length bytes; its remaining tail is unchanged.

**exception:** The destination is too short; nothing is written.

**exception:** New read admission is closed.

**remarks:** O(Length) overlap-safe copy, no managed allocation; one synchronous owner lease protects the entire copy.

## `M:NativeWeave.Interop.NativeBuffer.CopyToAsync(System.IO.Stream,System.Threading.CancellationToken)`

```csharp
public System.Threading.Tasks.Task NativeWeave.Interop.NativeBuffer.CopyToAsync(System.IO.Stream destination, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken))
```

**summary:** Copies through one bounded staging array while retaining the native source until all writes finish.

**param destination:** A writable stream, left open after success, failure or cancellation.

**param cancellationToken:** Cancels the copy without undoing bytes already written.

**returns:** A task for completion and the end of source/staging-buffer use.

**exception:** The destination is null; thrown synchronously.

**exception:** The destination is not writable; thrown synchronously.

**exception:** Source admission is closed; thrown synchronously after destination validation.

**exception:** The returned task is canceled with the supplied token.

**remarks:** Validation is null, CanWrite, then source admission. Pre-cancellation returns a canceled task after admission, including for empty input, with no copy/write/staging allocation. An uncanceled empty input completes without writing. Nonempty work allocates exactly min(Length,65536) bytes once, copies each chunk synchronously, and awaits WriteAsync before reuse. One source lease spans the operation; no call-scoped span crosses await. Stream errors and cancellation can follow partial output and fault/cancel the task.

## `M:NativeWeave.Interop.NativeBuffer.Dispose`

```csharp
public void NativeWeave.Interop.NativeBuffer.Dispose()
```

**summary:** Closes new read admission and drops the original creator-owner reference.

**remarks:** Thread-safe, nonblocking and idempotent. Already admitted reads or writes keep native memory alive until their own finally paths finish. The private SafeHandle supplies only a bounded fallback finalizer.

## `M:NativeWeave.Interop.NativeBuffer.Read(NativeWeave.Interop.NativeBufferReader)`

```csharp
public void NativeWeave.Interop.NativeBuffer.Read(NativeWeave.Interop.NativeBufferReader reader)
```

**summary:** Invokes a reader synchronously under one native-owner lease.

**param reader:** A non-null synchronous reader that must not retain its span.

**exception:** The reader is null.

**exception:** New read admission is closed.

**remarks:** No copy or native callback. Runs outside admission locks; the reader may Dispose this buffer and continue its current view. Reader exceptions propagate after release.

## `M:NativeWeave.Interop.NativeBuffer.Read``1(NativeWeave.Interop.NativeBufferReader{``0})`

```csharp
public TResult NativeWeave.Interop.NativeBuffer.Read<TResult>(NativeWeave.Interop.NativeBufferReader<TResult> reader)
```

**summary:** Projects a synchronous leased read into a scalar or independently owned result.

**typeparam TResult:** A non-ref-like result; borrowed bytes cannot escape in safe code.

**param reader:** A non-null synchronous projection, invoked outside admission locks.

**returns:** The reader's independently owned result.

**exception:** The reader is null.

**exception:** New read admission is closed.

**remarks:** No byte copy is imposed. A cached static reader avoids delegate allocation; captured readers may allocate. Exceptions release the lease.

## `M:NativeWeave.Interop.NativeBuffer.ToArray`

```csharp
public byte[] NativeWeave.Interop.NativeBuffer.ToArray()
```

**summary:** Copies all native bytes into an independently owned managed array.

**returns:** A managed array unaffected by this buffer's later disposal; empty results may share Array.Empty.

**exception:** New read admission is closed.

**exception:** The managed array cannot be allocated; the lease is still released.

**remarks:** Admission precedes allocation. O(Length), one array allocation for nonempty data and one copy.

## `M:NativeWeave.Interop.NativeBuffer.TryCopyTo(System.Span{System.Byte})`

```csharp
public bool NativeWeave.Interop.NativeBuffer.TryCopyTo(System.Span<byte> destination)
```

**summary:** Copies all bytes only when the destination has sufficient capacity.

**param destination:** The caller-owned destination.

**returns:** False only for a short destination, which is left untouched; otherwise true.

**exception:** New read admission is closed, even when the destination is short.

**remarks:** Always acquires admission before checking capacity. O(Length) on success, no allocation; native or contract faults are not swallowed.

## `M:NativeWeave.Interop.NativeBufferReader.#ctor(System.Object,System.IntPtr)`

```csharp
public NativeWeave.Interop.NativeBufferReader.NativeBufferReader(object @object, nint method)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

## `M:NativeWeave.Interop.NativeBufferReader.BeginInvoke(System.ReadOnlySpan{System.Byte},System.AsyncCallback,System.Object)`

```csharp
public virtual System.IAsyncResult NativeWeave.Interop.NativeBufferReader.BeginInvoke(System.ReadOnlySpan<byte> bytes, System.AsyncCallback callback, object @object)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

## `M:NativeWeave.Interop.NativeBufferReader.EndInvoke(System.IAsyncResult)`

```csharp
public virtual void NativeWeave.Interop.NativeBufferReader.EndInvoke(System.IAsyncResult result)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

## `M:NativeWeave.Interop.NativeBufferReader.Invoke(System.ReadOnlySpan{System.Byte})`

```csharp
public virtual void NativeWeave.Interop.NativeBufferReader.Invoke(System.ReadOnlySpan<byte> bytes)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

## `M:NativeWeave.Interop.NativeBufferReader`1.#ctor(System.Object,System.IntPtr)`

```csharp
public NativeWeave.Interop.NativeBufferReader<TResult>.NativeBufferReader(object @object, nint method)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

## `M:NativeWeave.Interop.NativeBufferReader`1.BeginInvoke(System.ReadOnlySpan{System.Byte},System.AsyncCallback,System.Object)`

```csharp
public virtual System.IAsyncResult NativeWeave.Interop.NativeBufferReader<TResult>.BeginInvoke(System.ReadOnlySpan<byte> bytes, System.AsyncCallback callback, object @object)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

## `M:NativeWeave.Interop.NativeBufferReader`1.EndInvoke(System.IAsyncResult)`

```csharp
public virtual TResult NativeWeave.Interop.NativeBufferReader<TResult>.EndInvoke(System.IAsyncResult result)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

## `M:NativeWeave.Interop.NativeBufferReader`1.Invoke(System.ReadOnlySpan{System.Byte})`

```csharp
public virtual TResult NativeWeave.Interop.NativeBufferReader<TResult>.Invoke(System.ReadOnlySpan<byte> bytes)
```

Compiler-generated record/delegate member; inherits the documented type semantics.

## `P:NativeWeave.Interop.NativeBuffer.IsDisposed`

```csharp
public bool NativeWeave.Interop.NativeBuffer.IsDisposed { get; }
```

**summary:** Gets whether new reads are closed; this does not assert physical reclamation.

**remarks:** Thread-safe, O(1), no native call or allocation.

## `P:NativeWeave.Interop.NativeBuffer.Length`

```csharp
public int NativeWeave.Interop.NativeBuffer.Length { get; }
```

**summary:** Gets the immutable byte length, including after logical disposal.

**remarks:** O(1), no native call or allocation. Length is in [0, Int32.MaxValue].

## `T:NativeWeave.Interop.Extensibility.NativeBufferFactory`

```csharp
public static class NativeWeave.Interop.Extensibility.NativeBufferFactory
```

**summary:** A trusted engine-maintainer seam for adopting immutable native results.

**remarks:** Ordinary applications receive buffers from typed domain operations. The caller guarantees creator/allocator pairing, one sole SafeHandle owner, stable initialized bytes, a negotiated per-result bound and nonthrowing, nonblocking free-threaded destruction. Shape validation cannot establish accessibility of an arbitrary address. The native module remains resident under the loader contract.

## `T:NativeWeave.Interop.NativeBuffer`

```csharp
public sealed class NativeWeave.Interop.NativeBuffer : System.IDisposable
```

**summary:** Owns immutable, address-stable native bytes through the creating module's destructor.

**remarks:** Concurrent reads are safe; callers synchronize access to shared destinations. Reads use A03 logical admission and SafeHandle references without per-call lease allocation. Disposal closes new admission but existing reads remain valid. No public pointer, escaping span, native-backed Memory or generic allocator is exposed. Native destruction must be nonblocking and safe on any release thread. This BCL-only surface is AOT compatible; domain engines enforce their result limit.

## `T:NativeWeave.Interop.NativeBufferReader`

```csharp
public delegate void NativeWeave.Interop.NativeBufferReader(System.ReadOnlySpan<byte> bytes)
```

**summary:** Reads immutable native bytes synchronously while their creating owner is leased.

**param bytes:** A read-only view valid only until this delegate returns.

**remarks:** The delegate runs on the calling thread outside admission locks. The span must not escape or cross an await. A cached static delegate avoids closure allocation.

## `T:NativeWeave.Interop.NativeBufferReader`1`

```csharp
public delegate TResult NativeWeave.Interop.NativeBufferReader<TResult>(System.ReadOnlySpan<byte> bytes)
```

**summary:** Projects a synchronous native read into independently owned managed data.

**typeparam TResult:** A non-ref-like result that cannot retain the borrowed span in safe code.

**param bytes:** A read-only view valid only until this delegate returns.

**returns:** A scalar or independently owned result.

**remarks:** The callback executes outside admission locks with one owner lease. Exceptions propagate after that lease is released. No native callback or asynchronous execution is involved.
