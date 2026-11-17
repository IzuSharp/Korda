//! Domain field meanings over the canonical B01 control envelope.
use crate::{Error, Result};
use nativeweave_data_contracts::{MAX_NODES, wire::control_fields};

pub struct Fields<'a>(pub Vec<(u16, &'a [u8])>);
impl<'a> Fields<'a> {
    pub fn parse(bytes: &'a [u8], kind: u16, allowed: &[(u16, u16, bool)]) -> Result<Self> {
        if bytes.len() < 16 {
            return Err(Error::invalid("truncated control"));
        }
        let count = u32::from_le_bytes(bytes[8..12].try_into().unwrap()) as usize;
        if count > allowed.len() || count > MAX_NODES || bytes.len() < 16 + count * 16 {
            return Err(Error::invalid("control count"));
        }
        let mut schema = Vec::with_capacity(count);
        let mut previous = 0;
        for n in 0..count {
            let at = 16 + n * 16;
            let id = u16::from_le_bytes(bytes[at..at + 2].try_into().unwrap());
            if id <= previous {
                return Err(Error::invalid("control order"));
            }
            previous = id;
            let (_, ty, _) = allowed
                .iter()
                .find(|(field, _, _)| *field == id)
                .ok_or_else(|| Error::invalid("unknown control field"))?;
            schema.push((id, *ty));
        }
        if allowed
            .iter()
            .any(|(id, _, required)| *required && !schema.iter().any(|(x, _)| x == id))
        {
            return Err(Error::invalid("required control field"));
        }
        let values = control_fields(bytes, kind, &schema)?;
        for ((_, ty), v) in schema.iter().zip(&values) {
            if *ty == 1 && v.len() != 4 || matches!(*ty, 2 | 3) && v.len() != 8 {
                return Err(Error::invalid("control width"));
            }
            if *ty == 4 {
                std::str::from_utf8(v).map_err(|_| Error::invalid("control UTF8"))?;
            }
        }
        Ok(Self(
            schema
                .into_iter()
                .zip(values)
                .map(|((id, _), v)| (id, v))
                .collect(),
        ))
    }
    pub fn get(&self, id: u16) -> Option<&'a [u8]> {
        self.0.iter().find(|(i, _)| *i == id).map(|(_, v)| *v)
    }
    pub fn bytes(&self, id: u16) -> Result<&'a [u8]> {
        self.get(id)
            .ok_or_else(|| Error::invalid("required field missing"))
    }
    pub fn u32(&self, id: u16) -> Result<u32> {
        Ok(u32::from_le_bytes(
            self.bytes(id)?
                .try_into()
                .map_err(|_| Error::invalid("u32 width"))?,
        ))
    }
    pub fn u64(&self, id: u16) -> Result<u64> {
        Ok(u64::from_le_bytes(
            self.bytes(id)?
                .try_into()
                .map_err(|_| Error::invalid("u64 width"))?,
        ))
    }
    pub fn text(&self, id: u16) -> Result<&'a str> {
        std::str::from_utf8(self.bytes(id)?).map_err(|_| Error::invalid("UTF8"))
    }
    pub fn boolean(&self, id: u16) -> Result<bool> {
        match self.u32(id)? {
            0 => Ok(false),
            1 => Ok(true),
            _ => Err(Error::invalid("boolean")),
        }
    }
}
pub fn encode(kind: u16, fields: &[(u16, u16, Vec<u8>)]) -> Vec<u8> {
    let size = 16 + fields.len() * 16 + fields.iter().map(|(_, _, b)| b.len()).sum::<usize>();
    let mut out = vec![0; size];
    out[..2].copy_from_slice(&1u16.to_le_bytes());
    out[2..4].copy_from_slice(&kind.to_le_bytes());
    out[4..8].copy_from_slice(&(size as u32).to_le_bytes());
    out[8..12].copy_from_slice(&(fields.len() as u32).to_le_bytes());
    let mut offset = 16 + fields.len() * 16;
    for (n, (id, ty, bytes)) in fields.iter().enumerate() {
        let at = 16 + n * 16;
        out[at..at + 2].copy_from_slice(&id.to_le_bytes());
        out[at + 2..at + 4].copy_from_slice(&ty.to_le_bytes());
        out[at + 4..at + 8].copy_from_slice(&(bytes.len() as u32).to_le_bytes());
        out[at + 8..at + 16].copy_from_slice(&(offset as u64).to_le_bytes());
        out[offset..offset + bytes.len()].copy_from_slice(bytes);
        offset += bytes.len();
    }
    out
}
pub fn sequence(bytes: &[u8]) -> Result<Vec<&[u8]>> {
    if bytes.len() < 4 {
        return Err(Error::invalid("sequence header"));
    }
    let count = u32::from_le_bytes(bytes[..4].try_into().unwrap()) as usize;
    if count > MAX_NODES {
        return Err(Error::capacity());
    }
    let mut at = 4usize;
    let mut values = Vec::with_capacity(count);
    for _ in 0..count {
        let len = u32::from_le_bytes(
            bytes
                .get(at..at + 4)
                .ok_or_else(|| Error::invalid("sequence length"))?
                .try_into()
                .unwrap(),
        ) as usize;
        at += 4;
        let end = at.checked_add(len).ok_or_else(Error::capacity)?;
        values.push(
            bytes
                .get(at..end)
                .ok_or_else(|| Error::invalid("sequence range"))?,
        );
        at = end;
    }
    if at != bytes.len() {
        return Err(Error::invalid("sequence trailing bytes"));
    }
    Ok(values)
}
pub fn quote(value: &str) -> String {
    let mut out = String::from("\"");
    for c in value.chars() {
        match c {
            '"' => out.push_str("\\\""),
            '\\' => out.push_str("\\\\"),
            '\u{0}'..='\u{1f}' => out.push_str(&format!("\\u00{:02x}", c as u32)),
            _ => out.push(c),
        }
    }
    out.push('"');
    out
}
