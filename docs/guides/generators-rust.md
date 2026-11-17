# Rust exports and ABI consistency

**Component:** A11, Linux x64 GNU. This is maintainer tooling; ordinary application
builds do not run Cargo or this command. Public Server/Database types retain their
existing owners. A11 introduces no application API, runtime binder or engine.

## Inputs and invocation

The source adapter is the representation authority. Its exact SHA-256 must match
both the A09 semantic sidecar and A09 parser inventory. Build the actual adapter,
extract its final dynamic symbols with `nm -D --defined-only`, and pass the one-symbol
per-line inventory to the command:

```sh
dotnet run --project src/Generators.Rust -- \
  native/interop/exports/binding.json parsed-inventory.json \
  native/interop/exports/src/lib.rs linked-symbols.txt generated
```

The command validates all input before writing output. Missing, extra or duplicate
symbols fail `NWABI002`; unsupported tuple/generic/ABI types fail `NWABI001`; stale
source or parser identity fails `NWGEN001`. A zero-exit raw extractor that omitted
an export does not satisfy inventory equality. Existing output is never evidence
that a failed invocation succeeded; maintainer staging must use the command's exit
status and manifest hashes. A13 owns isolation and staging of build outputs.

Rust keyword fields use raw-identifier spelling in Rust assertions. Names that
cannot be represented as C11 declarations, or collide with generated typedefs,
fail `NWGEN001` before output publication. A09 can retain `export_name` aliases,
but this A11 in-module assertion profile requires each C symbol to equal its Rust
function item's source name. An alias is rejected with an explicit diagnostic;
silently generating an unresolved Rust item is not permitted.

Output uses ordinal model ordering, UTF-8 and LF, without time, local paths or random
identities. Unchanged output files are not rewritten. The manifest records canonical
description, adapter, parser inventory, header and assertion SHA-256 identities,
the target triple, upstream commit/features and full symbol list.

## Compile the boundary

`nativeweave.generated.h` declares fixed-width scalars, opaque owners and the exact
C calling convention. Its C11 static assertions check size, natural alignment and
every field offset. `layout_assertions.rs` checks the same properties against the
actual Rust types and assigns every adapter function to its exact `unsafe extern
"C" fn(...)` type. It must be compiled in the adapter's module scope. A changed
record, parameter, result or calling convention is a compile failure.

The fixture in `native/interop/exports` calls the completed A01 bootstrap writer.
Its scalar Box owner and synchronous notification functions are an explicitly fixed
generation conformance fixture, with actual creator/live/release counters. They do
not implement A07, an asynchronous Host or a domain engine. A10 uses this same native
component to compare generated and handcrafted calls. Actual domain adapters wrap
their existing engine APIs; private engine layout is never added to the sidecar.

The fixture uses ordinary status returns. A failed create writes null; a failed
read preserves the caller's output; the creating module releases each owner once.
The callback is synchronous and retains neither cookie nor function pointer after
return. Native allocation follows the allocator's fatal OOM policy; no
`catch_unwind` claims to repair failed allocation, arbitrary pointers or a poisoned
engine. The function's pointer-validity preconditions remain caller obligations.

## Consumption and cost

The [sample](../../samples/api/generators-rust/) executes precompiled native bytes
with normal `dotnet run` and explicit-RID Native AOT publication. Maintaining the
fixture or engine is a separate operation from adding managed application source.
There is no generator-induced runtime allocation, reflection scan or runtime
compiler. ABI checks are compile-time constants. Native fixture create allocates
one Box, release frees it, and read performs one scalar load. These are conformance
costs, not application performance results.

The [compiled reference](../../src/Generators.Rust/api-reference.md) records the
intentional empty application surface. No independently released A11 package is
introduced: the tool is a source maintainer executable. A16/B24 own pristine final
package composition. The Linux verification receipt records each actual execution,
input and executable hash; Windows MSVC remains unexecuted in this batch.

## Verification

```sh
python3 tests/Generators.Rust/verify.py --output artifacts/A11/verification
```

This compiles and executes Rust native tests, the generated C header probe and
handwritten C# imports in both JIT and Native AOT modes. It compiles the generated
Rust assertions and deliberately rejects layout/signature/source/inventory drift,
unsupported tuples/generics, missing exports and cancellation. No Design stub
execution is recorded as a product result. Exact module identities and the raw
failed negative compiler logs remain in the receipt.
