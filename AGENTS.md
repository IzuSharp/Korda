# NativeWeave implementation guidance

## Current authorized product — DB-CPP-01

NativeWeave is a strongly typed C# Fluent ORM for PostgreSQL and SQLite. The current native implementation is C++20: a runtime query arena, libpq and SQLite, behind the existing C ABI and independent precompiled NuGet provider assets. DB-CPP-01 migrates the previously accepted Rust implementation; DB-API-01 and DB-Perf-01 are independent and must not be started here.

Read README.md, docs/background.md, docs/guides/cpp-native.md and the current Plan batch before editing. Preserve the exact API signatures, namespaces, defaults, nullability, mapping, errors, ownership, bounds and concurrency contracts. Business models and DTOs are runtime metadata; adding an ordinary C# model never requires rebuilding the C++ library.

## Implementation and verification

- Project C/C++ uses LLVM 23.1.5 and C++20/C17. Linux explicitly selects clang/clang++; Windows uses clang-cl with MSVC ABI/runtime and SDK; formal macOS uses the same LLVM with Apple SDK (local Apple Clang must record its actual version/build). Only Linux x64 is accepted in this batch.
- Use shared CMake Release and ASan/UBSan presets, pinned clang-format/tidy and native dependency lock. Never fall back to GCC for project sources or weaken an existing resource/correctness assertion.
- Keep exact frozen C# and C ABI declarations. Generated C and C# files are verified by their generators; the private C++ declaration view changes only the C parameter name `template`.
- Run `python3 eng/build.py verify` from a committed clean tree with disposable PostgreSQL18 and its tools. Historical API declaration checks retain immutable Design c62cdb8 and Plan 8afa0311 inputs. New behavior evidence must bind the candidate's actual source and binaries.
- Ordinary consumers use PackageReference and precompiled native assets. Neither packages nor generators invoke a native compiler. .NET Native AOT retains its normal platform compiler/linker prerequisites.
- Keep credentials and generated binaries out of Git; preserve LICENSE, branch protections, independent technical review and security gates. Candidate production does not authorize public NuGet publication.
- The owner's subsequent publication instruction explicitly authorizes promoting the accepted DB-CPP-01 database package closure to the existing `dekueon` NuGet account. The owner clarified that CI must exclude `NativeWeave.HelloWorld`; its historical NuGet versions remain unchanged and unlisting is not part of this task. This separate delivery uses the existing `release.yml` / `nuget` trusted publisher, the reviewed immutable manifest in `packaging/releases/DB-CPP-01-nuget.json`, and actual registry/signature/consumer receipts. It does not authorize new products, package consolidation or changes to the accepted binaries.
- Record incomplete tests and known baseline defects explicitly. A missing provider, unavailable sanitizer, or failed consumer is not a passed acceptance row.

Applications independently choose standard ASP.NET Core/HTTP/gRPC hosts. NativeWeave server/protocol/MCP/proof products remain retired. English is the repository documentation language. Worktrees belong under the parent project's `.worktree/`. Current handoff: Plan/ledger/evidence/batches/DB-CPP-01/handoff-linux.md. The fixed Rust behavioral source remains in Git history at 465cd0f90e6af05ec50eb581156240377cf0825b.
