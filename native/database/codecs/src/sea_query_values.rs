//! Exact conversion from SeaQuery's native value enum into the sole binder.
use crate::{ErrorKind, Limits, Parameters, Result, error, scalar};
use nativeweave_data_contracts::{
    Codec,
    wire::{Batch, Expectation},
};
use sea_query::{Value, Values};

impl Parameters {
    /// Converts SeaQuery values through the same checked scalar/wire/SQLx path.
    /// The admitted descriptor chooses semantics; variant mismatch and unsigned
    /// overflow reject. No convenience binder, panic fallback or string coercion.
    pub fn from_sea_query(values: Values, expected: Expectation, limits: Limits) -> Result<Self> {
        limits.validate()?;
        if values.0.len() != expected.descriptors.len() || values.0.len() > limits.max_parameters {
            return Err(error(ErrorKind::Shape, "SeaQuery parameter count mismatch"));
        }
        let mut total = 96usize
            .checked_add(
                values
                    .0
                    .len()
                    .checked_mul(40)
                    .ok_or_else(|| error(ErrorKind::Capacity, "parameter count overflow"))?,
            )
            .ok_or_else(|| error(ErrorKind::Capacity, "parameter size overflow"))?;
        let mut cells = Vec::with_capacity(values.0.len());
        for (value, descriptor) in values.0.into_iter().zip(&expected.descriptors) {
            crate::check_profile(&descriptor.codec, expected.profile)?;
            let cell = convert(value, &descriptor.codec, limits.max_field_bytes)?;
            total = total
                .checked_add(cell.as_ref().map_or(0, Vec::len))
                .ok_or_else(|| error(ErrorKind::Capacity, "parameter size overflow"))?;
            if total > limits.max_parameter_bytes {
                return Err(error(ErrorKind::Capacity, "parameter byte limit"));
            }
            cells.push(cell);
        }
        let views = cells.iter().map(|v| v.as_deref()).collect::<Vec<_>>();
        let bytes = Batch::encode(&expected, 1, &views)?;
        Self::decode(&bytes, expected, limits)
    }
}

fn convert(value: Value, codec: &Codec, limit: usize) -> Result<Option<Vec<u8>>> {
    macro_rules! bytes {
        ($v:expr) => {
            $v.map(|x| x.to_le_bytes().to_vec())
        };
    }
    macro_rules! checked {
        ($v:expr, $ty:ty) => {
            $v.map(|x| {
                <$ty>::try_from(x)
                    .map(|x| x.to_le_bytes().to_vec())
                    .map_err(scalar::conversion)
            })
            .transpose()?
        };
    }
    let result = match (codec.id, value) {
        (1, Value::SmallInt(v)) => bytes!(v),
        (1, Value::TinyInt(v)) => v.map(|x| i16::from(x).to_le_bytes().to_vec()),
        (1, Value::SmallUnsigned(v)) => checked!(v, i16),
        (1, Value::TinyUnsigned(v)) => v.map(|x| i16::from(x).to_le_bytes().to_vec()),
        (2 | 17, Value::Int(v)) => bytes!(v),
        (2 | 17, Value::Unsigned(v)) => checked!(v, i32),
        (3 | 15, Value::BigInt(v)) => bytes!(v),
        (3 | 15, Value::BigUnsigned(v)) => checked!(v, i64),
        (4, Value::Bool(v)) => v.map(|x| vec![u8::from(x)]),
        (5, Value::String(v)) => v.map(String::into_bytes),
        (6, Value::Bytes(v)) => v,
        (7, Value::Uuid(v)) => v.map(|x| x.into_bytes().to_vec()),
        (8, Value::ChronoDate(v)) => v.map(scalar::date_bytes).transpose()?,
        (9, Value::ChronoTime(v)) => v.map(scalar::time_bytes).transpose()?,
        (10, Value::ChronoDateTimeUtc(v)) => v.map(scalar::instant_bytes).transpose()?,
        (10, Value::ChronoDateTimeWithTimeZone(v)) => {
            v.map(|x| scalar::instant_bytes(x.to_utc())).transpose()?
        }
        (11, Value::Double(v)) => bytes!(v),
        (12, Value::Float(v)) => bytes!(v),
        (13 | 14, Value::Decimal(v)) => v.map(scalar::decimal_bytes),
        (15, Value::Decimal(v)) => v
            .map(|v| {
                let configuration: serde_json::Value =
                    serde_json::from_str(codec.configuration.as_deref().ok_or_else(|| {
                        error(ErrorKind::Conversion, "money configuration required")
                    })?)
                    .map_err(scalar::conversion)?;
                let scale = configuration["scale"]
                    .as_u64()
                    .ok_or_else(|| error(ErrorKind::Conversion, "money scale required"))?
                    as u32;
                let v = v.normalize();
                if v.scale() > scale {
                    return Err(error(ErrorKind::Conversion, "money precision loss"));
                }
                let coefficient = v
                    .mantissa()
                    .checked_mul(10i128.pow(scale - v.scale()))
                    .ok_or_else(|| error(ErrorKind::Conversion, "money coefficient overflow"))?;
                Ok(i64::try_from(coefficient)
                    .map_err(scalar::conversion)?
                    .to_le_bytes()
                    .to_vec())
            })
            .transpose()?,
        (16, Value::Json(v)) => v
            .map(|v| {
                let mut writer = BoundedWriter {
                    bytes: Vec::new(),
                    limit,
                };
                serde_json::to_writer(&mut writer, &v).map_err(|_| {
                    error(
                        ErrorKind::Capacity,
                        "JSON serialization exceeds field capacity",
                    )
                })?;
                Ok::<_, crate::CodecError>(writer.bytes)
            })
            .transpose()?,
        _ => {
            return Err(error(
                ErrorKind::Conversion,
                "SeaQuery variant differs from admitted logical codec",
            ));
        }
    };
    if result.as_ref().is_some_and(|v| v.len() > limit) {
        return Err(error(ErrorKind::Capacity, "SeaQuery field byte limit"));
    }
    Ok(result)
}
struct BoundedWriter {
    bytes: Vec<u8>,
    limit: usize,
}
impl std::io::Write for BoundedWriter {
    fn write(&mut self, bytes: &[u8]) -> std::io::Result<usize> {
        if bytes.len() > self.limit.saturating_sub(self.bytes.len()) {
            return Err(std::io::Error::other("field byte limit"));
        }
        self.bytes.extend_from_slice(bytes);
        Ok(bytes.len())
    }
    fn flush(&mut self) -> std::io::Result<()> {
        Ok(())
    }
}
