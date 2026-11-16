use crate::{ErrorKind, Result, check_profile, error, scalar};
use chrono::{NaiveDate, NaiveTime};
use nativeweave_data_contracts::{Codec, MAX_BYTES, Profile, wire::validate_scalar};
use rust_decimal::Decimal;
use sqlx::sqlite::{SqliteArguments, SqliteRow};
use sqlx::{Arguments, Encode, Row, Sqlite, Type, TypeInfo, ValueRef};

/// Appends one SQLx value and propagates the encoding failure.
pub fn append_sqlite<'a, T>(args: &mut SqliteArguments, value: T) -> Result<()>
where
    T: Encode<'a, Sqlite> + Type<Sqlite>,
{
    args.add(value)
        .map_err(|_| error(ErrorKind::Encoding, "SQLite argument encoding failed"))
}
pub(crate) fn push(args: &mut SqliteArguments, codec: &Codec, bytes: Option<&[u8]>) -> Result<()> {
    check_profile(codec, Profile::Sqlite)?;
    let Some(bytes) = bytes else {
        return append_sqlite(args, None::<i64>);
    };
    match codec.id {
        1 => append_sqlite(args, i16::from_le_bytes(scalar::fixed(bytes)?)),
        2 | 17 => append_sqlite(args, i32::from_le_bytes(scalar::fixed(bytes)?)),
        3 | 10 | 15 => append_sqlite(args, i64::from_le_bytes(scalar::fixed(bytes)?)),
        4 => append_sqlite(args, i32::from(bytes[0])),
        5 | 16 => append_sqlite(args, scalar::text(bytes)?.to_owned()),
        6 | 7 => append_sqlite(args, bytes.to_vec()),
        8 => append_sqlite(args, scalar::date(bytes)?.format("%Y-%m-%d").to_string()),
        9 => append_sqlite(args, scalar::sqlite_time_text(scalar::time(bytes)?)),
        11 => append_sqlite(args, f64::from_le_bytes(scalar::fixed(bytes)?)),
        12 => append_sqlite(args, f64::from(f32::from_le_bytes(scalar::fixed(bytes)?))),
        14 => append_sqlite(args, scalar::decimal(bytes)?.normalize().to_string()),
        _ => Err(error(
            ErrorKind::Capability,
            "unsupported SQLite logical codec",
        )),
    }
}

/// Converts a declared SQLite value without implicit affinity coercion.
/// The caller validates the complete projection before calling this scalar step.
/// NULL is accepted only for a declared nullable codec; an absent column is an error.
pub fn read_sqlite(
    row: &SqliteRow,
    ordinal: usize,
    codec: &Codec,
    max_field_bytes: usize,
) -> Result<Option<Vec<u8>>> {
    check_profile(codec, Profile::Sqlite)?;
    if max_field_bytes == 0 || max_field_bytes > MAX_BYTES {
        return Err(error(ErrorKind::Capacity, "invalid result field limit"));
    }
    if ordinal >= row.len() {
        return Err(error(ErrorKind::Shape, "missing result column"));
    }
    let raw = row.try_get_raw(ordinal).map_err(scalar::conversion)?;
    if raw.is_null() {
        return if codec.nullable {
            Ok(None)
        } else {
            Err(error(ErrorKind::Conversion, "unexpected SQL NULL"))
        };
    }
    let expected_type = match codec.id {
        1 | 2 | 3 | 4 | 10 | 15 | 17 => "INTEGER",
        5 | 8 | 9 | 14 | 16 => "TEXT",
        6 | 7 => "BLOB",
        11 | 12 => "REAL",
        _ => {
            return Err(error(
                ErrorKind::Capability,
                "unsupported SQLite logical codec",
            ));
        }
    };
    if raw.type_info().name() != expected_type {
        return Err(error(
            ErrorKind::Shape,
            "SQLite runtime storage type mismatch",
        ));
    }
    macro_rules! get {
        ($ty:ty) => {
            row.try_get::<$ty, _>(ordinal).map_err(scalar::conversion)?
        };
    }
    let bytes = match codec.id {
        1 => i16::try_from(get!(i64))
            .map_err(scalar::conversion)?
            .to_le_bytes()
            .to_vec(),
        2 | 17 => i32::try_from(get!(i64))
            .map_err(scalar::conversion)?
            .to_le_bytes()
            .to_vec(),
        3 | 10 | 15 => get!(i64).to_le_bytes().to_vec(),
        4 => {
            let value = get!(i64);
            if !matches!(value, 0 | 1) {
                return Err(error(
                    ErrorKind::Conversion,
                    "SQLite Boolean must be zero or one",
                ));
            }
            vec![value as u8]
        }
        5 | 16 => {
            let text = get!(&str);
            if text.len() > max_field_bytes {
                return Err(error(ErrorKind::Capacity, "result field byte limit"));
            }
            text.as_bytes().to_vec()
        }
        6 | 7 => {
            let value = get!(&[u8]);
            if value.len() > max_field_bytes {
                return Err(error(ErrorKind::Capacity, "result field byte limit"));
            }
            value.to_vec()
        }
        8 => {
            let text = get!(&str);
            if text.len() != 10 {
                return Err(error(
                    ErrorKind::Conversion,
                    "SQLite date is not canonical ISO text",
                ));
            }
            let date = NaiveDate::parse_from_str(text, "%Y-%m-%d").map_err(scalar::conversion)?;
            if date.format("%Y-%m-%d").to_string() != text {
                return Err(error(
                    ErrorKind::Conversion,
                    "SQLite date is not canonical ISO text",
                ));
            }
            scalar::date_bytes(date)?
        }
        9 => {
            let text = get!(&str);
            if text.len() != 15 {
                return Err(error(
                    ErrorKind::Conversion,
                    "SQLite time is not canonical microsecond text",
                ));
            }
            let time =
                NaiveTime::parse_from_str(text, "%H:%M:%S%.6f").map_err(scalar::conversion)?;
            if scalar::sqlite_time_text(time) != text {
                return Err(error(
                    ErrorKind::Conversion,
                    "SQLite time is not canonical microsecond text",
                ));
            }
            scalar::time_bytes(time)?
        }
        11 => get!(f64).to_le_bytes().to_vec(),
        12 => {
            let value = get!(f64);
            let narrow = value as f32;
            if !narrow.is_finite() || f64::from(narrow) != value {
                return Err(error(
                    ErrorKind::Conversion,
                    "SQLite Single conversion would lose precision",
                ));
            }
            narrow.to_le_bytes().to_vec()
        }
        14 => {
            let text = get!(&str);
            if text.len() > 31 {
                return Err(error(ErrorKind::Conversion, "SQLite decimal text range"));
            }
            let value = Decimal::from_str_exact(text)
                .map_err(scalar::conversion)?
                .normalize();
            if value.to_string() != text {
                return Err(error(
                    ErrorKind::Conversion,
                    "SQLite decimal is not canonical fixed-point text",
                ));
            }
            scalar::decimal_bytes(value)
        }
        _ => {
            return Err(error(
                ErrorKind::Capability,
                "unsupported SQLite logical codec",
            ));
        }
    };
    if bytes.len() > max_field_bytes {
        return Err(error(ErrorKind::Capacity, "result field byte limit"));
    }
    validate_scalar(codec, &bytes, Profile::Sqlite)?;
    Ok(Some(bytes))
}
