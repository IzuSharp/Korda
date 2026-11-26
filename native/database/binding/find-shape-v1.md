# Private complete-key Find shape extension v1

`find-shape-v1.json` defines capability bit 29 (`db.find.shape.v1`) and one
private export. The frozen database v1 declarations and fingerprint are unchanged.
Like the existing cursor pull and command completion extensions, the current
managed product requires the capability and its export at Source creation.
The independently installed provider package supplies its matching native asset.
This does not require or change the public `db.prepared-shape.v1` capability.

`nw_db_find_plan_create_v1` takes a Source owner, its registered model, the exact
model table ID and its complete registered projection ID. It validates the
ordered nonnullable primary key and complete column declaration order, then
returns an ordinary native Plan owner, its conservative retained-byte cache
charge and its actual fixed logical query bytes (`NativePlan::owned_bytes`).
Failed calls with valid output slots clear all three outputs. The Plan uses the existing owner release
function. Construction performs no provider call and retains no Session, values,
cancellation token or transaction.

The cold compiler builds one equality predicate per ordered key, the complete
model projection and the existing terminal 3 (`SingleOrDefault`, including the
typed `LIMIT 2` argument). Private ordinal slots start at 1 and use names
`find_key_0`, `find_key_1`, and so on. They belong to a private parameter registry;
they never allocate or publish source-wide public query parameter identities.
The normal parameter schema and one-row values frame remain unchanged. Importing
this Plan into an ordinary query is rejected, so private ordinals cannot escape
into a public query or CTE. Ordinary one-shot plans retain their original state
transition; only this private immutable shape is reusable without public Prepare.

The existing query admission rules still apply. Each private slot replaces one
literal expression and preserves its logical node, depth and fixed NWDB frame
cost. That fixed cost is derived from `parameter_frame_size` with one descriptor
and an empty payload. Every execution adds its actual key byte lengths to the
fixed logical query cost synchronously before creating its values frame or Task.
The managed precheck also retains the former per-key Column/Value/equality/WHERE
quota order using scalar counters and the shared wire-size constants; it emits
no command or temporary value frames. Native rendering independently rechecks
the same logical bound before SQL dispatch. Existing binding-quota classification
and individual expression-frame argument errors are unchanged. The terminal's `LIMIT 2` does not
add a paging clause or logical node, but remains a typed SQL argument and counts
toward the parameter bound. Native tests compare the former Fluent selection
encoding's exact node and byte thresholds, including a warm variable-length key
that fits the input frame but exceeds the original logical query budget.

The source-local managed LRU keys by exact Table reference, retaining its exact
mapper and immutable parameter/projection layouts. Equal metadata hashes do not
establish mapper identity. Entry count and retained bytes are bounded by the
existing prepared-shape limits; an individually oversized shape executes without
cache admission. The charge includes retained native query arena, compiled
shape, private parameter metadata and managed wire/projection layout allowances.
The registered model and projection are shared with the source registry. Each
caller retains a separate reference through terminal completion. Cache eviction
or Source close drops the cache reference without closing a caller's accepted
Plan, and the final reference releases the native owner.

Find rendering directly reuses that retained compiled shape. It does not repeat
the general ShapeCache's identity copy, ABI hash, lock or LRU lookup; ordinary and
public prepared execution retain their existing cache path. This bypass changes
no source, capability, parameter or execution admission checks.

The public `EntityKey` still snapshots values when created. Each execution copies
those owned bytes directly into one values frame before awaiting, without
decoding and re-encoding individual keys or rebuilding a Fluent command. It uses
the existing cursor pull, provider schema validation, typed stream and DTO
materializer. Cancellation, mapper failures, duplicate-key cardinality, original
error precedence and provider/session retirement remain on those shared paths.

The existing native query and ABI suites and the actual two-provider Native AOT
`tests/Orm.Relations` suite cover this extension. No benchmark or profiler is part
of its CI checks.
