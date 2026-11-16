# Internal ABI descriptions, layout and bootstrap

Task **A01**, Linux x64 implementation. Design input:
`c62cdb8aee833b6ab1a03e29a3f59f81a1b483f9`, C-ABI-001/C-ERROR-001/C-PLATFORM-001
revision 0.4.0. The native wire version remains **1.0**; document versions and
package versions do not change its representation.

This component defines the shared fixed wire records and a stateless bootstrap
writer. It contains no database/server engine, module loader, owner, scheduler or
default runtime. The compiled `NativeWeave.Interop.Abi` assembly has no public
types. Application metadata and preflight APIs (`AbiVersion`, `EngineDescriptor`,
`EngineCatalog` and named capabilities) are owned by A02/A24. See the
[compiled API reference contribution](../../src/Interop.Abi/api-reference.md).

## One source of wire declarations

[`abi-v1.json`](../../native/interop/abi/abi-v1.json) is the product description.
It records field storage, status numbers, callback prototypes, defined reserved
fields and the bootstrap call contract. The deterministic generator emits the C
header, Rust `repr(C)` records, internal C# `Sequential` records and concrete
layout probes. Do not edit generated declarations independently:

```sh
python3 native/interop/abi/generate.py
python3 native/interop/abi/generate.py --check
```

The description pins the accepted design header's SHA-256. The conformance
oracle is separately checked in and is not regenerated during verification.
Tests compile deliberately reordered and narrowed C fields and require the
complete layout comparison to reject both changes, including a width change
which leaves the containing record's size unchanged.

| Common v1 record | Size / alignment on Linux x64 | Every field offset |
| --- | --- | --- |
| `nw_abi_info_v1` | 32 / 8 | `struct_size=0`, `abi_major=4`, `abi_minor=8`, `pointer_width_bits=12`, `capabilities=16`, `reserved=24` |
| `nw_error_info_v1` | 24 / 8 | `struct_size=0`, `domain=4`, `code=8`, `flags=12`, `correlation_id=16` |
| `nw_bytes_view_v1` | 16 / 8 | `data=0`, `length=8` |
| `nw_operation_callbacks_v1` | 24 / 8 | `struct_size=0`, `flags=4`, `cookie=8`, `on_terminal=16` |
| `nw_callbacks_v1` | 24 / 8 | `struct_size=0`, `flags=4`, `cookie=8`, `on_event=16` |

Every field's width is checked as well as its offset. These five records contain
no implicit padding in the tested x64 profile. Defined `reserved`/`flags` fields
must be zero; generated helpers check this without dereferencing borrowed
pointers. Native and managed probes use actual compiler layouts, not calculated
offsets substituted for compiler output. The managed alignment probe uses the
offset of a concrete record following one byte in a naturally aligned wrapper.
No generic wrapper crosses the ABI.

`nw_status` is always a 32-bit unsigned scalar. Values 0 through 13 retain the
exact D08 numbers; only zero means ordinary success. `PENDING` is permitted only
by a declared try/poll operation. Unknown values never become success or an
implicit retry. Wire Boolean storage is one byte with only 0/1 valid. C `long`,
language enum layouts, Rust collections and CLR references are excluded.

## Engine-prefixed bootstrap

An actual engine supplies its own `nw_<engine>_get_abi_v1` export with the generated
`nw_get_abi_v1_fn` signature. It calls the product Rust `write_abi_v1` helper with
the minor version and capabilities actually compiled into that engine. The
shared crate is an `rlib` and exports no generic global product symbol.

The call has three arguments: requested major, actual output capacity in bytes,
and a caller-owned output pointer. It returns only scalar status. The caller
initializes its output and checks status before reading the complete record.

| Condition | Status | Output |
| --- | --- | --- |
| Null output | `INVALID_ARGUMENT` | No write |
| Requested major other than 1 | `NOT_SUPPORTED` | No write |
| Capacity below the complete 32-byte v1 record | `BUFFER_TOO_SMALL` | No write |
| Misaligned address within valid allocated storage | `INVALID_ARGUMENT` | No write |
| Compatible call | `OK` | Exactly 32 bytes written; `struct_size=32`, `reserved=0`; unknown caller tail untouched |

