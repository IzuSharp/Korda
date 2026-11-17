# Linux package tools

A14 adds no application public types or members. Core XML and compiled API
reference are packaged from the actual implemented assemblies. The maintainer
entry points below do not become an application initialization requirement.

| Entry | Contract |
| --- | --- |
| `linux_profile.py ARTIFACT --profile PROFILE --output RECEIPT` | Read ELF64 x64 architecture, exact exports, allowed dynamic dependencies and maximum glibc; reject undeclared paths/ISA/dependencies. No module load or execution. |
| `packages.pack` | Package immutable, already-built bytes with exact owned `[V]` dependencies. Whole archive hashes are external, never embedded recursively. |
| `tests/Packaging.Linux/verify.py` | Package the actual Core/ABI and completed native component closure, then compile and execute independent RID-free and Linux AOT consumers. |
| `tests/Packaging.Linux/runtime.py` | Execute transferred AOT outputs in an SDK/Rust-free runtime environment and hash the actual selected native bytes. |

Tool objects are local maintainer details. File IO, subprocess work, package
compression and inspection allocate according to bounded artifact sizes. No
default Rust runtime, native download, compiler invocation or reflection loader
is added to Core or to ordinary application execution.
