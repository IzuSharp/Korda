//! Explicit real-service component fixture. Run with managed-produced vectors.
//! Missing vectors/PostgreSQL are errors, never successful skips.
use nativeweave_data_codecs::{ErrorKind, Limits, Parameters, read_postgres, read_sqlite};
use nativeweave_data_contracts::{
    Codec, Model, Profile,
    wire::{Batch, Descriptor, Expectation, Relation, hex},
};
use serde_json::{Value, json};
use sha2::{Digest, Sha256};
use sqlx::postgres::{PgConnectOptions, PgSslMode};
use sqlx::{Connection, PgConnection, SqliteConnection};
use std::{path::Path, str::FromStr, sync::Arc};
type TestResult<T = ()> = std::result::Result<T, Box<dyn std::error::Error + Send + Sync>>;

fn unhex(value: &str) -> TestResult<Vec<u8>> {
    if !value.len().is_multiple_of(2) {
        return Err("odd hex length".into());
    }
    (0..value.len())
        .step_by(2)
        .map(|i| u8::from_str_radix(&value[i..i + 2], 16).map_err(Into::into))
        .collect()
}
fn codec(value: &Value) -> Codec {
    Codec {
        id: value["logicalId"].as_u64().unwrap() as u32,
        configuration: value["configuration"].as_str().map(str::to_owned),
        nullable: value["nullable"].as_bool().unwrap(),
    }
}
fn expected(value: &Value) -> TestResult<Expectation> {
    let logical = codec(value);
    let schema = format!(
        "{{\"version\":1,\"parameters\":[{{\"id\":1,\"name\":\"p0\",\"codec\":{},\"codec_config\":{},\"nullable\":{}}}]}}",
        logical.id,
        logical.configuration.as_deref().unwrap_or("null"),
        logical.nullable
    );
    let reconstructed: [u8; 32] = Sha256::digest(schema.as_bytes()).into();
    let supplied: [u8; 32] = unhex(value["hash"].as_str().unwrap())?
        .try_into()
        .map_err(|_| "hash width")?;
    if reconstructed != supplied {
        return Err("managed/native canonical parameter schema hash mismatch".into());
    }
    Ok(Expectation {
        kind: 1,
        profile: Profile::try_from(value["profile"].as_u64().unwrap() as u32)
            .map_err(nativeweave_data_codecs::CodecError::from)?,
        hash: reconstructed,
        entity_id: 0,
        projection_id: 0,
        exact_rows: Some(1),
        descriptors: vec![Descriptor {
            id: 1,
            codec: codec(value),
            bound_codec: value["boundId"].as_u64().unwrap() as u32,
        }],
    })
}
fn pg_sql(id: u32) -> &'static str {
    match id {
        1 => "SELECT $1::smallint",
        2 | 17 => "SELECT $1::integer",
        3 | 15 => "SELECT $1::bigint",
        4 => "SELECT $1::boolean",
        5 => "SELECT $1::text",
        6 => "SELECT $1::bytea",
        7 => "SELECT $1::uuid",
        8 => "SELECT $1::date",
        9 => "SELECT $1::time",
        10 => "SELECT $1::timestamptz",
        11 => "SELECT $1::double precision",
        12 => "SELECT $1::real",
        13 => "SELECT $1::numeric",
        16 => "SELECT $1::jsonb",
        _ => panic!("fixture codec"),
    }
}
fn builtin(id: u32, nullable: bool) -> Codec {
    Codec {
        id,
        configuration: None,
        nullable,
    }
}

