# Native query implementation reference

B11 adds no public CLR symbol. The exact Cdecl inventory remains generated and
checked under `src/Query.Binding/NativeCalls.g.cs` and
`native/database/binding/signatures-v1.json`. The task does not add a second
query ABI, managed SQL representation, public native handle, or facade assembly.

The maintained runtime is C++20 in `native/cpp/src/query.cpp`, with Host entry
points in `native/cpp/src/host_abi.cpp`. It preserves the fixed SeaQuery 1.0.2 SQL
semantics and the existing ABI validation contracts.

| Existing managed call | C++ query owner |
| --- | --- |
| `QueryBinding.Create` | `NativeQuery` constructor with B04 source/model/limits/capabilities |
| `Column`, `Value`, `Parameter` | `NativeQuery::{column,value,parameter}`; validated B09 token and native `Expr` |
| `Binary`, `Unary`, `List` | `NativeQuery::{binary,unary,list}`; C++ expression nodes and checked native slots |
| `Clause` | `NativeQuery::clause`; candidate query-data mutation followed by B09 atomic validation |
| `CloneSelected`, `SnapshotAndImport` | `clone_selected`, `seal(8)`, `import_plan`; source stays mutable |
| `Import` | `import_model`, `import_plan`, `import_cte`; same source/profile and retained native references |
| `Seal` | `NativeQuery::seal`; immutable `NativePlan` plus B09 lifecycle metadata |
| B04 execution creation | `NativePlan::render` with an optional `ShapeCache`; copied exact values, cached dialect SQL and canonical provider parameters |
| SQL diagnostic | `clone_selected → seal(8) → render_shape`; placeholders preserved; original query stays mutable |
| Source shape cache | `ShapeCache` constructor, `acquire` and `clear`; finite immutable shapes, exact ABI/profile/metadata/query keys, independent execution values |
| Row-lock dispatch | `NativePlan::requires_transaction`; B04 enforces a live explicit transaction |
| CTE creation | `NativeCte::create`; selected native body snapshot and declared model validation |

All buffers above are copied during synchronous admission. Values remain separate
from SQL identifiers and pass through the checked C++ parameter decoder and
libpq/sqlite3 binding in `native/cpp/src/provider.cpp`.
Query mutation is single-writer; B04 owns managed/native admission. Borrowed
parameter views never survive the call. B09 owns argument/state/capability status
definitions; B11 adds finite expanded-expression charges to prevent repeated
native child cloning from growing exponentially behind a small token count.

See [the guide](../../docs/guides/query-native.md) for composition and verification
scope. Public Fluent signatures are documented by B13 when its actual assembly is
compiled; a native unit test is not an assembled-host acceptance receipt.
