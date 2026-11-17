# NativeWeave synchronous proof-engine API

Generated from the compiled product assembly and XML by `src/Build.Integration/ApiReference`.

A17 bounded real miniz_oxide zlib verification profile, Linux x64 only. This package does not establish a general production codec or all-platform support.

## `F:NativeWeave.Engines.SyncProof.ZlibCodec.MaximumProfileBytes`

```csharp
public const int NativeWeave.Engines.SyncProof.ZlibCodec.MaximumProfileBytes = 16777216
```

**summary:** The maximum value of each independently enforced profile limit, in bytes.

**Inherited type policy:** This verification engine uses independent raw-input, compressed-input and decoded-output limits. It performs no background work, callback, cancellation or executor startup. Instances contain only immutable limits and are safe for concurrent calls on independently owned inputs. The package's current native asset is Linux x64. This is an integration proof, not a general streaming codec API. Native owners use the creating allocator and the shared Core buffer lifetime contract.

## `M:NativeWeave.Engines.SyncProof.ZlibCodec.#ctor(System.Int32,System.Int32,System.Int32)`

```csharp
public NativeWeave.Engines.SyncProof.ZlibCodec.ZlibCodec(int rawInputLimit = 4194304, int compressedInputLimit = 4259840, int decodedOutputLimit = 4194304)
```

**summary:** Creates an immutable codec profile without loading native code or starting threads.

**param rawInputLimit:** Maximum bytes admitted for compression, from zero through MaximumProfileBytes.

**param compressedInputLimit:** Maximum encoded bytes accepted by decompression and published by compression.

**param decodedOutputLimit:** Maximum decoded bytes; must be at least rawInputLimit for same-profile round trips.

**exception:** A limit is negative, exceeds MaximumProfileBytes, or rawInputLimit exceeds decodedOutputLimit.

**remarks:** Defaults are 4 MiB raw/decoded and 4 MiB + 64 KiB encoded. A smaller encoded cap may reject incompressible input; it never publishes a result unusable by this same decoder. A zero decoded cap admits an empty decoded payload only.

**Inherited type policy:** This verification engine uses independent raw-input, compressed-input and decoded-output limits. It performs no background work, callback, cancellation or executor startup. Instances contain only immutable limits and are safe for concurrent calls on independently owned inputs. The package's current native asset is Linux x64. This is an integration proof, not a general streaming codec API. Native owners use the creating allocator and the shared Core buffer lifetime contract.

## `M:NativeWeave.Engines.SyncProof.ZlibCodec.Compress(System.ReadOnlySpan{System.Byte})`

```csharp
public byte[] NativeWeave.Engines.SyncProof.ZlibCodec.Compress(System.ReadOnlySpan<byte> input)
```

**summary:** Compresses a synchronous borrowed span into a caller-owned managed array.

**param input:** Bytes borrowed only until the native compression call returns.

**returns:** Independent zlib bytes admissible to this same profile's decoder.

**exception:** The raw input limit is exceeded before native admission.

**exception:** The encoded limit, native contract or panic boundary fails; no partial result is returned.

**exception:** The precompiled asset cannot pass preflight.

**exception:** A managed allocation or fallible native output reservation fails.

**remarks:** One synchronous native run and one native release; the owned output is copied once into an array. Compression is noninterruptible after entry. The Rust algorithm and fixed owner use some infallible allocations; process OOM/abort is not recoverable.

**Inherited type policy:** This verification engine uses independent raw-input, compressed-input and decoded-output limits. It performs no background work, callback, cancellation or executor startup. Instances contain only immutable limits and are safe for concurrent calls on independently owned inputs. The package's current native asset is Linux x64. This is an integration proof, not a general streaming codec API. Native owners use the creating allocator and the shared Core buffer lifetime contract.

## `M:NativeWeave.Engines.SyncProof.ZlibCodec.CompressOwned(System.ReadOnlySpan{System.Byte})`

```csharp
public NativeWeave.Interop.NativeBuffer NativeWeave.Engines.SyncProof.ZlibCodec.CompressOwned(System.ReadOnlySpan<byte> input)
```

**summary:** Compresses into a creator-owned immutable native buffer with scoped reads.

**param input:** Synchronous borrowed input, no longer retained when this method returns.

**returns:** A buffer the caller must dispose; successful output can be decoded by this profile.

**exception:** The raw input limit is exceeded.

**exception:** Native compression or its output limit fails without publishing an owner.

**exception:** The precompiled asset cannot pass preflight.

**exception:** Fallible reservation or managed wrapping fails; native ownership is released before propagation.

**remarks:** One native run; no managed payload copy. The result retains its actual reserved native capacity until the last Core read lease ends. Dispose adds one creator release. No raw pointer or escaping span is exposed.

**Inherited type policy:** This verification engine uses independent raw-input, compressed-input and decoded-output limits. It performs no background work, callback, cancellation or executor startup. Instances contain only immutable limits and are safe for concurrent calls on independently owned inputs. The package's current native asset is Linux x64. This is an integration proof, not a general streaming codec API. Native owners use the creating allocator and the shared Core buffer lifetime contract.

## `M:NativeWeave.Engines.SyncProof.ZlibCodec.Decompress(System.ReadOnlySpan{System.Byte})`

```csharp
public byte[] NativeWeave.Engines.SyncProof.ZlibCodec.Decompress(System.ReadOnlySpan<byte> input)
```

**summary:** Decompresses exactly one complete zlib stream into a caller-owned managed array.

**param input:** Encoded bytes borrowed only until the native call returns; trailing bytes are rejected.

**returns:** Independent decoded bytes within DecodedOutputLimit.

**exception:** The compressed input limit is exceeded before native admission.

