//! Bounds-checked db-wire/1 and canonical kind23 relation records.
use crate::{
    Codec, ContractError, MAX_BYTES, MAX_NODES, Model, Profile, Result, numbers, quote, sha,
};
use std::collections::HashSet;
use std::sync::Arc;

/// An exact source-bound scalar column expected by the admitted operation.
#[derive(Debug, Clone)]
pub struct Descriptor {
    /// Column, parameter or projection-ordinal identity.
    pub id: u32,
    /// Logical scalar and nullable metadata.
    pub codec: Codec,
    /// Source-bound codec ID; configured codec IDs start at 1024.
    pub bound_codec: u32,
}
/// Expected operation identity supplied by the admitted source/plan, never trusted from the wire itself.
#[derive(Debug, Clone)]
pub struct Expectation {
    /// Exact allowed payload kind, 1 through 4.
    pub kind: u16,
    /// Ordinary semantic profile.
    pub profile: Profile,
    /// Complete admitted metadata/projection/parameter hash.
    pub hash: [u8; 32],
    /// Declared table ID for mutation payloads; otherwise zero.
    pub entity_id: u32,
    /// Positive registered projection ID for result batches; otherwise zero.
    pub projection_id: u32,
    /// Exact ordered descriptor shape.
    pub descriptors: Vec<Descriptor>,
}
impl Expectation {
    fn validate(&self, rows: u32) -> Result<()> {
        if !(1..=4).contains(&self.kind) || self.descriptors.len() > MAX_NODES {
            return Err(ContractError("unknown kind or descriptor limit"));
        }
        if self.kind == 1 && rows != 1
            || self.kind == 4 && (rows == 0 || self.entity_id == 0)
            || self.kind == 3
                && (self.descriptors.len() != 1
                    || self.descriptors[0].id != 1
                    || self.hash != [0; 32])
            || self.kind != 4 && self.entity_id != 0
            || (self.kind == 2) != (self.projection_id > 0)
        {
            return Err(ContractError("invalid kind-specific identity/count"));
        }
        let mut ids = HashSet::new();
        for d in &self.descriptors {
            d.codec.validate(self.profile)?;
            if !ids.insert(d.id) || self.kind != 2 && d.id == 0 {
                return Err(ContractError("duplicate/reserved descriptor ID"));
            }
            if d.codec.configuration.is_none() && d.bound_codec != d.codec.id
                || d.codec.configuration.is_some() && d.bound_codec < 1024
            {
                return Err(ContractError("invalid source-bound codec"));
            }
        }
        Ok(())
    }
}

/// A validated owned row-major batch, parameter sequence or mutation value envelope.
#[derive(Debug)]
pub struct Batch {
    /// Independent owned encoded bytes; no caller borrow survives parsing.
    bytes: Vec<u8>,
    /// Exact admitted identity used for validation.
    expectation: Expectation,
    /// Number of complete rows, including zero-row EOS where allowed.
    rows: u32,
}
impl Batch {
    /// Returns validated owned bytes without granting mutation.
    pub fn bytes(&self) -> &[u8] {
        &self.bytes
    }
    /// Returns the validated row count.
    pub fn rows(&self) -> u32 {
        self.rows
    }

