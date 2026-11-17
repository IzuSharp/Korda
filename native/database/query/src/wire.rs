//! Extraction over B01's envelope validator. B09 remains the operation/schema validator.
use crate::*;
use nativeweave_data_contracts::wire::{Batch, Descriptor, Expectation, control_fields};

pub(crate) struct Fields<'a>(BTreeMap<u16, &'a [u8]>);
impl<'a> Fields<'a> {
    pub fn new(bytes: &'a [u8]) -> Result<Self> {
        if bytes.len() < 16 {
            return Err(invalid("truncated clause"));
        }
        let count = u32::from_le_bytes(
            bytes[8..12]
                .try_into()
                .map_err(|_| invalid("clause header"))?,
        ) as usize;
        if count > 22 || bytes.len() < 16 + count * 16 {
            return Err(invalid("clause field count"));
        }
        let schema = (0..count)
            .map(|i| {
                let at = 16 + i * 16;
                (
                    u16::from_le_bytes([bytes[at], bytes[at + 1]]),
                    u16::from_le_bytes([bytes[at + 2], bytes[at + 3]]),
                )
            })
            .collect::<Vec<_>>();
        let fields = control_fields(bytes, 6, &schema)?;
        Ok(Self(
            schema
                .iter()
                .zip(fields)
                .map(|((id, _), v)| (*id, v))
                .collect(),
        ))
    }
    pub fn get(&self, id: u16) -> Option<&'a [u8]> {
        self.0.get(&id).copied()
    }
    pub fn bytes(&self, id: u16) -> Result<&'a [u8]> {
        self.get(id).ok_or_else(|| invalid("missing clause field"))
    }
    pub fn u32(&self, id: u16) -> Result<u32> {
        Ok(u32::from_le_bytes(
            self.bytes(id)?
                .try_into()
                .map_err(|_| invalid("u32 width"))?,
        ))
    }
    pub fn u64(&self, id: u16) -> Result<u64> {
        Ok(u64::from_le_bytes(
            self.bytes(id)?
                .try_into()
                .map_err(|_| invalid("u64 width"))?,
        ))
    }
    pub fn text(&self, id: u16) -> Result<&'a str> {
        std::str::from_utf8(self.bytes(id)?).map_err(|_| invalid("UTF8 field"))
    }
    pub fn tokens(&self, id: u16) -> Result<Vec<u64>> {
        let b = self.bytes(id)?;
        if !b.len().is_multiple_of(8) {
            return Err(invalid("token vector"));
        }
        Ok(b.as_chunks::<8>()
            .0
            .iter()
            .map(|v| u64::from_le_bytes(*v))
            .collect())
    }
}

type DecodedValues = (Codec, u32, Vec<Option<Arc<[u8]>>>);
pub(crate) fn values(
    root: &ModelBinding,
    bytes: &[u8],
    exact: Option<u32>,
    limits: Limits,
) -> Result<DecodedValues> {
    if bytes.len() < 112 || bytes.len() > limits.bytes {
        return Err(invalid("value size"));
    }
    let bound = u32::from_le_bytes(
        bytes[104..108]
            .try_into()
            .map_err(|_| invalid("codec field"))?,
    );
    let flags = u16::from_le_bytes(
        bytes[102..104]
            .try_into()
            .map_err(|_| invalid("nullable field"))?,
    );
    if flags > 1 {
        return Err(invalid("nullable flags"));
    }
    let codec = root.codecs.get(bound, flags == 1)?;
    let expected = Expectation {
        kind: 3,
        profile: root.source.profile,
        hash: [0; 32],
        entity_id: 0,
        projection_id: 0,
        descriptors: vec![Descriptor {
            id: 1,
            codec: codec.clone(),
            bound_codec: bound,
        }],
        exact_rows: exact,
    };
    let batch = Batch::parse(bytes, expected)?;
    if batch.rows() as usize > limits.parameters {
        return Err(capacity("value count"));
    }
    let cells = (0..batch.rows())
        .map(|r| batch.cell(r, 0).map(|v| v.map(Arc::from)))
        .collect::<std::result::Result<Vec<_>, _>>()?;
    Ok((codec, bound, cells))
}
