# Bounded compiler metadata v1

`Bindings.nwmetadata` is UTF-8 with LF and TAB-separated fields, at most 1 MiB.
The first line is `nativeweave-metadata-v1`. It is emitted only after A09 source,
model and final linked inventory agree. Blank final lines are allowed; unknown or
duplicate required fields fail. The source hash identifies exact `Bindings.g.cs`
bytes; the schema digest is the canonical A09 model SHA-256.

| Row | Ordered values after the row name |
| --- | --- |
| module | Qualified stable module ID |
| digest | Canonical schema SHA-256 |
| namespace | Generated C# namespace |
| library | Logical native library name |
| source_sha256 | Generated C# UTF-8 SHA-256 |
| record | Name, size, natural alignment |
| field | Record, field name, wire type, byte offset |
| export | C symbol, C# return type, comma-separated ordered C# parameter types |
| parameter | Export, name, wire type, direction, borrow end, owner kind, mutable 0/1, copy-before-accept 0/1, maximum bytes |
| owner | Kind, release export, parent kind or empty, release executor |
| callback | Symbol, cookie index, notification owner, close export, drain export |
| operation | Name, owner, create, start, request-cancel, take-result, try-retire, release, input-end profile |

Records precede their fields; exports precede their parameter rows. Owners,
callbacks and operations follow the complete export inventory. All named groups
use ordinal ordering; parameters and fields preserve their ABI order. C# names
may contain `@` keyword escaping in the C# type column. No row executes arbitrary
source, invokes Cargo, accesses a database or supplies application runtime state.

A12 checks the facts against the actual compiled/source declarations. The compiler
metadata generator separately emits immutable constants and rejects malformed
metadata structure. It is not a replacement for A09 source validation or A12 parity.
