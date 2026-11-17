# A17 synchronous Engine integration proof

This Linux x64 verification component uses **miniz_oxide 0.9.1**, a real Rust zlib/DEFLATE implementation. It exercises the existing A02 loader, A03 logical admission, A04 immutable buffer and A05 error boundaries through A09/A10/A11 generated static imports. It starts no executor or callback dispatcher. It does not establish a general streaming codec product or other-platform acceptance.

## Install and use

The task's local candidate is `NativeWeave.Engines.SyncProof` version `0.1.0-proof.1`; it is not added to the repository's automatic public preview release scope. Add the candidate's local NuGet feed, then reference that package. The small Core package contains its managed Core and A01 ABI assemblies. Runtime assets are precompiled under `runtimes/linux-x64/native/`; application `dotnet build`, `dotnet run` and explicit-RID Native AOT publication never invoke Cargo.

```csharp
using NativeWeave.Engines.SyncProof;

var codec = new ZlibCodec();
byte[] encoded = codec.Compress("hello from C#"u8);
byte[] decoded = codec.Decompress(encoded);
```

The advanced path extends the same profile and lifecycle:

```csharp
using NativeWeave.Engines.SyncProof;
using NativeWeave.Interop;

var codec = new ZlibCodec(
    rawInputLimit: 1024,
    compressedInputLimit: 2048,
    decodedOutputLimit: 1024);
using NativeBuffer encoded = codec.CompressOwned("hello"u8);
int length = encoded.Read(static bytes => bytes.Length);
byte[] output = new byte[length];
encoded.CopyTo(output);
using NativeBuffer decoded = codec.DecompressOwned(output);
Console.WriteLine(decoded.Read(static bytes => bytes.Length));
```

`ZlibCodec` is immutable and owns no native resource; it is not disposable. Each returned `NativeBuffer` owns a distinct native result and must be disposed. Input spans are borrowed only inside one synchronous native call. Arrays returned by the convenience methods remain independently valid after native release. The native library is resident for the process lifetime under A02; disposing a result does not unload code.

`ZlibCodec.Descriptor` is the optional explicit inspection path. Ordinary methods provide it automatically; applications do not register modules, supply raw handles, select native file paths or initialize a global runtime. Descriptor access allocates its immutable package metadata once. First `Inspect` validates the exact asset hash and ABI; later calls reuse the admitted module. Its only capability is `codec.zlib.v1`, bit zero, with `EngineThreadingModel.Synchronous`.

## Limits, failure and ownership

The defaults are 4,194,304 raw bytes, 4,259,840 compressed bytes and 4,194,304 decoded bytes. Every profile field is independently bounded by 16,777,216 bytes. Raw input cannot exceed the decoded-output cap; invalid constructor combinations fail before loading native code. Zero-sized caps are legal and may make compression fail because even an empty zlib stream needs framing bytes.

Compression uses fallible bounded output growth with `Vec::try_reserve_exact` and invokes the upstream `deflate::core::compress` at level six. If the encoded bytes cannot fit the profile's decoder-input cap, it returns the documented limit failure **before publishing an owner**. Therefore incompressible expansion either round-trips with the same profile or produces a limit error. It never assumes that encoded size is at most raw size. Decompression uses the upstream non-wrapping output-buffer mode with fallible growth capped by the decoded-output limit; it validates the zlib header/checksum and requires the complete input to be consumed. Truncated/corrupt input, trailing bytes, concatenated streams and output overrun fail without exposing partial bytes.

Managed input/option bounds throw `ArgumentOutOfRangeException` before native admission. The native adapter preserves D08 status/domain/code. Domain failures use status 13, error domain `0x53594e43`:

| Native code | Meaning |
| --- | --- |
| 1 | Invalid, truncated, checksum-invalid or non-single-stream input |
| 2 | Native raw input cap exceeded |
| 3 | Native compressed input cap exceeded |
| 4 | Decoded output would exceed its cap |
| 5 | Encoded output cannot fit this profile's decoder input cap |

These failures become `NativeEngineException` with the original status/domain/code. A failed fallible output reservation becomes `OutOfMemoryException`. Unexpected unwind-mode panics use the existing A05 containment/quarantine boundary and close further native business admission. Cleanup of published immutable results remains available. No exception is permitted to unwind across C.

Some upstream fixed Huffman-workspace allocation and the fixed owner `Box` allocation remain infallible: allocator abort, stack exhaustion, invalid pointers and process failure are not recoverable promises. Requested output growth never exceeds its declared cap; reallocation can temporarily retain both old and new storage, bounded by twice that cap before allocator overhead. Actual published Vec capacity is counted separately. These byte limits do not bound whole-process RSS, concurrent callers or allocator retention. The adapter does not advertise cancellation of the noninterruptible synchronous algorithm.

