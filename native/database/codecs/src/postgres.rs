use crate::{ErrorKind, Result, check_profile, error, scalar};
use chrono::{DateTime, NaiveDate, NaiveTime, Utc};
use nativeweave_data_contracts::{Codec, MAX_BYTES, Profile, wire::validate_scalar};
use rust_decimal::Decimal;
use serde_json::value::RawValue;
use sqlx::postgres::{PgArgumentBuffer, PgArguments, PgRow, PgTypeInfo, PgValueFormat};
use sqlx::types::Json;
use sqlx::{Arguments, Column, Encode, Postgres, Row, Type, ValueRef};
use uuid::Uuid;

/// Appends one SQLx value and propagates the encoding failure without retaining it.
/// Intended for native adapters and a deliberately failing encoder regression probe.
pub fn append_postgres<'a, T>(args: &mut PgArguments, value: T) -> Result<()>
where
    T: Encode<'a, Postgres> + Type<Postgres>,
{
    args.add(value)
        .map_err(|_| error(ErrorKind::Encoding, "PostgreSQL argument encoding failed"))
}

pub(crate) fn push(args: &mut PgArguments, codec: &Codec, bytes: Option<&[u8]>) -> Result<()> {
    check_profile(codec, Profile::PostgreSql)?;
    macro_rules! add {
        ($ty:ty, $convert:expr) => {{
            let value: Option<$ty> = bytes.map($convert).transpose()?;
            append_postgres(args, value)
        }};
    }
    match codec.id {
        1 => add!(i16, |b| Ok::<_, crate::CodecError>(i16::from_le_bytes(
            scalar::fixed(b)?
        ))),
        2 | 17 => add!(i32, |b| Ok::<_, crate::CodecError>(i32::from_le_bytes(
            scalar::fixed(b)?
        ))),
        3 | 15 => add!(i64, |b| Ok::<_, crate::CodecError>(i64::from_le_bytes(
            scalar::fixed(b)?
        ))),
        4 => add!(bool, |b: &[u8]| Ok::<_, crate::CodecError>(b[0] == 1)),
        5 => add!(String, |b| Ok::<_, crate::CodecError>(
            scalar::text(b)?.to_owned()
        )),
        6 => append_postgres(args, bytes.map(<[u8]>::to_vec)),
        7 => add!(Uuid, |b| Ok::<_, crate::CodecError>(Uuid::from_bytes(
            scalar::fixed(b)?
        ))),
        8 => add!(NaiveDate, scalar::date),
        9 => add!(NaiveTime, scalar::time),
        10 => add!(DateTime<Utc>, scalar::instant),
        11 => add!(f64, |b| Ok::<_, crate::CodecError>(f64::from_le_bytes(
            scalar::fixed(b)?
        ))),
        12 => add!(f32, |b| Ok::<_, crate::CodecError>(f32::from_le_bytes(
            scalar::fixed(b)?
        ))),
        13 => add!(Decimal, scalar::decimal),
        16 => {
            let value: Option<Json<Box<RawValue>>> = bytes
                .map(|b| {
                    serde_json::from_slice::<Box<RawValue>>(b)
                        .map(Json)
                        .map_err(scalar::conversion)
                })
                .transpose()?;
            append_postgres(args, value)
        }
        _ => Err(error(
            ErrorKind::Capability,
            "unsupported PostgreSQL logical codec",
        )),
    }
}

fn compatible(codec: &Codec, ty: &PgTypeInfo) -> bool {
    match codec.id {
        1 => <i16 as Type<Postgres>>::compatible(ty),
        2 | 17 => <i32 as Type<Postgres>>::compatible(ty),
        3 | 15 => <i64 as Type<Postgres>>::compatible(ty),
        4 => <bool as Type<Postgres>>::compatible(ty),
        5 => <String as Type<Postgres>>::compatible(ty),
        6 => <Vec<u8> as Type<Postgres>>::compatible(ty),
        7 => <Uuid as Type<Postgres>>::compatible(ty),
        8 => <NaiveDate as Type<Postgres>>::compatible(ty),
        9 => <NaiveTime as Type<Postgres>>::compatible(ty),
        10 => <DateTime<Utc> as Type<Postgres>>::compatible(ty),
        11 => <f64 as Type<Postgres>>::compatible(ty),
        12 => <f32 as Type<Postgres>>::compatible(ty),
        13 => <Decimal as Type<Postgres>>::compatible(ty),
        16 => <Json<Box<RawValue>> as Type<Postgres>>::compatible(ty),
        _ => false,
    }
}

