# Binding model maintainer sample

Run `python3 tests/Generators.Model/verify.py` at the repository root. The verifier
builds the test fixture with the maintainer's pinned Rust toolchain, then runs this
sample with its exact asset digest. Normal application Entity/DTO builds never run
this maintainer extractor or rebuild native assets.

The sample validates a bounded sidecar, checks handwritten/static descriptor parity,
and uses the existing exact A02 metadata declarations. It does not create a
DataSource, load a database or claim a distributed generator package.
