# DB-CPP-01 native regression correspondence

The comparison source is merged product commit
`465cd0f90e6af05ec50eb581156240377cf0825b`. Every Rust path below refers to
that immutable commit, including after its replaced implementation is removed.
The retained component C# business assertions and API calls are unchanged. Their C++ bridges invoke
actual Host/provider objects; controlled completion hooks exist only in the
`NW_HOST_TEST_HOOKS` test-support library and are absent from product binaries.

This is the source-to-test correspondence. Final source hashes, complete
verification manifests, NuGet consumption and CI acceptance belong in the
[DB-CPP-01 Linux migration report](https://github.com/IzuSharp/Plan/blob/main/ledger/evidence/batches/DB-CPP-01/migration-report-linux.md).
A physical-provider test alone does not establish Host ownership, admission,
transaction-outcome or buffer-credit claims.

## Retained gates and recorded local executions

All native runs use LLVM Clang 23.1.5 and C++20. The contracts, scalar,
administration and transaction/streaming tests also executed under ASan and
UBSan. This container cannot run LeakSanitizer's process inspection; these local
sanitizer executions set `detect_leaks=0`. They do not claim LeakSanitizer
acceptance. The shared CI sanitizer preset retains leak detection in a capable
runner. Release fixtures use the same static LLVM C++ runtime as the products.

| C++ executable or maintained verifier | Accepted source | Preserved assertions / local execution |
| --- | --- | --- |
| `nw_contracts_tests`, `tests/Data.Abstractions/verify.py` | `interfaces/src/bin/verify-fixtures.rs`; unchanged managed fixture producer | Ten exact cross-language goldens, 17 scalar representations, 43 original malformed inputs, schema constants, registry deduplication and failed-registration atomicity; fresh producer's 12 cases pass |
| `nw_scalar_tests` | `codecs/src/{scalar,sqlite,postgres}.rs` | Exact SQLite storage classes and canonical text, integer narrowing, finite floats, PostgreSQL OIDs and epochs, exact 96-bit numeric, JSONB framing; 50 invalid/lossy refusals pass |
| `nw_codec_roundtrip`, `tests/Data.Codecs/verify.py` | `codecs/examples/roundtrip.rs`, `codecs/tests/fallibility.rs` | Same C# JIT/AOT producers generate 120 vectors each; both real providers return them to the unchanged C# verifier; 4,189 malformed/truncated inputs reject without provider dispatch; composite/optional key and partial-binding failure assertions pass |
| `nw_administration_tests` | `migrations/src/tests.rs`, SQLx 0.9.0 migration semantics | SQLite 48 checks; with a fresh PostgreSQL database, 69 checks including advisory serialization, nontransactional partial-index replay, checksum/history and zero-column catalog |
| `nw_host_review_tests` administrative cases | `migrations/host.rs::execute`, source close and the independent one-slot semaphore | Actual `DbOperation::start` fills four administrative waiters while all three provider workers are occupied; the next start completes with Capacity immediately; queued cancellation/deadline/close require no free worker. A real SQLite collation destructor blocks physical close and proves the permit remains held until close returns; retaining a terminal result handle does not retain that permit. |
| `nw_sqlite_fixture_tests`, `tests/Data.Sqlite/verify.py` | `sqlite/tests/support/scenarios.rs`, unchanged B03 managed consumer | All 14 original native families pass: 140,219 checks; unchanged managed JIT/AOT consumers each pass 44 assertions |
| `nw_postgres_fixture_tests`, `tests/Data.Postgres/verify.py` | `tests/Data.Postgres/native/src/oracles.rs`, unchanged B02 managed consumer | All 11 original real-service families execute in native, JIT and AOT; results retain the original per-case fields instead of inventing an assertion total |
| `nw_transaction_tests`, `tests/Data.Transactions/verify.py` | `transactions/tests/lifecycle.rs`, unchanged B05 consumer | All 13 native lifecycle families, 50 checks; unchanged JIT/AOT consumer each passes 29 assertions |
| `nw_streaming_tests`, `tests/Data.Streaming/verify.py` | `streaming/tests/bounded.rs`, unchanged B06 consumer | All six native ownership/budget families plus complete-row-preflight/aggregate-budget regressions; unchanged dual-provider JIT/AOT consumers each pass 598 assertions across 58 cases |
| `nw_provider_tests`, `provider_verify.py` | Additional focused probes of accepted provider behavior | Independent TLS endpoints, ambient configuration rejection, exact BUSY timeout, real cancel/drain/discard, same-backend reuse and connection replacement; these supplement the original B02/B03 gates |
| `native.abi_status`, `tests/Migration.Cpp/abi_probe.py` | Direct calls to the fixed Rust provider and the C++ candidate, with the same 24 invalid-input cases | Exact status/domain/code equality for binding versus Host errors, projection sequence bounds, malformed plan parameters and owner-before-codec-validation precedence; both runs return all observed native owner counts to zero. `abi-status.expected.json` retains the fixed reference oracle. |

The `golden/contracts` directory records producer provenance and SHA-256 for
every versioned byte fixture. None of these candidate gates invokes Cargo,
`rustc`, SQLx or SeaQuery. Source stability and artifact hashes are checked by
the Python component verifiers; a source change during a run is a failure even
when its individual consumer executions pass.

## SQLite B03: all 14 native assertion families

The maintained bridge is `fixtures/sqlite.cpp`. Each numbered scenario is
invoked by the original C# consumer; `native.sqlite14` also invokes every
scenario directly. These use real SQLite/Host state, UDF/commit-hook gates and
independent SQL where appropriate.

| Original scenario | C++ correspondence and preserved claim |
| --- | --- |
| `profile` | Engine version, foreign keys, WAL, FULL, observable busy timeout, threadsafe build, configuration refusals |
| `memory` | Private memory identity and retained content through sequential leases |
| `codecs` | Actual provider storage/value equality and rejected lossy conversions |
| `savepoints` | Rollback-to retains tracked scope, release ordering and independently persisted outer effect |
| `cancellation` | Actual SQLite execution interruption, separate stop/retirement observation and stale cancellation isolation |
| `early_stream` | Partial stream closes without reading the full result; physical connection remains safe to reuse |
| `pool_retirement` | One-slot admission remains charged until the old physical connection/operation retires |
| `lost_commit` | A real SQLite commit followed by a controlled acknowledgement failure remains `Unknown`; independent SQL confirms durable data |
| `admission` | Finite admission/waiters/input ownership and restored permits after canceled admission |
| `concurrency` | Rejecting single-command admission and independent physical writer-lock behavior |
| `migrations` | Original migration history, checksum, atomicity and idempotency assertions |
| `read_only` | File-level read-only behavior, transaction query-only effect/reset and exact isolation capability |
| `last_parent` | A real SQLite collation destructor blocks physical close; while blocked, transaction/session are not retired and `source.leased` remains 1; only the observed close releases them |
| `row_handoff` | Repeated 10,000-row delivery preserves order and bounded row handoff, then returns provider/Host credits |

## PostgreSQL B02: all 11 real-service assertion families

The maintained bridge is `fixtures/postgres.cpp`. The verifier provisions the
same real TLS categories as the accepted B02 runner. It never treats a missing
service as success.

| Original oracle | C++ correspondence and preserved claim |
| --- | --- |
| `transaction_parent_retirement` | Last parent/accessor retirement precedes physical lease return; retired owners reject new work |
| `tls` | Verified identity, hostname mismatch, untrusted root, expired certificate, explicit plaintext opt-in, URI override refusal and application identity |
| `timeout_rollback` | Real `57014`, then `25P02`, explicit rollback, same-backend reuse; failed COMMIT is known rolled back |
| `stream_and_admission` | Early stream release, complete empty metadata and canceled pool admission returning its permit |
| `codecs` | Real B07 values and wide numeric rejection, supplemented by the 120-vector roundtrip gate |
| `savepoints_and_b05` | Rollback-to retains depth, outer transaction persists independently, predispatch cancellation performs no commit |
| `lost_commit` | `CommitLossProxy` forwards COMMIT, observes server `CommandComplete(COMMIT)` and idle `ReadyForQuery`, drops both reply frames and closes the client; Host reports `Unknown`, independent SQL finds the committed row, cleanup preserves `Unknown` |
| `conflict_snapshot` | Synchronization on the real server lock proves same-statement CTE snapshot behavior and the separate-statement retry distinction |
| `quarantine` | Short cleanup deadline and terminated backend both retire unsafe connections; replacement backend differs and permits return |
| `migration_lock` | An explicit contaminated-lease negative control exposes the old retained advisory lock; actual dedicated administration retirement leaves zero locks; unchanged checksum/history runs separately in `native.administration` |
| `close_with_admitted_operation` | Closing admission keeps its permit and parent alive while the admitted operation runs; release follows actual completion |

The old PostgreSQL stop contract deliberately drains instead of issuing a
cancel-key connection. The C++ implementation preserves that distinction,
including a still-usable surrounding transaction. It waits within the finite
cleanup budget and physically discards an undrained connection. An additional
large-result probe accepts either observed safe reuse or observed discard; it
does not impose a new performance requirement or relax an original oracle.

## B05 controlled lifecycle mapping

`transaction_tests.cpp` links the actual Host coordinator, begins real SQLite
transactions and installs only completion/retirement scheduling hooks. It does
not implement a second transaction state machine. `fixtures/transactions.cpp`
retains the exact old component C ABI for the unchanged managed consumer.

| Original test | C++ test / preserved claim |
| --- | --- |
| `cancellation_before_commit_has_no_provider_effect` | `canceled_before`: no provider COMMIT before admission; join performs actual rollback |
| `request_cancellation_after_dispatch_cannot_cancel_commit` | `cancel_after`: admitted COMMIT owns its terminal observation independently of the request token |
| `deadline_retains_running_future_and_unknown_after_late_confirmation` | `deadline_owner`: deadline retains the driver/accessor/lease; late confirmation cannot overwrite `Unknown` |
| `response_loss_remains_unknown_after_cleanup_rollback` | `response_loss`: provider code/correlation/recovery identity survive actual cleanup rollback |
| `failed_transaction_cannot_commit_and_real_rollback_is_distinct` | `failed_transaction`: failed state rejects commit; actual rollback has its own outcome |
| `provider_rollback_confirmation_is_never_commit_success` | `known_rollback`: confirmed rollback is provider failure and `RolledBack`, never success/unknown; real PostgreSQL case also runs in B02 |
| `unconfirmed_rollback_without_commit_preserves_inprogress` | `unconfirmed_rollback`: failed rollback invents no database outcome and discards the unsafe connection |
| `savepoints_are_lifo_keep_rollback_scope_and_preserve_outer` | `savepoint_lifo`: SQL rollback-to, LIFO admission, released-token disposal and outer preservation |
| `nontop_disposal_closes_admission_without_reordering_sql` | `non_top_disposal`: non-top disposal closes admission and keeps its cleanup failure through automatic rollback |
| `bounded_close_never_reports_queued_retirement_as_released` | `bounded_close`: independent cleanup deadline reports pending while the actual retirement gate remains closed |
| `stale_generation_and_cancellation_cannot_affect_next_owner` | `stale_owner`, provider stale-token probes and original managed consumer: stale owner acts only on its retired transaction; fresh owner remains usable |
| `capability_and_raw_control_reject_before_provider_traffic` | `capability_preflight`: unsupported SQLite isolation and raw transaction-control SQL reject before BEGIN; real SQLite read-only capability is retained in B03 |
| `last_owner_drop_retains_cleanup_until_driver_retirement` | `final_owner`: dropping the public wrapper retains the actual Host cleanup owner until observed retirement |

The separate physical last-parent barrier is also exercised by the real B03
close-destructor gate and B02 parent-retirement oracle. Positive paths wait for
all Host source/session counters to return to zero.

## B06 bounded-stream ownership mapping

`streaming_tests.cpp` uses real SQLite statements and a UDF to hold an admitted
partial fetch. Its retirement gate is compiled only into test support.
`fixtures/streaming.cpp` composes actual Host cursors, buffers, sources and both
providers while retaining the original component C ABI and C# consumer.

| Original test | C++ test / preserved claim |
| --- | --- |
| `empty_one_multibatch_slow_consumer_and_take_once` | `bounded_batches` plus unchanged C# bridge: no prefetch before fetch, bounded batches, no read during a slow consumer, one batch owner, once-only transfer and empty EOS |
| `owned_batch_outlives_cursor_and_transfers_its_budget` | `batch_outlives_cursor`: transferred bytes remain readable; their actual source reservation survives cursor close; new cursor rejects before SQL until the batch is freed |
| `oversized_field_and_shape_errors_stop_before_more_reads` | `field_and_shape`: oversized field stops before another row; complete dynamic shape wins over earlier scalar conversion/size; each column consumes remaining aggregate row budget |
| `abandoned_cursor_and_canceled_close_keep_cleanup_charge_until_receipt` | `abandoned_close`: dropping the public owner and its bounded wait leaves actual cleanup/accessor/reservation owners live until the retirement gate opens |
| `abandoned_partial_fetch_rejects_resume_and_close_still_joins` | `partial_fetch_cancel`: real SQLite UDF holds the second row; concurrent resume rejects, no partial batch transfers, cancel retains its charge until actual statement retirement |
| `unknown_retirement_isolated_and_invalid_limits_never_dispatch` | `unknown_and_invalid`: a controlled unconfirmed close retains a finite quarantine charge; invalid counts/row-table capacity perform no SQL |

Two additional controlled scheduling cases cover the C++ cleanup worker's
ownership boundary. `retirement_worker_owner_barrier` pauses after actual
provider close while the worker still owns the cursor: concurrent close/join
must remain pending, and dropping the last caller owner after join must return
credits immediately. `retirement_notification_barrier` pauses a waiter between
checking completion and waiting, lets the cleanup worker drop its cursor owner,
and then releases the waiter; publication and waiting use the same independent
completion mutex. The original three-second timeout and immediate-zero resource
assertions remain unchanged. A deliberate early-return mutation in `Cursor::join`
is rejected by the worker-owner barrier case.

The deliberately quarantined case runs in a subprocess and exits with its
uncertain owner still retained, matching the original bounded isolation claim.
It is not counted as a positive resource-release or leak-sanitizer case. All
ordinary parent-process paths require zero source/session/cursor/buffer counters.

## B07 fallibility assertion families

`codec_roundtrip.cpp` retains the representable assertions from all seven
functions in the fixed `native/database/codecs/tests/fallibility.rs`. Its
`legacy_fallibility` group uses the unchanged managed vector metadata, performs
24 deterministic refusals in addition to the malformed-input sweep, and sends
valid argument owners through the actual providers.

| Original Rust function | C++ assertion or explicit private-adapter replacement |
| --- | --- |
| `errors_propagate_and_partial_argument_values_are_rolled_back_and_dropped` | Invalid second scalar after the first prepared/bound argument; both providers retain zero writes and admit a fresh successful owner. Rust-specific partial argument-buffer length and drop counters are replaced by RAII ownership and sanitizer checks. |
| `capacities_profiles_and_fresh_argument_owners` | Exact `hello` value rejects `max_field_bytes=4` and `max_parameters=0` with capacity code 6. A mismatched expected provider profile rejects before binding. Two separately decoded argument owners each execute successfully on SQLite. The removed private `Parameters::postgres()` method has no C++ counterpart. |
| `null_is_not_empty_and_tampered_identity_rejects` | Distinct NULL/empty wires each retain one argument slot and roundtrip distinctly through PostgreSQL. All twelve original header/descriptor/cell offsets are mutated explicitly and rejected. |
| `sea_query_values_share_the_checked_binder_and_reject_loss` | The shared canonical binder retains the exact three-byte JSON field-capacity refusal; unchanged managed vectors cover signed 64-bit bounds and reject oversized unsigned enums and sub-microsecond values. Heterogeneous SeaQuery `Value` conversion is removed, as described below. |
| `malformed_offsets_lengths_tags_and_null_flags_fail_before_argument_creation` | All eight original extreme offset/length/tag/null-flag mutations reject explicitly, in addition to 4,189 rejected truncations and seeded mutations. |
| `sqlite_partial_encoder_failure_does_not_leave_a_hidden_argument` | The real partial-bind failure and fresh-owner SQL check run on SQLite and PostgreSQL; no failed argument can leave a hidden value in a later statement. |
| `native_leap_instants_reject_before_argument_build` | The deleted private chrono adapter accepted a leap-second representation with nanoseconds at least one billion and rejected it. Canonical microsecond wire values and C# `DateTime`/`DateTimeOffset` cannot represent that private shape; no C++ chrono conversion entry point is introduced. This is a removed implementation adapter, not a newly accepted leap-second value. |

## Private adapter tests that change with the implementation language

- SQLx's test-only `Fails` encoder previously checked partially appended argument
  buffers and Rust drop counters. C++ has no corresponding public extensible
  argument buffer. `codec_roundtrip.cpp` now supplies an invalid second scalar
  to a real statement after the first argument is prepared/bound, confirms zero
  database effects, then executes a fresh argument owner successfully on both
  providers. RAII owns every partial allocation.
- SeaQuery's private heterogeneous `Value`/`Values` and chrono nanosecond
  adapters no longer exist. The C++ query path uses the same validated canonical
  byte binder as explicit SQL. The unchanged C# consumer still rejects
  sub-microsecond time/instant inputs before native dispatch. The real
  PostgreSQL negative control passes nine fractional digits to the server and
  confirms that the returned binary timestamp cannot preserve them.
- The original B05 fake driver's SQLite `read_only=false` capability is not an
  accepted real-provider limitation: the actual SQLite provider supports its
  validated query-only transaction option. Its unsupported isolation preflight,
  exact read-only behavior and reset remain in B03, without changing the C# API.

## Existing restrictions deliberately preserved

- The accepted migration statement-prefix guard rejects transaction-control
  tokens outside comments/quotes. It also rejects a SQLite trigger body's final
  `; END;`; DB-CPP-01 preserves that accepted restriction.
- PostgreSQL text containing NUL already rejects in the original scalar
  provider-size preflight. The C++ text encoder retains that conversion category
  before libpq text-parameter dispatch.
- SQLite interruption can automatically end its transaction. Administration
  still attempts the tracked rollback and retains its secondary failure in the
  cleanup chain, matching the original cancellation assertion.
- SQLx's SHA-384 migration checksum and both ledger schemas stay unchanged.
  Public bundle and per-script identities continue to use SHA-256.
- The original SQLx migration checksum-mismatch advisory-lock issue is an
  explicitly named negative-control setup in B02; it is not silently reported
  as successful product cleanup.