    /// Rejects malformed identities, lengths, NULLs, scalar values and overlapping ranges before returning an owner.
    pub fn parse(bytes: &[u8], expected: Expectation) -> Result<Self> {
        if bytes.len() < 96 || bytes.len() > MAX_BYTES || &bytes[..4] != b"NWDB" {
            return Err(ContractError("invalid wire header/size"));
        }
        let rows = u32at(bytes, 56)?;
        expected.validate(rows)?;
        if u16at(bytes, 4)? != 1
            || u16at(bytes, 6)? != expected.kind
            || u32at(bytes, 8)? != 96
            || u32at(bytes, 12)? != 0
            || u64at(bytes, 16)? != bytes.len() as u64
            || bytes[24..56] != expected.hash
            || u32at(bytes, 60)? as usize != expected.descriptors.len()
            || u32at(bytes, 64)? != expected.entity_id
            || u32at(bytes, 68)? != 0
            || u32at(bytes, 72)? != expected.projection_id
            || Profile::try_from(u32at(bytes, 76)?)? != expected.profile
            || u64at(bytes, 80)? != 96
        {
            return Err(ContractError("version/profile/identity/header mismatch"));
        }
        let count = (rows as usize)
            .checked_mul(expected.descriptors.len())
            .ok_or(ContractError("cell count overflow"))?;
        let cells_offset = 96usize
            .checked_add(
                expected
                    .descriptors
                    .len()
                    .checked_mul(16)
                    .ok_or(ContractError("descriptor overflow"))?,
            )
            .ok_or(ContractError("descriptor overflow"))?;
        let payload_offset = cells_offset
            .checked_add(
                count
                    .checked_mul(24)
                    .ok_or(ContractError("cell size overflow"))?,
            )
            .ok_or(ContractError("cell size overflow"))?;
        if payload_offset > bytes.len() || u64at(bytes, 88)? != payload_offset as u64 {
            return Err(ContractError("truncated/overlapping tables"));
        }
        for (i, d) in expected.descriptors.iter().enumerate() {
            let o = 96 + i * 16;
            if u32at(bytes, o)? != d.id
                || u16at(bytes, o + 4)? as u32 != d.codec.id
                || u16at(bytes, o + 6)? != u16::from(d.codec.nullable)
                || u32at(bytes, o + 8)? != d.bound_codec
                || u32at(bytes, o + 12)? != 0
            {
                return Err(ContractError("descriptor shape mismatch"));
            }
        }
        let mut ranges = Vec::new();
        for i in 0..count {
            let d = &expected.descriptors[i % expected.descriptors.len()];
            let o = cells_offset + i * 24;
            let flags = u16at(bytes, o + 2)?;
            let start = u64at(bytes, o + 8)?;
            let length = u64at(bytes, o + 16)?;
            if u16at(bytes, o)? as u32 != d.codec.id
                || u32at(bytes, o + 4)? != d.bound_codec
                || flags > 1
            {
                return Err(ContractError("cell codec/flags mismatch"));
            }
            if flags == 1 {
                if !d.codec.nullable || start != 0 || length != 0 {
                    return Err(ContractError("invalid NULL cell"));
                }
                continue;
            }
            if start < payload_offset as u64
                || start > bytes.len() as u64
                || length > bytes.len() as u64 - start
                || length == 0 && start != payload_offset as u64
            {
                return Err(ContractError("invalid cell range"));
            }
            if length > 0 {
                ranges.push((start, start + length));
            }
            validate_scalar(
                &d.codec,
                &bytes[start as usize..(start + length) as usize],
                expected.profile,
            )?;
        }
        ranges.sort_unstable();
        if ranges.windows(2).any(|p| p[1].0 < p[0].1) {
            return Err(ContractError("overlapping cell data"));
        }
        Ok(Self {
            bytes: bytes.to_vec(),
            expectation: expected,
            rows,
        })
    }
    /// Re-encodes owned values with canonical offsets; used for independent cross-language goldens.
    pub fn encode(expected: &Expectation, rows: u32, values: &[Option<&[u8]>]) -> Result<Vec<u8>> {
        expected.validate(rows)?;
        let count = (rows as usize)
            .checked_mul(expected.descriptors.len())
            .ok_or(ContractError("cell count overflow"))?;
        if values.len() != count {
            return Err(ContractError("row-major cell count mismatch"));
        }
        let payload = 96usize
            .checked_add(
                expected
                    .descriptors
                    .len()
                    .checked_mul(16)
                    .ok_or(ContractError("descriptor overflow"))?,
            )
            .and_then(|n| n.checked_add(count.checked_mul(24)?))
            .ok_or(ContractError("wire length overflow"))?;
        let total = values.iter().try_fold(payload, |n, v| {
            n.checked_add(v.map_or(0, |b| b.len()))
                .ok_or(ContractError("payload length overflow"))
        })?;
        if total > MAX_BYTES {
            return Err(ContractError("wire byte budget"));
        }
        let mut bytes = vec![0; total];
        bytes[..4].copy_from_slice(b"NWDB");
        put16(&mut bytes, 4, 1);
        put16(&mut bytes, 6, expected.kind);
        put32(&mut bytes, 8, 96);
        put64(&mut bytes, 16, total as u64);
        bytes[24..56].copy_from_slice(&expected.hash);
        put32(&mut bytes, 56, rows);
        put32(&mut bytes, 60, expected.descriptors.len() as u32);
        put32(&mut bytes, 64, expected.entity_id);
        put32(&mut bytes, 72, expected.projection_id);
        put32(&mut bytes, 76, expected.profile as u32);
        put64(&mut bytes, 80, 96);
        put64(&mut bytes, 88, payload as u64);
        for (i, d) in expected.descriptors.iter().enumerate() {
            let o = 96 + i * 16;
            put32(&mut bytes, o, d.id);
            put16(&mut bytes, o + 4, d.codec.id as u16);
            put16(&mut bytes, o + 6, u16::from(d.codec.nullable));
            put32(&mut bytes, o + 8, d.bound_codec);
        }
        let mut cursor = payload;
        for (i, value) in values.iter().enumerate() {
            let d = &expected.descriptors[i % expected.descriptors.len()];
            let o = 96 + expected.descriptors.len() * 16 + i * 24;
            put16(&mut bytes, o, d.codec.id as u16);
            put16(&mut bytes, o + 2, u16::from(value.is_none()));
            put32(&mut bytes, o + 4, d.bound_codec);
            if let Some(value) = value {
                validate_scalar(&d.codec, value, expected.profile)?;
                put64(
                    &mut bytes,
                    o + 8,
                    if value.is_empty() { payload } else { cursor } as u64,
                );
                put64(&mut bytes, o + 16, value.len() as u64);
                bytes[cursor..cursor + value.len()].copy_from_slice(value);
                cursor += value.len();
            } else if !d.codec.nullable {
                return Err(ContractError("SQL NULL requires nullable metadata"));
            }
        }
        Ok(bytes)
    }
    /// Reads a validated cell while retaining its batch owner; NULL is not an absent ordinal.
    pub fn cell(&self, row: u32, column: usize) -> Result<Option<&[u8]>> {
        if row >= self.rows || column >= self.expectation.descriptors.len() {
            return Err(ContractError("cell ordinal out of range"));
        }
        let o = 96
            + self.expectation.descriptors.len() * 16
            + (row as usize * self.expectation.descriptors.len() + column) * 24;
        if u16at(&self.bytes, o + 2)? == 1 {
            return Ok(None);
        }
        let start = u64at(&self.bytes, o + 8)? as usize;
        let length = u64at(&self.bytes, o + 16)? as usize;
        Ok(Some(&self.bytes[start..start + length]))
    }
}

