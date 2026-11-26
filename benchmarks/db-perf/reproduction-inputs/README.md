# Historical reproduction inputs

These files preserve the exact source and build inputs associated with earlier
DB-Perf-01 observations. `index.json` records their original evidence paths,
SHA-256 values, Git blob identities, and fixed archive paths. They include
superseded and unsuccessful attempts; their presence does not qualify a result.

Current product libraries and benchmark consumers do not compile or import this
directory. The short harness gate verifies its byte identities and runs the
current controller and scheduler checks separately. To reproduce an earlier
observation, use its recorded toolchain, package, arguments, and source identity.
Do not substitute a current source file with the same basename.

Full implementation inputs belong in Korda. The companion Plan records keep
their hashes, fixed Korda commit references, commands, and original measurement
counters, rather than maintaining a second copy of the implementation.

Snapshot status fields describe their capture time. The Plan evidence bindings
record the eventual published Korda commit; they own the current recovery status.