#[tokio::main(flavor = "current_thread")]
async fn main() -> TestResult {
    let directory = std::env::args()
        .nth(1)
        .ok_or("managed vector directory required")?;
    let directory = Path::new(&directory);
    let url = std::env::var("NATIVEWEAVE_TEST_POSTGRES_URL")
        .map_err(|_| "NATIVEWEAVE_TEST_POSTGRES_URL is required")?;
    let options = PgConnectOptions::from_str(&url)?.ssl_mode(PgSslMode::Disable);
    let mut pg = PgConnection::connect_with(&options).await?;
    let mut sqlite = SqliteConnection::connect("sqlite::memory:").await?;
    let pg_version: String = sqlx::query_scalar("SELECT version()")
        .fetch_one(&mut pg)
        .await?;
    let sqlite_version: String = sqlx::query_scalar("SELECT sqlite_version()")
        .fetch_one(&mut sqlite)
        .await?;
    println!("PostgreSQL: {pg_version}\nSQLite: {sqlite_version}");
    let values: Vec<Value> = serde_json::from_slice(&std::fs::read(directory.join("cases.json"))?)?;
    let mut results = Vec::new();
    let mut provider_sends = 0usize;
    for value in &values {
        let shape = expected(value)?;
        let wire = unhex(value["wire"].as_str().unwrap())?;
        let parameters = Parameters::decode(&wire, shape.clone(), Limits::default())?;
        assert_eq!(parameters.len(), 1);
        let bytes = if shape.profile == Profile::PostgreSql {
            let args = parameters.postgres()?;
            provider_sends += 1;
            let row = sqlx::query_with(pg_sql(codec(value).id), args)
                .fetch_one(&mut pg)
                .await?;
            read_postgres(&row, 0, &codec(value), 4 * 1024 * 1024)?
        } else {
            let args = parameters.sqlite()?;
            provider_sends += 1;
            let row = sqlx::query_with("SELECT ?1", args)
                .fetch_one(&mut sqlite)
                .await?;
            read_sqlite(&row, 0, &codec(value), 4 * 1024 * 1024)?
        };
        results.push(
            json!({"name":value["name"],"hex":bytes.as_deref().map(hex).map(|h|h.to_uppercase())}),
        );
    }
    println!(
        "PASS real C# wire/SQLx scalar roundtrips: {}",
        results.len()
    );
    // Provider field failures occur before conversion is published, even when nullable.
    let numeric = Codec {
        id: 13,
        configuration: Some("{\"precision\":29,\"scale\":28}".into()),
        nullable: false,
    };
    for sql in [
        "SELECT 1e100::numeric",
        "SELECT 1e-30::numeric",
        "SELECT 'NaN'::numeric",
    ] {
        let row = sqlx::query(sql).fetch_one(&mut pg).await?;
        assert!(
            read_postgres(&row, 0, &numeric, 4096).is_err(),
            "wide/tiny/special numeric accepted"
        );
    }
    let row = sqlx::query("SELECT 1.239::numeric")
        .fetch_one(&mut pg)
        .await?;
    assert!(
        read_postgres(
            &row,
            0,
            &Codec {
                id: 13,
                configuration: Some("{\"precision\":4,\"scale\":2}".into()),
                nullable: false
            },
            4096
        )
        .is_err()
    );
    let row = sqlx::query("SELECT NULL::text").fetch_one(&mut pg).await?;
    assert_eq!(
        read_postgres(&row, 0, &builtin(3, true), 4096)
            .unwrap_err()
            .kind,
        ErrorKind::Shape
    );
    assert_eq!(
        read_postgres(&row, 1, &builtin(5, true), 4096)
            .unwrap_err()
            .kind,
        ErrorKind::Shape
    );
    let row = sqlx::query("SELECT 'Infinity'::double precision")
        .fetch_one(&mut pg)
        .await?;
    assert!(read_postgres(&row, 0, &builtin(11, false), 4096).is_err());
    for (sql, codec_id) in [
        ("SELECT 'infinity'::timestamptz", 10),
        ("SELECT '-infinity'::timestamptz", 10),
        ("SELECT 'infinity'::date", 8),
        ("SELECT '-infinity'::date", 8),
        ("SELECT '24:00:00'::time", 9),
    ] {
        let row = sqlx::query(sql).fetch_one(&mut pg).await?;
        assert!(read_postgres(&row, 0, &builtin(codec_id, false), 4096).is_err());
    }
    for (sql, id) in [
        ("SELECT 32768", 1),
        ("SELECT 2147483648", 2),
        ("SELECT 2", 4),
        ("SELECT 1e30", 3),
        ("SELECT 0.1", 12),
        ("SELECT X'0011'", 7),
        ("SELECT '2026-1-2'", 8),
        ("SELECT '12:34:56.123456789'", 9),
        ("SELECT '1.00'", 14),
        ("SELECT 'not json'", 16),
    ] {
        let row = sqlx::query(sql).fetch_one(&mut sqlite).await?;
        assert!(
            read_sqlite(&row, 0, &builtin(id, false), 4096).is_err(),
            "invalid SQLite scalar accepted"
        );
    }
    let row = sqlx::query("SELECT 'too long'")
        .fetch_one(&mut sqlite)
        .await?;
    assert_eq!(
        read_sqlite(&row, 0, &builtin(5, false), 2)
            .unwrap_err()
            .kind,
        ErrorKind::Capacity
    );
    assert_eq!(
        read_sqlite(&row, 1, &builtin(5, true), 2).unwrap_err().kind,
        ErrorKind::Shape
    );
    // The fixed Design loss control is rerun through this actual product converter.
    let precise =
        chrono::DateTime::<chrono::Utc>::from_timestamp(1_700_000_000, 123456789).unwrap();
    assert!(nativeweave_data_codecs::instant_bytes(precise).is_err());
    assert!(
        nativeweave_data_codecs::time_bytes(
            chrono::NaiveTime::from_hms_nano_opt(1, 2, 3, 123456789).unwrap()
        )
        .is_err()
    );
    let raw: chrono::DateTime<chrono::Utc> = sqlx::query_scalar("SELECT $1::timestamptz")
        .bind(precise)
        .fetch_one(&mut pg)
        .await?;
    assert_ne!(
        raw, precise,
        "upstream loss control did not exercise truncation"
    );
    println!("PASS actual provider conversion negatives and upstream precision-loss control");

    let sends_before_validation = provider_sends;
    let valid = &values[0];
    let original = unhex(valid["wire"].as_str().unwrap())?;
    let shape = expected(valid)?;
    let mut rejected = 0usize;
    for length in 0..original.len() {
        if Parameters::decode(&original[..length], shape.clone(), Limits::default()).is_err() {
            rejected += 1;
        }
    }
    let mut rng = 0x1234_5678_9abc_def0u64;
    for _ in 0..4096 {
        rng ^= rng << 13;
        rng ^= rng >> 7;
        rng ^= rng << 17;
        let mut bytes = original.clone();
        let index = rng as usize % bytes.len();
        bytes[index] ^= (rng >> 32) as u8 | 1;
        if Parameters::decode(&bytes, shape.clone(), Limits::default()).is_err() {
            rejected += 1;
        }
    }
    assert!(rejected > 3000);
    assert_eq!(
        provider_sends, sends_before_validation,
        "validation dispatched a provider query"
    );
    for profile in [Profile::Sqlite, Profile::PostgreSql] {
        let parent = Arc::new(
            Model::parse(&std::fs::read(directory.join("parent.json"))?, profile)
                .map_err(nativeweave_data_codecs::CodecError::from)?,
        );
        let child = Arc::new(
            Model::parse(&std::fs::read(directory.join("child.json"))?, profile)
                .map_err(nativeweave_data_codecs::CodecError::from)?,
        );
        let record = std::fs::read(directory.join("relation.bin"))?;
        let relation = Relation::parse(&record, parent.clone(), child.clone())
            .map_err(nativeweave_data_codecs::CodecError::from)?;
        let expected_key = relation
            .parent_key_expectation(profile, &[2, 7])
            .map_err(nativeweave_data_codecs::CodecError::from)?;
        let key = std::fs::read(directory.join(format!("key-{}.bin", profile as u32)))?;
        let params = Parameters::decode(&key, expected_key.clone(), Limits::default())?;
        assert_eq!(params.len(), 2);
        if profile == Profile::Sqlite {
            let _ = params.sqlite()?;
        } else {
            let _ = params.postgres()?;
        }
        let mut reordered = expected_key.clone();
        reordered.descriptors.swap(0, 1);
        assert!(Parameters::decode(&key, reordered, Limits::default()).is_err());
        let mut optional = expected_key.clone();
        optional.descriptors[0].codec.nullable = true;
        assert!(Parameters::decode(&key, optional, Limits::default()).is_err());
        let mut bad_record = record.clone();
        *bad_record.last_mut().unwrap() ^= 1;
        assert!(Relation::parse(&bad_record, parent, child).is_err());
        let parsed =
            Batch::parse(&key, expected_key).map_err(nativeweave_data_codecs::CodecError::from)?;
        assert_eq!(
            parsed
                .cell(0, 1)
                .map_err(nativeweave_data_codecs::CodecError::from)?
                .unwrap(),
            &[
                0, 17, 34, 51, 68, 85, 102, 119, 136, 153, 170, 187, 204, 221, 238, 255
            ]
        );
    }
    println!(
        "PASS malformed wire fuzz/lengths: {rejected}; kind23 composite/optional key binding; no validation provider sends"
    );
    std::fs::write(
        directory.join("results.json"),
        serde_json::to_vec_pretty(&results)?,
    )?;
    std::fs::write(
        directory.join("native-summary.json"),
        serde_json::to_vec_pretty(
            &json!({"roundtrips":results.len(),"malformedRejected":rejected,"providerSendsForVectors":provider_sends,"postgres":pg_version,"sqlite":sqlite_version,"mode":"actual pinned SQLx component; not complete host"}),
        )?,
    )?;
    pg.close().await?;
    sqlite.close().await?;
    Ok(())
}
