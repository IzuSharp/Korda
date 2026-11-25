# Owned result batch extension v1

`result-batch-v1.json` specifies the additive capability bit 27
(`db.result.batch.v1`), two Cdecl exports and the 32-byte Linux x64 output record.
The frozen database v1 header, signature inventory, bootstrap fingerprint and
NWDB reserved fields retain their meanings and bytes. Current provider packages
require this capability and both exports during source negotiation. The managed stream
entry point does not fall back to taking and viewing a buffer in separate calls.

`nw_db_operation_take_batch_v1` consumes the same buffer result as
`nw_db_operation_take_buffer_v1`. Either entry point may transfer it exactly
once. A wrong operation result kind rejects without consuming its result. The
whole output record is zero on failure. On success, `owner` transfers one native
buffer reference, and `bytes` borrows that owner's immutable allocation until the
owner is released. The existing `nw_db_buffer_release_v1` releases it. Snapshot
and error consumers may continue using the old take/view functions.

| Offset | Field | Meaning |
| --- | --- | --- |
| 0 | `nw_db_buffer* owner` | Non-null owned reference on success |
| 8 | `nw_bytes_view_v1 bytes` | The same allocation exposed by buffer_view |
| 24 | `uint32_t known_end` | 0 or 1; provider EOF has already been observed |
| 28 | `uint32_t reserved` | Zero |

`known_end` is an observation of the fetch already performed. It never causes an
extra provider step, prefetch, SQL execution, wait or close operation. A last
nonempty batch may set it after natural EOF. Filling the exact requested row
count without observing EOF leaves it zero; the next fetch remains necessary.
Other buffer-result operations leave it zero. Cursor retirement and safe
connection return still require their normal close/join protocol; the flag does
not assert that an active reader or retained result buffer can be released.

Managed consumers validate the record and byte range while retaining the owner.
A malformed successful result releases its transferred owner exactly once.
Every synchronous materializer retains the same SafeHandle scope as before, and
no borrowed span crosses an await. After the last known batch has been consumed,
the internal transport checks managed source admission, then its linked token,
then calls `nw_db_cursor_check_read_admission_v1` at the boundary where the old
path would have fetched again. That synchronous export checks the native source
(including independent native poisoning) and the same cursor active gate,
closing/retired, previous live batch and already transferred empty-end state as
`Cursor::fetch`. It creates no operation or result buffer, reads no row, and sends
no SQL or protocol message. The one synchronous FFI preserves lifecycle errors
while removing the old empty fetch's queue, async owner and result transfer.

Session close propagates to the admitted native cursor and retains the original
`NW_INVALID_STATE` / code 8 mapping. Source close and cancellation retain their
earlier managed checks. Transaction disposal waits for the cursor's session gate
and does not prohibit an already admitted reader from reaching EOF; this check
does not re-admit a command or reject a disposed query/plan retained by a cursor.
Concurrent enumerator disposal and mapper failures still join cleanup through
the existing stream owner, with the original failure taking precedence.