/// Validates fixed wire representations without implementing any provider database conversion.
pub fn validate_scalar(codec: &Codec, bytes: &[u8], profile: Profile) -> Result<()> {
    codec.validate(profile)?;
    let fixed = match codec.id {
        1 => Some(2),
        2 | 8 | 12 | 17 => Some(4),
        3 | 9 | 10 | 11 | 15 => Some(8),
        4 => Some(1),
        7 | 13 | 14 => Some(16),
        _ => None,
    };
    if fixed.is_some_and(|n| bytes.len() != n) {
        return Err(ContractError("scalar fixed width mismatch"));
    }
    match codec.id {
        4 if bytes[0] > 1 => return Err(ContractError("invalid Boolean")),
        5 => {
            std::str::from_utf8(bytes).map_err(|_| ContractError("invalid UTF-8"))?;
        }
        8 => {
            let v = i32::from_le_bytes(bytes.try_into().map_err(|_| ContractError("date width"))?);
            if !(-719162..=2932896).contains(&v) {
                return Err(ContractError("date range"));
            }
        }
        9 if u64at(bytes, 0)? > 86399999999 => return Err(ContractError("time range")),
        10 => {
            let v = i64::from_le_bytes(
                bytes
                    .try_into()
                    .map_err(|_| ContractError("instant width"))?,
            );
            if !(-62135596800000000..=253402300799999999).contains(&v) {
                return Err(ContractError("instant range"));
            }
        }
        11 if !f64::from_le_bytes(
            bytes
                .try_into()
                .map_err(|_| ContractError("double width"))?,
        )
        .is_finite() =>
        {
            return Err(ContractError("nonfinite double"));
        }
        12 if !f32::from_le_bytes(
            bytes
                .try_into()
                .map_err(|_| ContractError("single width"))?,
        )
        .is_finite() =>
        {
            return Err(ContractError("nonfinite single"));
        }
        13 | 14 => {
            if bytes[12] > 28 || bytes[13] > 1 || bytes[14] != 0 || bytes[15] != 0 {
                return Err(ContractError("decimal flags/range"));
            }
            let mut coefficient = [0; 16];
            coefficient[..12].copy_from_slice(&bytes[..12]);
            let coefficient = u128::from_le_bytes(coefficient);
            let scale = bytes[12];
            if coefficient == 0 && (scale != 0 || bytes[13] != 0)
                || coefficient != 0 && scale > 0 && coefficient % 10 == 0
            {
                return Err(ContractError("noncanonical decimal"));
            }
            if codec.id == 13 {
                let config: serde_json::Value = serde_json::from_str(
                    codec
                        .configuration
                        .as_deref()
                        .ok_or(ContractError("decimal config"))?,
                )
                .map_err(|_| ContractError("decimal config"))?;
                let target_scale = config["scale"]
                    .as_u64()
                    .ok_or(ContractError("decimal scale"))?
                    as u8;
                let precision = config["precision"]
                    .as_u64()
                    .ok_or(ContractError("decimal precision"))?
                    as u32;
                if scale > target_scale
                    || coefficient
                        .checked_mul(10u128.pow((target_scale - scale) as u32))
                        .is_none_or(|v| v >= 10u128.pow(precision))
                {
                    return Err(ContractError("decimal precision loss/overflow"));
                }
            }
        }
        16 => {
            if bytes.starts_with(&[0xef, 0xbb, 0xbf]) {
                return Err(ContractError("JSON BOM"));
            }
            let value: serde_json::Value =
                serde_json::from_slice(bytes).map_err(|_| ContractError("invalid JSON"))?;
            fn depth(value: &serde_json::Value, level: usize) -> bool {
                level <= 64
                    && match value {
                        serde_json::Value::Array(a) => a.iter().all(|v| depth(v, level + 1)),
                        serde_json::Value::Object(o) => o.values().all(|v| depth(v, level + 1)),
                        _ => true,
                    }
            }
            if !depth(&value, 0) {
                return Err(ContractError("JSON depth limit"));
            }
        }
        _ => {}
    }
    Ok(())
}

