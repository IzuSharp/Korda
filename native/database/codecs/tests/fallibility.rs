use nativeweave_data_codecs::{ErrorKind, Limits, Parameters, append_postgres, append_sqlite};
use nativeweave_data_contracts::{
    Codec, Profile,
    wire::{Batch, Descriptor, Expectation},
};
use sqlx::encode::IsNull;
use sqlx::error::BoxDynError;
use sqlx::postgres::{PgArgumentBuffer, PgArguments, PgTypeInfo};
use sqlx::sqlite::{SqliteArguments, SqliteArgumentsBuffer, SqliteTypeInfo};
use sqlx::{Arguments, Encode, Postgres, Sqlite, Type};
use std::sync::{
    Arc,
    atomic::{AtomicUsize, Ordering},
};

struct Fails(Arc<AtomicUsize>);
impl Drop for Fails {
    fn drop(&mut self) {
        self.0.fetch_add(1, Ordering::SeqCst);
    }
}
impl Type<Postgres> for Fails {
    fn type_info() -> PgTypeInfo {
        <i32 as Type<Postgres>>::type_info()
    }
}
impl Type<Sqlite> for Fails {
    fn type_info() -> SqliteTypeInfo {
        <i32 as Type<Sqlite>>::type_info()
    }
}
impl Encode<'_, Postgres> for Fails {
    fn encode_by_ref(
        &self,
        buffer: &mut PgArgumentBuffer,
    ) -> std::result::Result<IsNull, BoxDynError> {
        buffer.extend_from_slice(&[1, 2, 3, 4]);
        Err("deliberate test encoder failure".into())
    }
}
impl Encode<'_, Sqlite> for Fails {
    fn encode_by_ref(
        &self,
        buffer: &mut SqliteArgumentsBuffer,
    ) -> std::result::Result<IsNull, BoxDynError> {
        let _ = <i64 as Encode<Sqlite>>::encode_by_ref(&7, buffer)?;
        Err("deliberate test encoder failure".into())
    }
}
#[test]
fn errors_propagate_and_partial_argument_values_are_rolled_back_and_dropped() {
    let drops = Arc::new(AtomicUsize::new(0));
    let mut pg = PgArguments::default();
    append_postgres(&mut pg, 1i32).unwrap();
    assert_eq!(
        append_postgres(&mut pg, Fails(drops.clone()))
            .unwrap_err()
            .kind,
        ErrorKind::Encoding
    );
    assert_eq!(pg.len(), 1);
    assert_eq!(drops.load(Ordering::SeqCst), 1);
    let mut sq = SqliteArguments::default();
    append_sqlite(&mut sq, 1i32).unwrap();
    assert_eq!(
        append_sqlite(&mut sq, Fails(drops.clone()))
            .unwrap_err()
            .kind,
        ErrorKind::Encoding
    );
    assert_eq!(sq.len(), 1);
    assert_eq!(drops.load(Ordering::SeqCst), 2);
}
fn shape(profile: Profile, id: u32, config: Option<&str>, nullable: bool) -> Expectation {
    Expectation {
        kind: 1,
        profile,
        hash: [1; 32],
        entity_id: 0,
        projection_id: 0,
        exact_rows: Some(1),
        descriptors: vec![Descriptor {
            id: 1,
            codec: Codec {
                id,
                configuration: config.map(str::to_owned),
                nullable,
            },
            bound_codec: if config.is_some() { 1024 } else { id },
        }],
    }
}
#[test]
fn capacities_profiles_and_fresh_argument_owners() {
    let expected = shape(Profile::Sqlite, 5, None, false);
    let wire = Batch::encode(&expected, 1, &[Some(b"hello")]).unwrap();
    assert_eq!(
        Parameters::decode(
            &wire,
            expected.clone(),
            Limits {
                max_field_bytes: 4,
                ..Limits::default()
            }
        )
        .err()
        .unwrap()
        .kind,
        ErrorKind::Capacity
    );
    assert!(
        Parameters::decode(
            &wire,
            expected.clone(),
            Limits {
                max_parameters: 0,
                ..Limits::default()
            }
        )
        .is_err()
    );
    let parameters = Parameters::decode(&wire, expected, Limits::default()).unwrap();
    assert_eq!(
        parameters.postgres().unwrap_err().kind,
        ErrorKind::Capability
    );
    assert_eq!(parameters.sqlite().unwrap().len(), 1);
    assert_eq!(parameters.sqlite().unwrap().len(), 1);
}
#[test]
fn null_is_not_empty_and_tampered_identity_rejects() {
    let expected = shape(Profile::PostgreSql, 5, None, true);
    let empty = Batch::encode(&expected, 1, &[Some(b"")]).unwrap();
    let null = Batch::encode(&expected, 1, &[None]).unwrap();
    assert_ne!(empty, null);
    assert_eq!(
        Parameters::decode(&empty, expected.clone(), Limits::default())
            .unwrap()
            .postgres()
            .unwrap()
            .len(),
        1
    );
    assert_eq!(
        Parameters::decode(&null, expected.clone(), Limits::default())
            .unwrap()
            .postgres()
            .unwrap()
            .len(),
        1
    );
    for offset in [4, 6, 12, 24, 60, 76, 96, 100, 104, 112, 114, 116] {
        let mut broken = empty.clone();
        broken[offset] ^= 0x80;
        assert!(Parameters::decode(&broken, expected.clone(), Limits::default()).is_err());
    }
}