**exception:** Input is malformed, truncated, corrupt, has trailing data, or exceeds the output limit; no partial bytes escape.

**exception:** The precompiled asset cannot pass preflight.

**exception:** A managed allocation or fallible native output reservation fails.

**remarks:** One native run/release pair and one output copy. Output growth uses fallible reservations capped by the declared limit. There is no cancellation point inside the synchronous algorithm.

**Inherited type policy:** This verification engine uses independent raw-input, compressed-input and decoded-output limits. It performs no background work, callback, cancellation or executor startup. Instances contain only immutable limits and are safe for concurrent calls on independently owned inputs. The package's current native asset is Linux x64. This is an integration proof, not a general streaming codec API. Native owners use the creating allocator and the shared Core buffer lifetime contract.

## `M:NativeWeave.Engines.SyncProof.ZlibCodec.DecompressOwned(System.ReadOnlySpan{System.Byte})`

```csharp
public NativeWeave.Interop.NativeBuffer NativeWeave.Engines.SyncProof.ZlibCodec.DecompressOwned(System.ReadOnlySpan<byte> input)
```

**summary:** Decodes one complete zlib stream into a creator-owned immutable native buffer.

**param input:** Call-borrowed encoded input, including its header and checksum; trailing bytes are rejected.

**returns:** A buffer that must be disposed, including when its length is zero.

**exception:** The encoded input limit is exceeded.

**exception:** Malformed input, checksum failure or decoded-output overrun is observed; partial output is discarded.

**exception:** The precompiled asset cannot pass preflight.

**exception:** Fallible reservation or managed wrapping fails; no native owner leaks.

**remarks:** One native run with bounded output growth and no managed payload copy. Scoped Read and CopyTo use A03/A04 leases. Disposal is nonblocking and supports already-admitted reads; there is no asynchronous codec operation.

**Inherited type policy:** This verification engine uses independent raw-input, compressed-input and decoded-output limits. It performs no background work, callback, cancellation or executor startup. Instances contain only immutable limits and are safe for concurrent calls on independently owned inputs. The package's current native asset is Linux x64. This is an integration proof, not a general streaming codec API. Native owners use the creating allocator and the shared Core buffer lifetime contract.

## `P:NativeWeave.Engines.SyncProof.ZlibCodec.CompressedInputLimit`

```csharp
public int NativeWeave.Engines.SyncProof.ZlibCodec.CompressedInputLimit { get; }
```

**summary:** Gets the immutable decoder-input and encoder-result byte limit with no allocation or native call.

**Inherited type policy:** This verification engine uses independent raw-input, compressed-input and decoded-output limits. It performs no background work, callback, cancellation or executor startup. Instances contain only immutable limits and are safe for concurrent calls on independently owned inputs. The package's current native asset is Linux x64. This is an integration proof, not a general streaming codec API. Native owners use the creating allocator and the shared Core buffer lifetime contract.

## `P:NativeWeave.Engines.SyncProof.ZlibCodec.DecodedOutputLimit`

```csharp
public int NativeWeave.Engines.SyncProof.ZlibCodec.DecodedOutputLimit { get; }
```

**summary:** Gets the immutable decoded-output byte limit with no allocation or native call.

**Inherited type policy:** This verification engine uses independent raw-input, compressed-input and decoded-output limits. It performs no background work, callback, cancellation or executor startup. Instances contain only immutable limits and are safe for concurrent calls on independently owned inputs. The package's current native asset is Linux x64. This is an integration proof, not a general streaming codec API. Native owners use the creating allocator and the shared Core buffer lifetime contract.

## `P:NativeWeave.Engines.SyncProof.ZlibCodec.Descriptor`

```csharp
public static NativeWeave.Interop.EngineDescriptor NativeWeave.Engines.SyncProof.ZlibCodec.Descriptor { get; }
```

**summary:** Gets the statically compiled package descriptor without loading the native module.

**remarks:** First descriptor access reads a small assembly resource and allocates immutable metadata; later access reuses it. Inspect performs the shared A02 preflight. The module remains resident after load.

**Inherited type policy:** This verification engine uses independent raw-input, compressed-input and decoded-output limits. It performs no background work, callback, cancellation or executor startup. Instances contain only immutable limits and are safe for concurrent calls on independently owned inputs. The package's current native asset is Linux x64. This is an integration proof, not a general streaming codec API. Native owners use the creating allocator and the shared Core buffer lifetime contract.

## `P:NativeWeave.Engines.SyncProof.ZlibCodec.RawInputLimit`

```csharp
public int NativeWeave.Engines.SyncProof.ZlibCodec.RawInputLimit { get; }
```

**summary:** Gets the immutable compression-input byte limit with no allocation or native call.

**Inherited type policy:** This verification engine uses independent raw-input, compressed-input and decoded-output limits. It performs no background work, callback, cancellation or executor startup. Instances contain only immutable limits and are safe for concurrent calls on independently owned inputs. The package's current native asset is Linux x64. This is an integration proof, not a general streaming codec API. Native owners use the creating allocator and the shared Core buffer lifetime contract.

## `T:NativeWeave.Engines.SyncProof.ZlibCodec`

```csharp
public sealed class NativeWeave.Engines.SyncProof.ZlibCodec
```

**summary:** A bounded synchronous integration proof using the native miniz_oxide zlib algorithm.

**remarks:** This verification engine uses independent raw-input, compressed-input and decoded-output limits. It performs no background work, callback, cancellation or executor startup. Instances contain only immutable limits and are safe for concurrent calls on independently owned inputs. The package's current native asset is Linux x64. This is an integration proof, not a general streaming codec API. Native owners use the creating allocator and the shared Core buffer lifetime contract.
