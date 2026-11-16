use crate::{
    Result, invalid,
    model::{CodecBindings, ProjectionBinding, SourceIdentity},
};
use nativeweave_data_contracts::{MAX_NODES, Projection, wire::control_fields};
use std::sync::Arc;

/// One synchronously borrowed, exact control record. B01 owns envelope/range checks.
pub(crate) struct Fields<'a>(Vec<(u16, &'a [u8])>);
impl<'a> Fields<'a> {
    pub fn parse(bytes: &'a [u8], kind: u16, allowed: &[(u16, u16, bool)]) -> Result<Self> {
        if bytes.len() < 16 {
            return Err(invalid("truncated control record"));
        }
        let count = u32::from_le_bytes(bytes[8..12].try_into().unwrap()) as usize;
        if count > allowed.len() || count > MAX_NODES || bytes.len() < 16 + count * 16 {
            return Err(invalid("control field count"));
        }
        let mut schema = Vec::with_capacity(count);
        let mut previous = 0;
        for index in 0..count {
            let offset = 16 + index * 16;
            let id = u16::from_le_bytes(bytes[offset..offset + 2].try_into().unwrap());
            if id <= previous {
                return Err(invalid("duplicate/unordered control field"));
            }
            previous = id;
            let (_, ty, _) = allowed
                .iter()
                .find(|(field, _, _)| *field == id)
                .ok_or_else(|| invalid("unknown control field"))?;
            schema.push((id, *ty));
        }
        if allowed
            .iter()
            .any(|(id, _, required)| *required && !schema.iter().any(|(found, _)| found == id))
        {
            return Err(invalid("required control field missing"));
        }
        let values = control_fields(bytes, kind, &schema)?;
        for ((_, ty), value) in schema.iter().zip(&values) {
            if matches!(*ty, 1) && value.len() != 4
                || matches!(*ty, 2 | 3) && value.len() != 8
                || *ty == 7 && !value.len().is_multiple_of(4)
                || *ty == 8 && !value.len().is_multiple_of(8)
            {
                return Err(invalid("control scalar/vector width"));
            }
            if *ty == 4 {
                std::str::from_utf8(value).map_err(|_| invalid("control UTF8"))?;
            }
        }
        Ok(Self(
            schema
                .into_iter()
                .zip(values)
                .map(|((id, _), value)| (id, value))
                .collect(),
        ))
    }
    pub fn get(&self, id: u16) -> Option<&'a [u8]> {
        self.0.iter().find(|(key, _)| *key == id).map(|(_, v)| *v)
    }
    pub fn bytes(&self, id: u16) -> Result<&'a [u8]> {
        self.get(id)
            .ok_or_else(|| invalid("required control field missing"))
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
        std::str::from_utf8(self.bytes(id)?).map_err(|_| invalid("UTF8"))
    }
    pub fn flag(&self, id: u16) -> Result<bool> {
        match self.u32(id)? {
            0 => Ok(false),
            1 => Ok(true),
            _ => Err(invalid("boolean flag")),
        }
    }
    pub fn require_set(&self, ids: &[u16]) -> Result<()> {
        if self.0.len() != ids.len() || self.0.iter().zip(ids).any(|((a, _), b)| a != b) {
            Err(invalid("operation field set"))
        } else {
            Ok(())
        }
    }
}
pub(crate) fn quote(value: &str) -> String {
    let mut result = String::from("\"");
    for c in value.chars() {
        match c {
            '"' => result.push_str("\\\""),
            '\\' => result.push_str("\\\\"),
            '\u{0}'..='\u{1f}' => result.push_str(&format!("\\u00{:02x}", c as u32)),
            _ => result.push(c),
        }
    }
    result.push('"');
    result
}
pub(crate) fn sequence(bytes: &[u8]) -> Result<Vec<&[u8]>> {
    if bytes.len() < 4 {
        return Err(invalid("truncated record sequence"));
    }
    let count = u32::from_le_bytes(bytes[..4].try_into().unwrap()) as usize;
    if count == 0 || count > MAX_NODES {
        return Err(invalid("record sequence count"));
    }
    let mut offset = 4usize;
    let mut result = Vec::with_capacity(count);
    for _ in 0..count {
        let end = offset
            .checked_add(4)
            .ok_or_else(|| invalid("record length overflow"))?;
        let len = u32::from_le_bytes(
            bytes
                .get(offset..end)
                .ok_or_else(|| invalid("record length"))?
                .try_into()
                .unwrap(),
        ) as usize;
        offset = end;
        let end = offset
            .checked_add(len)
            .ok_or_else(|| invalid("record length overflow"))?;
        result.push(
            bytes
                .get(offset..end)
                .ok_or_else(|| invalid("record range"))?,
        );
        offset = end;
    }
    if offset != bytes.len() {
        return Err(invalid("record sequence trailing bytes"));
    }
    Ok(result)
}
/// Parse exact kind13/kind22 source-bound shape into the existing B01 projection.
pub fn projection_record(
    bytes: &[u8],
    source: SourceIdentity,
    id: u32,
    codecs: Arc<CodecBindings>,
    inline_tokens: Option<&[u64]>,
) -> Result<ProjectionBinding> {
    let fields = Fields::parse(bytes, 13, &[(1, 5, true), (2, 5, true)])?;
    let records = sequence(fields.bytes(1)?)?;
    if inline_tokens.is_some_and(|tokens| tokens.len() != records.len()) {
        return Err(invalid("inline projection arity"));
    }
    let mut canonical = Vec::with_capacity(records.len());
    for (ordinal, record) in records.iter().enumerate() {
        let field = Fields::parse(
            record,
            22,
            &[
                (1, 1, true),
                (2, 4, true),
                (3, 1, true),
                (4, 1, true),
                (5, 2, false),
            ],
        )?;
        if field.u32(1)? as usize != ordinal {
            return Err(invalid("projection ordinal"));
        }
        if let Some(tokens) = inline_tokens {
            if field.get(5).is_some() && field.u64(5)? != tokens[ordinal] {
                return Err(invalid("projection token mismatch"));
            }
        } else if field.get(5).is_some() {
            return Err(invalid("registered projection contains query token"));
        }
        let codec = codecs.get(field.u32(3)?, field.flag(4)?)?;
        canonical.push(format!("{{\"ordinal\":{ordinal},\"name\":{},\"codec\":{},\"codec_config\":{},\"nullable\":{}}}",quote(field.text(2)?),codec.id,codec.configuration.as_deref().unwrap_or("null"),codec.nullable));
    }
    let text = format!("{{\"version\":1,\"fields\":[{}]}}", canonical.join(","));
    let projection = Projection::parse(text.as_bytes(), source.profile)?;
    if fields.bytes(2)? != projection.hash() {
        return Err(invalid("projection logical hash"));
    }
    ProjectionBinding::new(source, id, Arc::new(projection), codecs)
}

/// Read only the first required u32 selector with checked table/range bounds;
/// the selected exact schema still validates the complete record afterward.
pub(crate) fn discriminant(bytes: &[u8]) -> Result<u32> {
    if bytes.len() < 32 {
        return Err(invalid("missing operation selector"));
    }
    let count = u32::from_le_bytes(bytes[8..12].try_into().unwrap()) as usize;
    if count == 0
        || count > MAX_NODES
        || 16 + count * 16 > bytes.len()
        || bytes[16..24] != [1, 0, 1, 0, 4, 0, 0, 0]
    {
        return Err(invalid("operation selector field"));
    }
    let offset = u64::from_le_bytes(bytes[24..32].try_into().unwrap());
    if offset < (16 + count * 16) as u64
        || offset > bytes.len() as u64
        || 4 > bytes.len() as u64 - offset
    {
        return Err(invalid("operation selector range"));
    }
    Ok(u32::from_le_bytes(
        bytes[offset as usize..offset as usize + 4]
            .try_into()
            .unwrap(),
    ))
}
