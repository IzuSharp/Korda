# Checked data codecs (B07)

## Scope and application usage

`NativeWeave.Data` owns the public `ValueCodec<T>`, `Codecs`, `SqlParameters`,
`SqlCommand`, row and relation declarations. B07 implements their internal
managed parameter snapshot and the single fallible Rust conversion boundary.
It introduces no public CLR type, database host, connection pool, runtime, SQL
parser, implicit serializer or new consumer package. The complete DataSource /
DataSession composition remains B04; this component is not an installable full
database product.

Use explicit codecs when a value's precision, NULL policy or JSON metadata is
significant:

```csharp
using NativeWeave.Data;

var parameters = new SqlParameters()
    .Add("id", Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"), Codecs.Guid)
    .Add("amount", 123.45m, Codecs.Decimal(18, 2))
    .Add("note", (string?)null, Codecs.NullableReference(Codecs.String));
var command = SqlCommand.Create("SELECT @id, @amount, @note", parameters);
```

The [minimal/advanced sample](../../samples/api/data-codecs/Program.cs) also
shows static JSON metadata and SQLite's `DecimalText` profile. It constructs
real typed parameter snapshots and checks a precision error; it does not pretend
to execute an unavailable B04 host. The service fixture below executes the
B07 conversion and SQLx backend against both real databases.

## Exact conversion path

`Codecs / SqlParameters` → B01 owned scalar bytes → internal
`ParameterSnapshot.Create` → canonical db-wire/1 → Rust `Parameters::decode`
→ `Parameters::postgres` / `Parameters::sqlite` → SQLx `Arguments::add`
→ the provider operation. Result conversion uses `read_postgres` /
`read_sqlite`, returns owned canonical scalar bytes, and uses the B01 typed
codec for managed materialization. B06 owns complete result-shape preflight.

SeaQuery callers use `Parameters::from_sea_query`: its native `Value` variant
must agree with the admitted B01 descriptor. Unsigned narrowing, local/ambiguous
timestamps, wrong typed NULL variants and lossy conversion fail explicitly.
There is no automatic text coercion or panic-prone convenience binder.

The direct B07 boundary is an internal Rust library and an internal CLR adapter;
it adds no versioned C export. B02/B03/B04/B11 compose the declared database C
operations. The component fixture transports exact byte vectors between the
managed consumer and the native executable, so this evidence does not assert a
completed async host, C callback/retirement path or pristine NuGet consumption.

## Supported scalar profiles

| Logical IDs | PostgreSQL | SQLite | Checked boundary |
| --- | --- | --- | --- |
| 1, 2, 3, 17 | smallint, integer, bigint, enum-as-integer | INTEGER | Signed width, no REAL-as-integer fallback; unsigned native input must fit |
| 4 | boolean | INTEGER 0 or 1 | Other integer Boolean values reject |
| 5, 6 | text, bytea | TEXT, BLOB | Strict UTF-8 and byte bounds; PostgreSQL text NUL rejects before binding |
| 7 | uuid | 16-byte BLOB | RFC/network byte order, never CLR mixed-endian memory |
| 8 | date | `YYYY-MM-DD` TEXT | Year 1–9999 and canonical ISO spelling |
| 9 | time | `HH:MM:SS.ffffff` TEXT | Microseconds, no leap second, submicrosecond rounding or 24:00 |
| 10 | timestamptz | Unix-microsecond INTEGER | UTC normalization; DateTime must be UTC; leap seconds reject; CLR range checked before native decoding |
| 11, 12 | double precision, real | REAL | Finite values; SQLite-to-Single must be exactly representable |
| 13 | numeric with explicit precision/scale | Unsupported | CLR 96-bit decimal domain; no rounding or scale loss |
| 14 | Unsupported by this ordinary profile | Canonical decimal TEXT | Exact storage only; numeric SQL semantics are not implied |
| 15 | bigint | INTEGER | Exact checked Int64 scaled coefficient |
| 16 | JSON/JSONB | JSON TEXT | Static CLR metadata, strict bounded JSON; JSON null differs from SQL NULL |

`UtcDateTime` and `UtcInstant` share logical ID 10. Configured decimal and money
identities remain distinct source-bound IDs, starting at 1024. Unconfigured
builtins retain IDs 1–17. NULL preserves the declared codec and requires its
nullable flag. Empty text/blob is a non-null zero-length value.

