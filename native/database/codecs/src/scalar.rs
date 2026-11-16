use crate::{CodecError, ErrorKind, Result, error};
use chrono::{DateTime, Datelike, NaiveDate, NaiveTime, Timelike, Utc};
use nativeweave_data_contracts::{Codec, Profile, wire::validate_scalar};
use rust_decimal::Decimal;

pub(crate) fn fixed<const N: usize>(bytes: &[u8]) -> Result<[u8; N]> {
    bytes
        .try_into()
        .map_err(|_| error(ErrorKind::Conversion, "scalar width mismatch"))
}
pub(crate) fn decimal(bytes: &[u8]) -> Result<Decimal> {
    if bytes.len() != 16 {
        return Err(error(ErrorKind::Conversion, "decimal width"));
    }
    Ok(Decimal::from_parts(
        u32::from_le_bytes(fixed(&bytes[..4])?),
        u32::from_le_bytes(fixed(&bytes[4..8])?),
        u32::from_le_bytes(fixed(&bytes[8..12])?),
        bytes[13] == 1,
        u32::from(bytes[12]),
    ))
}
pub(crate) fn decimal_bytes(value: Decimal) -> Vec<u8> {
    let value = value.normalize();
    let mut result = vec![0; 16];
    result[..12].copy_from_slice(&value.mantissa().unsigned_abs().to_le_bytes()[..12]);
    result[12] = value.scale() as u8;
    result[13] = u8::from(value.is_sign_negative() && !value.is_zero());
    result
}
pub(crate) fn date(bytes: &[u8]) -> Result<NaiveDate> {
    let days = i32::from_le_bytes(fixed(bytes)?);
    NaiveDate::from_num_days_from_ce_opt(
        days.checked_add(719_163)
            .ok_or_else(|| error(ErrorKind::Conversion, "date overflow"))?,
    )
    .ok_or_else(|| error(ErrorKind::Conversion, "date range"))
}
pub(crate) fn date_bytes(value: NaiveDate) -> Result<Vec<u8>> {
    if !(1..=9999).contains(&value.year()) {
        return Err(error(ErrorKind::Conversion, "date outside CLR domain"));
    }
    Ok((value.num_days_from_ce() - 719_163).to_le_bytes().to_vec())
}
pub(crate) fn time(bytes: &[u8]) -> Result<NaiveTime> {
    let micros = u64::from_le_bytes(fixed(bytes)?);
    let secs = u32::try_from(micros / 1_000_000)
        .map_err(|_| error(ErrorKind::Conversion, "time range"))?;
    NaiveTime::from_num_seconds_from_midnight_opt(secs, ((micros % 1_000_000) * 1000) as u32)
        .ok_or_else(|| error(ErrorKind::Conversion, "time range"))
}
/// Converts native time with explicit rejection of nanosecond loss and leap seconds.
pub fn time_bytes(value: NaiveTime) -> Result<Vec<u8>> {
    if !value.nanosecond().is_multiple_of(1000) || value.nanosecond() >= 1_000_000_000 {
        return Err(error(
            ErrorKind::Conversion,
            "time precision loss or leap second",
        ));
    }
    Ok((u64::from(value.num_seconds_from_midnight()) * 1_000_000
        + u64::from(value.nanosecond()) / 1000)
        .to_le_bytes()
        .to_vec())
}
pub(crate) fn instant(bytes: &[u8]) -> Result<DateTime<Utc>> {
    DateTime::from_timestamp_micros(i64::from_le_bytes(fixed(bytes)?))
        .ok_or_else(|| error(ErrorKind::Conversion, "instant range"))
}
/// Converts a native UTC instant, rejecting submicrosecond loss before binding.
pub fn instant_bytes(value: DateTime<Utc>) -> Result<Vec<u8>> {
    if !value.timestamp_subsec_nanos().is_multiple_of(1000)
        || value.timestamp_subsec_nanos() >= 1_000_000_000
    {
        return Err(error(ErrorKind::Conversion, "instant precision loss"));
    }
    let micros = value.timestamp_micros();
    if !(-62_135_596_800_000_000..=253_402_300_799_999_999).contains(&micros) {
        return Err(error(ErrorKind::Conversion, "instant outside CLR domain"));
    }
    Ok(micros.to_le_bytes().to_vec())
}
pub(crate) fn sqlite_time_text(value: NaiveTime) -> String {
    value.format("%H:%M:%S%.6f").to_string()
}
pub(crate) fn text(bytes: &[u8]) -> Result<&str> {
    std::str::from_utf8(bytes).map_err(|_| error(ErrorKind::Conversion, "invalid UTF-8"))
}

// Bound exact storage widths for builtins and conservative maxima for PG numeric.
// This does not count runtime/container overhead; it bounds encoded value bytes.
pub(crate) fn provider_size(
    codec: &Codec,
    bytes: Option<&[u8]>,
    profile: Profile,
) -> Result<usize> {
    let Some(bytes) = bytes else {
        return Ok(0);
    };
    validate_scalar(codec, bytes, profile)?;
    if profile == Profile::PostgreSql && codec.id == 5 && bytes.contains(&0) {
        return Err(error(
            ErrorKind::Conversion,
            "PostgreSQL text cannot contain NUL",
        ));
    }
    Ok(match (profile, codec.id) {
        (Profile::Sqlite, 1 | 2 | 3 | 4 | 10 | 15 | 17) => 8,
        (Profile::Sqlite, 11 | 12) => 8,
        (Profile::Sqlite, 8) => 10,
        (Profile::Sqlite, 9) => 15,
        (Profile::Sqlite, 14) => decimal(bytes)?.to_string().len(),
        (Profile::PostgreSql, 13) => 24,
        (Profile::PostgreSql, 16) => bytes
            .len()
            .checked_add(1)
            .ok_or_else(|| error(ErrorKind::Capacity, "JSON encoding overflow"))?,
        _ => bytes.len(),
    })
}
pub(crate) fn conversion(_: impl std::fmt::Display) -> CodecError {
    error(ErrorKind::Conversion, "provider scalar conversion failed")
}