/// Converts one declared PostgreSQL scalar to owned canonical db-wire bytes.
/// Shape/type checking precedes nullable decoding. SQLx owns protocol decoding;
/// numeric is additionally compared with its raw representation to reject rounding.
pub fn read_postgres(
    row: &PgRow,
    ordinal: usize,
    codec: &Codec,
    max_field_bytes: usize,
) -> Result<Option<Vec<u8>>> {
    check_profile(codec, Profile::PostgreSql)?;
    if max_field_bytes == 0 || max_field_bytes > MAX_BYTES {
        return Err(error(ErrorKind::Capacity, "invalid result field limit"));
    }
    let column = row
        .columns()
        .get(ordinal)
        .ok_or_else(|| error(ErrorKind::Shape, "missing result column"))?;
    if !compatible(codec, column.type_info()) {
        return Err(error(ErrorKind::Shape, "PostgreSQL result type mismatch"));
    }
    let raw = row.try_get_raw(ordinal).map_err(scalar::conversion)?;
    if raw.is_null() {
        return if codec.nullable {
            Ok(None)
        } else {
            Err(error(ErrorKind::Conversion, "unexpected SQL NULL"))
        };
    }
    if raw.as_bytes().map_err(scalar::conversion)?.len() > max_field_bytes {
        return Err(error(ErrorKind::Capacity, "result field byte limit"));
    }
    // SQLx/chrono 0.9/0.4 can panic when adding PostgreSQL infinity or a
    // wider native date to its epoch. Reject native ranges before that decoder.
    if raw.format() == PgValueFormat::Binary {
        let native = raw.as_bytes().map_err(scalar::conversion)?;
        match codec.id {
            8 => {
                let days = i32::from_be_bytes(scalar::fixed(native)?);
                if !(-730_119..=2_921_939).contains(&days) {
                    return Err(error(
                        ErrorKind::Conversion,
                        "PostgreSQL date outside CLR domain",
                    ));
                }
            }
            9 => {
                let micros = i64::from_be_bytes(scalar::fixed(native)?);
                if !(0..=86_399_999_999).contains(&micros) {
                    return Err(error(
                        ErrorKind::Conversion,
                        "PostgreSQL time outside CLR domain",
                    ));
                }
            }
            10 => {
                let micros = i64::from_be_bytes(scalar::fixed(native)?);
                if !(-63_082_281_600_000_000..=252_455_615_999_999_999).contains(&micros) {
                    return Err(error(
                        ErrorKind::Conversion,
                        "PostgreSQL instant outside CLR domain",
                    ));
                }
            }
            _ => {}
        }
    }
    macro_rules! get {
        ($ty:ty) => {
            row.try_get::<$ty, _>(ordinal).map_err(scalar::conversion)?
        };
    }
    let bytes = match codec.id {
        1 => get!(i16).to_le_bytes().to_vec(),
        2 | 17 => get!(i32).to_le_bytes().to_vec(),
        3 | 15 => get!(i64).to_le_bytes().to_vec(),
        4 => vec![u8::from(get!(bool))],
        5 => get!(String).into_bytes(),
        6 => get!(Vec<u8>),
        7 => get!(Uuid).into_bytes().to_vec(),
        8 => scalar::date_bytes(get!(NaiveDate))?,
        9 => scalar::time_bytes(get!(NaiveTime))?,
        10 => scalar::instant_bytes(get!(DateTime<Utc>))?,
        11 => get!(f64).to_le_bytes().to_vec(),
        12 => get!(f32).to_le_bytes().to_vec(),
        13 => {
            let value = get!(Decimal);
            match raw.format() {
                PgValueFormat::Binary => {
                    ensure_numeric_exact(raw.as_bytes().map_err(scalar::conversion)?, value)?
                }
                PgValueFormat::Text => {
                    let exact = Decimal::from_str_exact(raw.as_str().map_err(scalar::conversion)?)
                        .map_err(scalar::conversion)?;
                    if exact != value {
                        return Err(error(ErrorKind::Conversion, "numeric precision loss"));
                    }
                }
            }
            scalar::decimal_bytes(value)
        }
        16 => get!(Json<Box<RawValue>>).0.get().as_bytes().to_vec(),
        _ => {
            return Err(error(
                ErrorKind::Capability,
                "unsupported PostgreSQL logical codec",
            ));
        }
    };
    if bytes.len() > max_field_bytes {
        return Err(error(ErrorKind::Capacity, "wire result field byte limit"));
    }
    validate_scalar(codec, &bytes, Profile::PostgreSql)?;
    Ok(Some(bytes))
}

fn canonical_numeric(bytes: &[u8]) -> Result<(u16, i32, Vec<u16>)> {
    if bytes.len() < 8 {
        return Err(error(ErrorKind::Conversion, "truncated PG numeric"));
    }
    let n = u16::from_be_bytes(scalar::fixed(&bytes[..2])?) as usize;
    if bytes.len() != 8 + 2 * n {
        return Err(error(ErrorKind::Conversion, "invalid PG numeric length"));
    }
    let mut weight = i32::from(i16::from_be_bytes(scalar::fixed(&bytes[2..4])?));
    let sign = u16::from_be_bytes(scalar::fixed(&bytes[4..6])?);
    let scale = u16::from_be_bytes(scalar::fixed(&bytes[6..8])?);
    if !matches!(sign, 0 | 0x4000) || scale > 16383 {
        return Err(error(
            ErrorKind::Conversion,
            "nonfinite or invalid PG numeric",
        ));
    }
    let mut digits = Vec::with_capacity(n);
    for chunk in bytes[8..].as_chunks::<2>().0 {
        let digit = u16::from_be_bytes(scalar::fixed(chunk)?);
        if digit > 9999 {
            return Err(error(ErrorKind::Conversion, "invalid PG numeric digit"));
        }
        digits.push(digit);
    }
    while digits.last() == Some(&0) {
        digits.pop();
    }
    let leading = digits.iter().take_while(|x| **x == 0).count();
    weight -= leading as i32;
    digits.drain(..leading);
    if digits.is_empty() {
        Ok((0, 0, digits))
    } else {
        Ok((sign, weight, digits))
    }
}
fn ensure_numeric_exact(bytes: &[u8], value: Decimal) -> Result<()> {
    let mut encoded = PgArgumentBuffer::default();
    let _ = value
        .encode_by_ref(&mut encoded)
        .map_err(scalar::conversion)?;
    if canonical_numeric(bytes)? != canonical_numeric(&encoded)? {
        return Err(error(ErrorKind::Conversion, "numeric precision loss"));
    }
    Ok(())
}
