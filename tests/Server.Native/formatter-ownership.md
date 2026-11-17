# SSE formatter allocation and retirement proof

This proof is for the pinned Linux x64 profile in `formatter-sources.json`, not for arbitrary versions of Axum, Bytes or Rust. A source/feature change requires reviewing this proof before retaining the unsafe adoption call.

## Admission precedes allocation

The caller's `ServerEvent` strings already belong to the application. B17 validates scalar metadata and computes UTF-8 lengths without building native payload. It first awaits a B16 queue reservation. A queue wait has no new payload allocation and is canceled by the request/operation/Host deadline path. Once that slot is reserved, request-local and global native credit are acquired atomically without waiting. Native create then borrows the call-only UTF-8 arrays under a separate managed grant, constructs exactly one Axum `Event`, polls one `Sse` body frame and drops the body. No uncredited formatter bytes or clones escape.

Let `d`, `e`, and `i` be the UTF-8 byte counts of data, event and id. Let `n` count CR and LF bytes separately. The design admission policy is `P = 6*d + e + i + 128`. The source-derived visible upper bound is `L = d + 6*(n+1) + e + i + 64`. Both must fit `MaxChunkBytes`. The additional bound matters for newline-dense input: Axum inserts `data: ` after each CR or LF, so `6*d+128` alone is not a universal bound on arbitrary long newline-only strings. This implementation preserves the design policy and rejects before allocation if the mature formatter's actual upper bound exceeds the chunk limit.

The fixed 64 covers final data/event/id delimiters and the largest allowed retry value (24 hours in milliseconds, at most eight digits). Absent fields allocate nothing; an empty data string can produce fewer bytes than the bound. Metadata invalid under Axum's documented panic conditions is rejected before calling its builder. Each builder method is invoked at most once.

## Capacity and conversion overlap

Axum 0.8.9 `Event::default` begins with a unique `BytesMut::new`. The data/event/id/retry methods append to that same active buffer. The formatter never clones or slices it before `finalize`. `Event::finalize` appends one newline and calls `BytesMut::freeze`; `SseBody::poll_frame` transfers those bytes directly to one `Frame::data`.

Bytes 1.12.1 `BytesMut::reserve_inner` uses its unique Vec-backed branch and calls `Vec::reserve`. Rust 1.100.0 `RawVecInner::grow_amortized` selects `max(2*old_capacity, required_capacity, minimum_nonzero_capacity)`. For `u8`, the initial minimum is eight. Define `B = max(8, P, L)`. At each growth point, the old capacity is below the required capacity, which is at most `B`; the new capacity is at most `2*B`. Even if reallocation temporarily retains both old and new allocations, their sum is at most `3*B`. B17 reserves that full peak before constructing the Event. This describes payload allocation capacity; allocator bookkeeping and separately bounded transport/configuration metadata are outside the application-payload ledger.

`freeze` retains the Vec backing rather than copying payload. The Host drops the single-event body before adopting the unique frame bytes. `WriteReservation::prepare_admitted` then places `Bytes` and the already-held credit in `FormattedAllocation`; `Bytes::from_owner` becomes the only escaping payload path. No original `Bytes` alias is retained. The retained credit remains `3*B`, conservatively covering the complete backing capacity and earlier conversion overlap, even if the final visible payload is much smaller. The exact source SHA-256 values, upstream commits, and pinned Rust allocation source URL are recorded beside this proof.

## Ownership transitions and cleanup

Successful native create consumes the one write reservation and publishes a never-started Core operation owning the prepared payload. Caller input is no longer borrowed after create returns. Releasing an unstarted operation drops the payload and queue slot. Starting it transfers the payload into the bounded native body. Native operation completion only acknowledges local enqueue; it is not a peer-consumption acknowledgement.

Every clone or nonempty slice produced after adoption shares the credited owner. Dropping the original frame, completing dispatch, closing the byte budget or timing out shutdown cannot release that credit early. The last payload owner releases it, including a one-byte slice retained after all other owners disappear. `prepared_formatter_credit_survives_last_nonempty_slice` verifies this B16 extension directly. `prepared_write_unstarted_drop_rolls_back_queue_and_payload` and `prepared_write_survives_close_and_preserves_owned_input` bind the adjacent prepare/commit lifecycle. B17's actual JIT/AOT SSE rows verify the formatter and HTTP transport composition.

The unsafe precondition is deliberately narrower than “the visible length fits.” It requires a preceding grant covering the entire backing and overlap, a unique original formatter path, and no escaped uncredited aliases. Reusing the method for a different formatter or storage family requires a new concrete source proof.