/// A complete immutable relation retaining both admitted model owners.
#[derive(Debug, Clone)]
pub struct Relation {
    /// Retained parent model.
    parent: Arc<Model>,
    /// Retained child model.
    child: Arc<Model>,
    /// Complete primary-key IDs in key ordinal order.
    parent_columns: Vec<u32>,
    /// Distinct matching child FK IDs in pair order.
    child_columns: Vec<u32>,
    /// Per-pair optional FK flags.
    child_nullable: Vec<bool>,
    /// Exact independent canonical relation identity.
    canonical: Vec<u8>,
    /// SHA-256 of that independent identity.
    hash: [u8; 32],
}
impl Relation {
    /// Returns the immutable canonical relation identity.
    pub fn bytes(&self) -> &[u8] {
        &self.canonical
    }
    /// Returns the relation identity hash.
    pub fn hash(&self) -> [u8; 32] {
        self.hash
    }
    /// Returns the retained parent model.
    pub fn parent(&self) -> &Model {
        &self.parent
    }
    /// Returns the retained child model.
    pub fn child(&self) -> &Model {
        &self.child
    }

    /// Validates kind23 against both already admitted models before returning a retained owner.
    pub fn parse(bytes: &[u8], parent: Arc<Model>, child: Arc<Model>) -> Result<Self> {
        let fields = control_fields(
            bytes,
            23,
            &[
                (1, 1),
                (2, 5),
                (3, 1),
                (4, 5),
                (5, 7),
                (6, 7),
                (7, 7),
                (8, 5),
            ],
        )?;
        if fields[0].len() != 4
            || fields[2].len() != 4
            || u32at(fields[0], 0)? != parent.table_id
            || u32at(fields[2], 0)? != child.table_id
            || fields[1] != parent.hash
            || fields[3] != child.hash
            || fields[7].len() != 32
        {
            return Err(ContractError("relation model identity mismatch"));
        }
        let parent_columns = vector(fields[4])?;
        let child_columns = vector(fields[5])?;
        let flags = vector(fields[6])?;
        if parent_columns.is_empty()
            || parent_columns.len() != child_columns.len()
            || parent_columns.len() != flags.len()
            || parent_columns.len() != parent.keys().len()
            || flags.iter().any(|n| *n > 1)
        {
            return Err(ContractError("incomplete relation pair vectors"));
        }
        let mut child_ids = HashSet::new();
        for (ordinal, ((p, c), nullable)) in parent_columns
            .iter()
            .zip(&child_columns)
            .zip(&flags)
            .enumerate()
        {
            let p = parent.column(*p)?;
            let c = child.column(*c)?;
            if p.key_ordinal != Some(ordinal as u32)
                || p.codec.nullable
                || c.codec.nullable != (*nullable == 1)
                || !p.codec.same_scalar(&c.codec)
                || p.collation != c.collation
                || !child_ids.insert(c.id)
            {
                return Err(ContractError(
                    "relation key/codec/configuration/collation/NULL mismatch",
                ));
            }
        }
        let child_nullable = flags.iter().map(|n| *n == 1).collect();
        let mut relation = Self {
            parent,
            child,
            parent_columns,
            child_columns,
            child_nullable,
            canonical: Vec::new(),
            hash: [0; 32],
        };
        relation.canonical = relation.encode_identity();
        relation.hash = sha(&relation.canonical);
        if fields[7] != relation.hash {
            return Err(ContractError("relation hash mismatch"));
        }
        Ok(relation)
    }
    /// Encodes the independent relation identity without mutating either model hash.
    pub fn encode_identity(&self) -> Vec<u8> {
        format!("{{\"version\":1,\"parent_model\":{},\"parent_table\":{},\"child_model\":{},\"child_table\":{},\"parent_columns\":[{}],\"child_columns\":[{}],\"child_nullable\":[{}]}}", quote(&hex(&self.parent.hash)), self.parent.table_id, quote(&hex(&self.child.hash)), self.child.table_id, numbers(&self.parent_columns), numbers(&self.child_columns), self.child_nullable.iter().map(bool::to_string).collect::<Vec<_>>().join(",")).into_bytes()
    }
    /// Encodes canonical sorted kind23 control fields for a cross-language golden.
    pub fn encode_record(&self) -> Vec<u8> {
        let fields = [
            self.parent.table_id.to_le_bytes().to_vec(),
            self.parent.hash.to_vec(),
            self.child.table_id.to_le_bytes().to_vec(),
            self.child.hash.to_vec(),
            encode_vector(&self.parent_columns),
            encode_vector(&self.child_columns),
            encode_vector(
                &self
                    .child_nullable
                    .iter()
                    .map(|n| u32::from(*n))
                    .collect::<Vec<_>>(),
            ),
            self.hash.to_vec(),
        ];
        let types = [1u16, 5, 1, 5, 7, 7, 7, 5];
        let start = 16 + fields.len() * 16;
        let size = start + fields.iter().map(Vec::len).sum::<usize>();
        let mut output = vec![0; size];
        put16(&mut output, 0, 1);
        put16(&mut output, 2, 23);
        put32(&mut output, 4, size as u32);
        put32(&mut output, 8, fields.len() as u32);
        let mut cursor = start;
        for (i, field) in fields.iter().enumerate() {
            let o = 16 + i * 16;
            put16(&mut output, o, (i + 1) as u16);
            put16(&mut output, o + 2, types[i]);
            put32(&mut output, o + 4, field.len() as u32);
            put64(&mut output, o + 8, cursor as u64);
            output[cursor..cursor + field.len()].copy_from_slice(field);
            cursor += field.len();
        }
        output
    }
    /// Defines the exact parent-key db-wire expectation for one-parent loading.
    pub fn parent_key_expectation(
        &self,
        profile: Profile,
        bound_codecs: &[u32],
    ) -> Result<Expectation> {
        if bound_codecs.len() != self.parent_columns.len() {
            return Err(ContractError("parent-key codec bindings missing"));
        }
        let descriptors = self
            .parent_columns
            .iter()
            .zip(bound_codecs)
            .map(|(id, bound)| {
                Ok(Descriptor {
                    id: *id,
                    codec: self.parent.column(*id)?.codec.clone(),
                    bound_codec: *bound,
                })
            })
            .collect::<Result<Vec<_>>>()?;
        Ok(Expectation {
            kind: 4,
            profile,
            hash: self.parent.hash,
            entity_id: self.parent.table_id,
            projection_id: 0,
            descriptors,
        })
    }
}