Checks occur in that table's order. A non-null pointer must address its declared
live writable range and must not overlap another active input/output. Checking
capacity or alignment does not prove an arbitrary address safe. Tests never
dereference deliberately invalid memory. The pointer is borrowed only until
synchronous return; no owner is created, retained or transferred.

The writer is constant work, allocation-free and reentrant, with no callback,
I/O, lock or runtime initialization. It contains no operation that can unwind;
engine exports that add fallible work remain responsible for D08 containment.
Every engine uses its own compiled capability map. The internal managed check
rejects incompatible architecture, major, minimum minor, malformed prefix,
nonzero reserved fields and missing required capability bits before domain
ownership can be published. Unrequired unknown capability bits stay
uninterpreted. A larger provider record is compatible only through its complete
known prefix; callers never read an absent field.

The error record is represented here, but A05 implements its error-output
behavior. Its short diagnostic capacity preserves the original business status;
it must not reuse the bootstrap's `BUFFER_TOO_SMALL` algorithm. Callback record
representation likewise does not implement callback admission/retirement, which
remain A06/A07 responsibilities.

## Maintainer samples and verification

The [minimal/advanced sample](../../samples/api/interop-abi/Program.cs) uses a
friend assembly solely to demonstrate the internal typed import and preflight.
It is not an application-facing raw-handle API. The test-only native engine
exports `nw_abi_conformance_get_abi_v1`, calls the actual product writer, and
advertises two artificial conformance bits. It never claims Database or Server
capabilities and is never packaged as a product engine.

With the repository's .NET 11.0.100 / C# 15.0 / Rust 1.100.0 toolchains and native
linker prerequisites installed, execute the task's checks:

```sh
python3 tests/Interop.Abi/verify.py
```

This builds and runs C/Rust/managed layout probes, C signature calls, native and
managed no-write/capability/reserved/scalar tests, the compiled public-surface
check, minimal/advanced samples, and actual `linux-x64` Native AOT test/sample
executables. AOT execution asserts dynamic code is unavailable. It also checks
the exact native export inventory and retains compiled offset/width mutation
counterexamples. The native build explicitly uses the portable x86-64-v2 CPU
baseline; no host-native instruction set is selected.

Ordinary restores use `packages.lock.json`; the explicit Linux AOT restores use
the task-owned `packages.linux-x64.aot.lock.json` files. Both restore paths run in
locked mode, so publishing the AOT fixtures preserves the ordinary solution's
dependency locks. Verification also rejects a lock-file change during the run.

Each run creates a fresh `artifacts/A01/attempt-NNN/` directory with commands,
stdout/stderr bytes, return codes, toolchain identity, source/lock hashes,
native/managed/AOT artifact hashes and `result.json`. Failed attempts are kept.
`--skip-aot` is only a development shortcut and cannot record AOT acceptance.
`--offline-packages PATH` allows an existing cache of Microsoft .NET 11 SDK tool
archives to be used through an explicitly local feed. That option removes all
remote NuGet sources and skips unavailable vulnerability-data refresh for that
local invocation; repository and normal CI audit policy is unchanged.

Evidence states are per actual run in the Plan A01 handoff. Windows x64 remains
unverified in this batch. These tests establish this ABI component's Linux
behavior, not complete engine/lifecycle acceptance, pristine NuGet consumption,
or a performance advantage. A01 is not independently distributed as an
application package; A13/A16 own composition and pristine package consumption.

## Compatibility and costs

New optional capabilities/minor fields must preserve old field widths, offsets,
meanings, statuses and ownership. Incompatible changes require a new ABI major.
A01's current public managed API baseline is empty. Future public members must
come from their assigned design owners with API/XML/reference review.

The bootstrap performs one FFI call and one 32-byte record write, with zero
managed/native allocation in its implemented successful path. Managed validation
reads that copied record and performs scalar comparisons. Layout/probe JSON,
metadata inspection and sample output allocate as test tooling; they are not
hot-path product behavior. No claim about end-to-end engine allocation,
retirement, SQL/network throughput or package-load cost follows from this work.