JSONB may normalize JSON object formatting/order; tests compare the actual typed
value. Wire floating signed zero is preserved; provider value semantics and any
SQLite signed-zero normalization are checked separately. Arrays, native enum
names, ranges, composite values, infinity profiles and arbitrary codecs are not
implicitly enabled by upstream dependency features.

SQLx 0.9.0 numeric decoding can round values beyond `rust_decimal` precision.
B07 compares the decoded value's native reencoding with the original numeric
value and rejects any difference. SQLx/chrono also has an overflow path for
PostgreSQL timestamp infinity: B07 rejects wider native date/time/instant ranges
before invoking the native decoder. Neither is handled as an expected panic.

## Bounds, errors and ownership

`Limits` is finite: at most 4,096 parameters and 4 MiB for the input envelope and
provider argument bytes. Per-field limits are independently enforced and cannot
exceed the aggregate limit. Production source options may choose smaller limits.
Native provider-byte accounting includes framing, JSONB's version byte and a
conservative bound for numeric's native representation. Managed validation
finishes before source codec registration. Rust validates the complete admitted
shape, hash, ordered IDs, profile and canonical bytes before constructing arguments.

Every SQLx `Arguments::add` result is propagated. A failed build drops its local
partial argument owner and returns no executable arguments. The tests inject a
failing native encoder after partial output and verify rollback of argument
count and disposal. Each execution builds fresh arguments; submitted values do
not borrow caller input. There is no cancellation or connection management in
this synchronous component; an async owner retains the resulting arguments until
its operation is actually retired.

Managed argument/range errors remain standard exceptions; unsupported profiles
use `DatabaseCapabilityException`. Native `CodecError` classifies Conversion,
Capability, Capacity, Shape and Encoding with bounded constant messages and no
SQL, parameters or connection strings. Missing columns remain shape failures,
even when the requested CLR field is nullable. Complete source/domain mapping of
these categories belongs to the provider/host adapter.

Kind23 is parsed using the canonical B01 relation definition. The component
checks the exact ordered composite parent-key payload and optional child flags;
keys do not acquire nullable parent descriptors. Logical configuration,
collation, model hashes and IDs remain the B01 identity, not a parallel schema.

## Verification and applicability

```sh
python3 tests/Data.Codecs/verify.py --postgres-bin /path/to/postgresql/bin
```

The verifier requires PostgreSQL binaries or an explicit
`NATIVEWEAVE_TEST_POSTGRES_URL`. It fails if the service is unavailable. With
`--postgres-bin` it creates an isolated local test cluster, keeps it alive during
the fixture, and stops it in `finally`; no persistent service configuration is
changed. Only an explicitly supplied `--offline-config` disables audit lookup
for that local restore. Repository and CI audit policy are unchanged.

The verifier builds and executes ordinary C# and Linux x64 Native AOT consumers,
passes their exact independently generated bytes through pinned SQLx against
PostgreSQL and SQLite, and verifies returned values in both managed modes. It
covers all supported scalar IDs, boundary values, typed NULLs, UUID endianness,
huge/tiny numeric failures, precision-loss controls, malformed wire mutations,
kind23/composite parent keys and native encoding failures. It retains raw command
logs, actual toolchain/backend versions, source/lock/executable hashes and failed
run status. The compiled reference checker confirms zero public B07 symbols;
B01's exact public API and XML/reference baseline remain unchanged.

Internal-only status makes new public overloads/XML members and an independent
B07 package inapplicable. The [generated reference](../../src/Data.Codecs/api-reference.md)
records that boundary. A16/B24 retain pristine package and final release
composition obligations. Linux evidence does not claim Windows acceptance.

Cost is O(parameter count + encoded bytes). Managed values are snapshotted once
by B01 and copied into the wire envelope. Native validation owns the envelope
while extracting owned scalar copies; SQLx owns provider arguments. Result
buffers are owned copies. JSON and decimal conversion can allocate; there is no
zero-copy, allocation-free or performance-superiority claim.

Design inputs remain `c62cdb8aee833b6ab1a03e29a3f59f81a1b483f9`, especially
`docs/api/database.md` §§4.2, 9.1.2 and 9.2.1, C-DB-001 and the public API
standard. Actual fixed upstream source inspection is recorded in
`src/Data.Codecs/source-inputs.json`; inspected source is distinct from executed
NativeWeave component evidence.