fn control_fields<'a>(bytes: &'a [u8], kind: u16, fields: &[(u16, u16)]) -> Result<Vec<&'a [u8]>> {
    let start = 16 + fields.len() * 16;
    if bytes.len() < start
        || bytes.len() > MAX_BYTES
        || u16at(bytes, 0)? != 1
        || u16at(bytes, 2)? != kind
        || u32at(bytes, 4)? as usize != bytes.len()
        || u32at(bytes, 8)? as usize != fields.len()
        || u32at(bytes, 12)? != 0
    {
        return Err(ContractError("invalid control envelope"));
    }
    let mut result = Vec::with_capacity(fields.len());
    let mut ranges = Vec::new();
    for (i, (id, ty)) in fields.iter().enumerate() {
        let o = 16 + i * 16;
        let length = u32at(bytes, o + 4)? as u64;
        let offset = u64at(bytes, o + 8)?;
        if u16at(bytes, o)? != *id
            || u16at(bytes, o + 2)? != *ty
            || offset < start as u64
            || offset > bytes.len() as u64
            || length > bytes.len() as u64 - offset
        {
            return Err(ContractError("control field/length/offset mismatch"));
        }
        if length > 0 {
            ranges.push((offset, offset + length));
        }
        result.push(&bytes[offset as usize..(offset + length) as usize]);
    }
    ranges.sort_unstable();
    if ranges.windows(2).any(|p| p[1].0 < p[0].1) {
        return Err(ContractError("overlapping control ranges"));
    }
    Ok(result)
}
fn vector(bytes: &[u8]) -> Result<Vec<u32>> {
    if !bytes.len().is_multiple_of(4) || bytes.len() / 4 > MAX_NODES {
        return Err(ContractError("invalid u32 vector"));
    }
    bytes.chunks_exact(4).map(|b| u32at(b, 0)).collect()
}
fn encode_vector(values: &[u32]) -> Vec<u8> {
    values.iter().flat_map(|n| n.to_le_bytes()).collect()
}
/// Lowercase hexadecimal SHA-256 formatting used by canonical metadata identities.
pub fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!("{b:02x}")).collect()
}
fn u16at(bytes: &[u8], offset: usize) -> Result<u16> {
    Ok(u16::from_le_bytes(
        bytes
            .get(offset..offset + 2)
            .ok_or(ContractError("u16 read bounds"))?
            .try_into()
            .map_err(|_| ContractError("u16 read width"))?,
    ))
}
fn u32at(bytes: &[u8], offset: usize) -> Result<u32> {
    Ok(u32::from_le_bytes(
        bytes
            .get(offset..offset + 4)
            .ok_or(ContractError("u32 read bounds"))?
            .try_into()
            .map_err(|_| ContractError("u32 read width"))?,
    ))
}
fn u64at(bytes: &[u8], offset: usize) -> Result<u64> {
    Ok(u64::from_le_bytes(
        bytes
            .get(offset..offset + 8)
            .ok_or(ContractError("u64 read bounds"))?
            .try_into()
            .map_err(|_| ContractError("u64 read width"))?,
    ))
}
fn put16(bytes: &mut [u8], o: usize, value: u16) {
    bytes[o..o + 2].copy_from_slice(&value.to_le_bytes());
}
fn put32(bytes: &mut [u8], o: usize, value: u32) {
    bytes[o..o + 4].copy_from_slice(&value.to_le_bytes());
}
fn put64(bytes: &mut [u8], o: usize, value: u64) {
    bytes[o..o + 8].copy_from_slice(&value.to_le_bytes());
}
