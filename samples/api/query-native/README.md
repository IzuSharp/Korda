# Native query maintainer example

B11 is internal. Application-facing examples belong to B13's typed Fluent
surface in the `NativeWeave.Database` assembly. The compiled maintainer sample is
`tests/Query.Native/native/src/tests.rs`: `direct_seaquery_sql_and_parameter_order_agree`
constructs an actual typed native query from canonical B01 metadata, applies an
expression/filter/order/limit, seals it and compares the generated SQL and exact
argument order with direct SeaQuery. `sqlite_actual_seaquery_and_sole_binder_cases`
and the matching PostgreSQL test execute the generated command with the existing
B07 binder and inspect actual typed rows.

Run the current component tests with the repository's pinned toolchains:

```sh
cargo test --manifest-path tests/Query.Native/native/Cargo.toml --locked --offline
```

For complete two-provider evidence, set `NATIVEWEAVE_TEST_POSTGRES_URL` to an
isolated disposable test database and the required PostgreSQL test. This is a maintainer
test command, not a consumer installation requirement. Do not label the optional
PostgreSQL test's absent-endpoint path or native-only execution as full B11 host
or Native AOT acceptance.