#[test]
fn sea_query_values_share_the_checked_binder_and_reject_loss() {
    use sea_query::{Value, Values};
    let expected = shape(Profile::PostgreSql, 3, None, false);
    assert_eq!(
        Parameters::from_sea_query(
            Values(vec![Value::BigUnsigned(Some(i64::MAX as u64))]),
            expected.clone(),
            Limits::default()
        )
        .unwrap()
        .postgres()
        .unwrap()
        .len(),
        1
    );
    assert!(
        Parameters::from_sea_query(
            Values(vec![Value::BigUnsigned(Some(u64::MAX))]),
            expected.clone(),
            Limits::default()
        )
        .is_err()
    );
    assert!(
        Parameters::from_sea_query(
            Values(vec![Value::String(Some("42".into()))]),
            expected,
            Limits::default()
        )
        .is_err()
    );
    let instant = shape(Profile::PostgreSql, 10, None, false);
    let precise =
        chrono::DateTime::<chrono::Utc>::from_timestamp(1_700_000_000, 123456789).unwrap();
    assert!(
        Parameters::from_sea_query(
            Values(vec![Value::ChronoDateTimeUtc(Some(precise))]),
            instant,
            Limits::default()
        )
        .is_err()
    );
    let json = shape(Profile::Sqlite, 16, None, false);
    assert!(
        Parameters::from_sea_query(
            Values(vec![Value::Json(Some(Box::new(
                serde_json::json!({"long":"content"})
            )))]),
            json,
            Limits {
                max_field_bytes: 3,
                ..Limits::default()
            }
        )
        .is_err()
    );
}

#[test]
fn malformed_offsets_lengths_tags_and_null_flags_fail_before_argument_creation() {
    let expected = shape(Profile::PostgreSql, 3, None, false);
    let wire = Batch::encode(&expected, 1, &[Some(&42i64.to_le_bytes())]).unwrap();
    for (offset, bytes) in [
        (16, u64::MAX.to_le_bytes().to_vec()),
        (56, u32::MAX.to_le_bytes().to_vec()),
        (60, u32::MAX.to_le_bytes().to_vec()),
        (88, u64::MAX.to_le_bytes().to_vec()),
        (100, 999u16.to_le_bytes().to_vec()),
        (114, 1u16.to_le_bytes().to_vec()),
        (120, u64::MAX.to_le_bytes().to_vec()),
        (128, u64::MAX.to_le_bytes().to_vec()),
    ] {
        let mut malformed = wire.clone();
        malformed[offset..offset + bytes.len()].copy_from_slice(&bytes);
        assert!(Parameters::decode(&malformed, expected.clone(), Limits::default()).is_err());
    }
}

#[tokio::test]
async fn sqlite_partial_encoder_failure_does_not_leave_a_hidden_argument() {
    use sqlx::{Connection, Row, SqliteConnection};
    let drops = Arc::new(AtomicUsize::new(0));
    let mut args = SqliteArguments::default();
    append_sqlite(&mut args, 11i64).unwrap();
    assert!(append_sqlite(&mut args, Fails(drops.clone())).is_err());
    append_sqlite(&mut args, 22i64).unwrap();
    let mut connection = SqliteConnection::connect("sqlite::memory:").await.unwrap();
    let row = sqlx::query_with("SELECT ?1, ?2", args)
        .fetch_one(&mut connection)
        .await
        .unwrap();
    assert_eq!((row.get::<i64, _>(0), row.get::<i64, _>(1)), (11, 22));
    assert_eq!(drops.load(Ordering::SeqCst), 1);
    connection.close().await.unwrap();
}

#[test]
fn native_leap_instants_reject_before_argument_build() {
    use chrono::{NaiveDate, TimeZone, Utc};
    use sea_query::{Value, Values};
    let naive = NaiveDate::from_ymd_opt(2016, 12, 31)
        .unwrap()
        .and_hms_nano_opt(23, 59, 59, 1_000_000_000)
        .unwrap();
    let leap = Utc.from_utc_datetime(&naive);
    assert!(leap.timestamp_subsec_nanos() >= 1_000_000_000);
    assert!(
        nativeweave_data_codecs::instant_bytes(leap).is_err(),
        "native leap second was normalized"
    );
    let expected = shape(Profile::PostgreSql, 10, None, false);
    assert!(
        Parameters::from_sea_query(
            Values(vec![Value::ChronoDateTimeUtc(Some(leap))]),
            expected.clone(),
            Limits::default()
        )
        .is_err()
    );
    assert!(
        Parameters::from_sea_query(
            Values(vec![Value::ChronoDateTimeWithTimeZone(Some(
                leap.fixed_offset()
            ))]),
            expected,
            Limits::default()
        )
        .is_err()
    );
}
