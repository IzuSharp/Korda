# Native query implementation reference

B11 adds no public CLR symbol. The exact Cdecl inventory remains generated and
checked under `src/Query.Binding/NativeCalls.g.cs` and
`native/database/binding/signatures-v1.json`. The task does not add a second
query ABI, managed SQL representation, public native handle, or facade assembly.

| Existing managed call | Native SeaQuery owner |
| --- | --- |
| `QueryBinding.Create` | `NativeQuery::new` with B04 source/model/limits/capabilities |
| `Column`, `Value`, `Parameter` | `NativeQuery::{column,value,parameter}`; validated B09 token and native `Expr` |
| `Binary`, `Unary`, `List` | `NativeQuery::{binary,unary,list}`; SeaQuery operators and checked native slots |
| `Clause` | `NativeQuery::clause`; candidate SeaQuery mutation followed by B09 atomic validation |
| `CloneSelected`, `SnapshotAndImport` | `clone_selected`, `seal(8)`, `import_plan`; source stays mutable |
| `Import` | `import_model`, `import_plan`, `import_cte`; same source/profile and retained native references |
| `Seal` | `NativeQuery::seal`; immutable `NativePlan` plus B09 lifecycle metadata |
| B04 execution creation | `NativePlan::render`; copied exact values, SeaQuery SQL and canonical provider parameters |
| CTE creation | `NativeCte::create`; selected native body snapshot and declared model validation |

All buffers above are copied during synchronous admission. Values remain separate
from SQL identifiers and are sent through B07's existing fallible SQLx binder.
Query mutation is single-writer; B04 owns managed/native admission. Borrowed
parameter views never survive the call. B09 owns argument/state/capability status
definitions; B11 adds finite expanded-expression charges to prevent repeated
native child cloning from growing exponentially behind a small token count.

See [the guide](../../docs/guides/query-native.md) for composition and verification
scope. Public Fluent signatures are documented by B13 when its actual assembly is
compiled; a native unit test is not an assembled-host acceptance receipt.
