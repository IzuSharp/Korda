# Database verification

Pull requests run the checks selected by the committed diff and registered module dependencies. Inspect the plan with `python3 eng/ci/impact.py origin/main`. Database tests cover API signatures and XML documentation, parameter binding, transactions, migrations, typed queries, DTO mapping, streaming and resource disposal.

## Run the database checks locally

Use a clean committed checkout, the pinned toolchain in the [native build guide](guides/cpp-native.md), and a disposable PostgreSQL 18 server. Its test account needs permission to create and drop test databases and roles. Set `NW_TEST_POSTGRES` to that server's connection string and `NW_POSTGRES_BIN` to the matching server tools directory.

```sh
git clone https://github.com/IzuSharp/NativeWeave-Design artifacts/frozen-design
git -C artifacts/frozen-design checkout c62cdb8aee833b6ab1a03e29a3f59f81a1b483f9
git clone https://github.com/IzuSharp/Plan artifacts/frozen-plan
git -C artifacts/frozen-plan checkout 8afa0311f45ab8a8157fc45c51e95a0b17dd09f2
python3 eng/build.py verify
```

## Consumer acceptance

Linux x64 runners execute compiled Native AOT consumers with the SQLite provider, the PostgreSQL provider and both providers together. The tests check query results, dependency closure, native library identity and actual loaded paths. Clean-image and minimum-CPU checks are available through manual CI. Scheduled native analysis covers clang-tidy and C/C++ CodeQL.

The [release guide](releasing.md) covers public candidate and signed-package verification. The [DB-CPP-01 record](https://github.com/IzuSharp/Plan/tree/main/ledger/evidence/batches/DB-CPP-01) documents the original Rust-to-C++ migration under its historical package identities.
