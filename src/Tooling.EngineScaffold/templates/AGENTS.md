# Engine repository implementation rules

This repository explicitly adopts the immutable NativeWeave C# API and
documentation standard in `public-api-standard.json`. Read its local copy before
changing public API, bindings, ownership, cancellation or documented behavior.

Keep native domain execution in the pinned Engine. Ordinary application changes
reuse precompiled native assets. No consumer target invokes Cargo, downloads a
compiler, creates a global executor or discovers managed code by reflection.

The maintainer source descriptor and `scaffold-manifest.json` bind the exact
initial source, upstream/crate/features/patch/license provenance and template.
Changed source needs updated reviewed bindings, compiled API/XML baselines,
guide/examples and actual independent JIT/Native AOT package conformance. Run
`python3 eng/verify.py` with the reviewed feed and pinned SDK source. Preserve
failure receipts. Do not mark generated skeletons or design examples accepted.

An owner stays live until actual native and managed retirement. Cancellation or a
close timeout does not establish quiescence. Public APIs expose typed owners and
normal C# handlers; ABI pointers and callback cookies remain internal.

Review source fidelity, API usability, allocation/copy cost, compatibility and
resource behavior independently. Platform acceptance is exact and separate. This
template grants no publication, permission change or merge authorization.