After native success, the managed adapter retains a raw-owner cleanup guard until `SafeHandle` construction succeeds. It then retains that handle's cleanup obligation until `NativeBufferFactory.Adopt` successfully commits the wrapper. An exception at either stage releases the creating native owner once. Successful wrappers have one SafeHandle destruction path; repeated Dispose is idempotent, and A03 keeps admitted reads alive through logical closure. The private per-thread construction hook is test-only internal state, exercised after a real non-null native result and before managed adoption in normal and Native AOT tests.

## Source-to-API mapping and costs

| Managed surface | Generated/native operation | Native implementation and cost |
| --- | --- | --- |
| Constructor / limit properties | None | Immutable values, one managed profile object; no native runtime |
| Descriptor / Inspect | `nw_syncproof_get_abi_v1` | Existing A01 bootstrap + A02 hash/capability checks once per loaded identity |
| CompressOwned | `nw_syncproof_run_v1`, mode 0 | Real miniz_oxide core compression, synchronous input pin, one fallibly grown bounded output buffer, fixed workspace/owner, one SafeHandle and Core wrapper; no managed payload copy |
| DecompressOwned | `nw_syncproof_run_v1`, mode 1 | Real miniz_oxide bounded non-wrapping inflate, bounded fallible output reservations, no managed payload copy |
| Compress / Decompress | Same owned operation plus Core ToArray | Adds one managed payload array and one payload copy; native owner disposed in finally |
| NativeBuffer Read / CopyTo | Existing A03/A04 logical gate and SafeHandle lease | Read imposes no payload copy; CopyTo copies exactly the selected bytes; no additional native call |
| Result Dispose | `nw_syncproof_release_v1` | Exactly one creating-allocator Vec/Box release after the final admitted read |

The root Cargo workspace registration and one Core `InternalsVisibleTo` grant for the generated adapter are the only common integration edits. No new common capability or lifecycle algorithm is introduced. Generated internal owner helpers need that existing A03 implementation; applications cannot access the raw imports, SafeHandle, callback/async glue or native pointer storage.

The finite receipt records native/managed/package/executable sizes, direct-native baseline size, generated versus handwritten adapter lines, declared dependency versions and controlled result-capacity counters. It does not claim throughput superiority, zero allocation, no copying for array methods or a hard RSS bound. The Rust algorithm is reused, and .NET `ZLibStream` is a compatibility oracle only.

## Exact upstream inputs

- miniz_oxide 0.9.1: packaged VCS commit `4e582392df3a739d2b0dfd2c537dc33e8942be38`; crate SHA-256 `b63fbc4a50860e98e7b2aa7804ded1db5cbc3aff9193adaff57a6931bf7c4b4c`. `deflate/mod.rs` high-level loop and `deflate/core.rs` `CompressorOxide::new`, `compress`, `TDEFLStatus` were read to choose fixed-buffer admission and understand fixed workspace allocations. `inflate/mod.rs` bounded helper and `inflate/core.rs` non-wrapping decompressor were read to preserve partial-output and capacity behavior. The original R18 evidence is an input oracle; it is not relabelled as this product run.
- adler2 2.0.1: crate SHA-256 `320119579fcad9c21884f5c4861d16174d0e06250625266f50fe6898340abefa`; reused checksum implementation through miniz_oxide, no direct managed rewrite.
- .NET SDK 11.0.100 / runtime 11.0.0 / C# 15 and Rust 1.100.0 stay pinned. Existing Design interop source studies and exact `design-inputs.json` hashes are inherited. New tests execute actual installed .NET ZLibStream in both directions against the new native engine.

## Maintainer verification

Build the native proof with the pinned toolchain and run `python3 tests/Engines.SyncProof/verify.py --output <new-artifact-directory>`. The runner uses a clean committed source snapshot for formal acceptance; `--prepare` refreshes candidate locks/generated bindings without claiming acceptance. The API reference is generated from the built assembly and XML using the existing ApiReference tool. During isolated parallel work, pass `--packaging-root <A14-A16-worktree>` to reuse the committed shared packaging component; after batch integration the default repository root contains it. It verifies the parsed and linked export inventories, C/Rust layouts, direct Rust parity, malformed/limit cases, raw-owner construction failure, exact compiled public API/XML/reference, ordinary and Native AOT consumers and an independent local NuGet consumer with Rust invocation traps. A16 owns the separate pristine environment and complete release composition.

Linux x64 is the only executed platform in this batch. Windows AOT and other RID assets remain explicitly deferred. Successful finite ownership tests are not long-running load evidence. Independent technical/usability review is recorded separately from implementation self-review.
