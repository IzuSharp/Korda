//! Bounded structural scan through serde_json's parser, before any rmcp SDK parse.
use nativeweave_server_streaming::{Credit, RequestBudget, Side, StreamError};
use serde::de::{self, DeserializeSeed, MapAccess, SeqAccess, Visitor};
use std::sync::Arc;
use std::{fmt, time::Instant};

/// Separate native/managed credits are acquired atomically per side, never by a
/// wait-for-bytes loop. Partial admission is rolled back by ordinary ownership.
pub struct Admission {
    _native: Credit,
    _managed: Credit,
    _automatic: Credit,
}
pub fn admit(
    request: &Arc<RequestBudget>,
    native: u64,
    managed: u64,
    automatic: u64,
) -> Result<Admission, StreamError> {
    Ok(Admission {
        _native: request.try_acquire(Side::Native, native)?,
        _managed: request.try_acquire(Side::Managed, managed)?,
        _automatic: request.try_acquire(Side::Native, automatic)?,
    })
}

#[derive(Debug, Clone, Copy)]
pub struct Limits {
    pub wire: usize,
    pub depth: usize,
    pub nodes: usize,
    pub strings: usize,
}
impl Limits {
    pub fn validate(self) -> bool {
        (1..=1_048_576).contains(&self.wire)
            && (1..=32).contains(&self.depth)
            && (1..=4096).contains(&self.nodes)
            && (1..=262_144).contains(&self.strings)
    }
}
#[derive(Debug, Default, Clone, Copy, PartialEq, Eq)]
pub struct Shape {
    pub nodes: usize,
    pub strings: usize,
    pub members: usize,
    pub depth: usize,
}
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Error {
    InvalidJson,
    ResourceLimit,
    Timeout,
}

struct Scan {
    limits: Limits,
    shape: Shape,
    deadline: Instant,
    failure: Option<Error>,
}
impl Scan {
    fn check<E: de::Error>(&mut self, depth: usize, bytes: usize, member: bool) -> Result<(), E> {
        self.shape.nodes += 1;
        self.shape.strings = self
            .shape
            .strings
            .checked_add(bytes)
            .ok_or_else(|| E::custom("string overflow"))?;
        self.shape.members += usize::from(member);
        self.shape.depth = self.shape.depth.max(depth);
        let error = if Instant::now() >= self.deadline {
            Some(Error::Timeout)
        } else if depth > self.limits.depth
            || self.shape.nodes > self.limits.nodes
            || self.shape.strings > self.limits.strings
        {
            Some(Error::ResourceLimit)
        } else {
            None
        };
        if let Some(error) = error {
            self.failure = Some(error);
            return Err(E::custom("bounded MCP preflight"));
        }
        Ok(())
    }
}
struct ValueSeed<'a> {
    scan: &'a mut Scan,
    depth: usize,
}
impl<'de> DeserializeSeed<'de> for ValueSeed<'_> {
    type Value = ();
    fn deserialize<D: de::Deserializer<'de>>(self, deserializer: D) -> Result<(), D::Error> {
        deserializer.deserialize_any(self)
    }
}
impl<'de> Visitor<'de> for ValueSeed<'_> {
    type Value = ();
    fn expecting(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str("bounded JSON")
    }
    fn visit_bool<E: de::Error>(self, _: bool) -> Result<(), E> {
        self.scan.check(self.depth, 0, false)
    }
    fn visit_i64<E: de::Error>(self, _: i64) -> Result<(), E> {
        self.scan.check(self.depth, 0, false)
    }
    fn visit_u64<E: de::Error>(self, _: u64) -> Result<(), E> {
        self.scan.check(self.depth, 0, false)
    }
    fn visit_f64<E: de::Error>(self, _: f64) -> Result<(), E> {
        self.scan.check(self.depth, 0, false)
    }
    fn visit_unit<E: de::Error>(self) -> Result<(), E> {
        self.scan.check(self.depth, 0, false)
    }
    fn visit_str<E: de::Error>(self, value: &str) -> Result<(), E> {
        self.scan.check(self.depth, value.len(), false)
    }
    fn visit_seq<A: SeqAccess<'de>>(self, mut values: A) -> Result<(), A::Error> {
        self.scan.check(self.depth, 0, false)?;
        while values
            .next_element_seed(ValueSeed {
                scan: self.scan,
                depth: self.depth + 1,
            })?
            .is_some()
        {}
        Ok(())
    }
    fn visit_map<A: MapAccess<'de>>(self, mut fields: A) -> Result<(), A::Error> {
        self.scan.check(self.depth, 0, false)?;
        while fields
            .next_key_seed(KeySeed {
                scan: self.scan,
                depth: self.depth + 1,
            })?
            .is_some()
        {
            fields.next_value_seed(ValueSeed {
                scan: self.scan,
                depth: self.depth + 1,
            })?;
        }
        Ok(())
    }
}
struct KeySeed<'a> {
    scan: &'a mut Scan,
    depth: usize,
}
impl<'de> DeserializeSeed<'de> for KeySeed<'_> {
    type Value = ();
    fn deserialize<D: de::Deserializer<'de>>(self, d: D) -> Result<(), D::Error> {
        d.deserialize_str(self)
    }
}
impl Visitor<'_> for KeySeed<'_> {
    type Value = ();
    fn expecting(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str("bounded JSON key")
    }
    fn visit_str<E: de::Error>(self, value: &str) -> Result<(), E> {
        self.scan.check(self.depth, value.len(), true)
    }
}

/// Scan one complete JSON value without allocating an application Value tree.
/// The caller holds scan_scratch and the input backing-capacity grants throughout.
pub fn scan(bytes: &[u8], limits: Limits, deadline: Instant) -> Result<Shape, Error> {
    if !limits.validate() || bytes.len() > limits.wire {
        return Err(Error::ResourceLimit);
    }
    let mut scan = Scan {
        limits,
        shape: Shape::default(),
        deadline,
        failure: None,
    };
    let mut parser = serde_json::Deserializer::from_slice(bytes);
    if (ValueSeed {
        scan: &mut scan,
        depth: 1,
    })
    .deserialize(&mut parser)
    .is_err()
        || parser.end().is_err()
    {
        return Err(scan.failure.unwrap_or(Error::InvalidJson));
    }
    if Instant::now() >= deadline {
        return Err(Error::Timeout);
    }
    Ok(scan.shape)
}
/// Escape decoding uses serde_json's doubling Vec scratch; reserve before scanning.
/// Stack accounting is conservative for the admitted maximum32 recursive visitors.
pub fn scan_scratch(wire_capacity: usize, depth: usize) -> Option<u64> {
    wire_capacity
        .checked_mul(2)?
        .checked_add(depth.checked_mul(1024)?)?
        .checked_add(4096)
        .map(|n| n as u64)
}
/// Exact enabled profile peak: retained/recollected wire, collection growth,
/// native typed/Value nodes and BTreeMap slots, decoded strings and UTF8 bridges.
/// The formula is intentionally conservative; large valid wire bodies may not fit.
pub fn native_peak(wire_capacity: usize, shape: Shape) -> Option<u64> {
    wire_capacity
        .checked_mul(4)?
        .checked_add(shape.nodes.checked_mul(512)?)?
        .checked_add(shape.members.checked_mul(256)?)?
        .checked_add(shape.strings.checked_mul(6)?)?
        .checked_add(65_536)
        .map(|n| n as u64)
}
/// Separately held managed argument UTF8, decoder/DTO graph and response encoding.
pub fn managed_peak(wire_length: usize, shape: Shape) -> Option<u64> {
    wire_length
        .checked_mul(3)?
        .checked_add(shape.nodes.checked_mul(512)?)?
        .checked_add(shape.strings.checked_mul(8)?)?
        .checked_add(65_536)
        .map(|n| n as u64)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::time::Duration;
    fn limits() -> Limits {
        Limits {
            wire: 1_048_576,
            depth: 32,
            nodes: 4096,
            strings: 262_144,
        }
    }
    #[test]
    fn actual_serde_scan_bounds_each_resource_before_sdk_parse() {
        let end = Instant::now() + Duration::from_secs(5);
        let normal = scan(br#"{"x":[true,null,1,"\u0061"]}"#, limits(), end).unwrap();
        assert_eq!(
            (normal.nodes, normal.strings, normal.members, normal.depth),
            (7, 2, 1, 3)
        );
        let many = format!("[{}]", vec!["0"; 4096].join(","));
        assert_eq!(
            scan(many.as_bytes(), limits(), end),
            Err(Error::ResourceLimit)
        );
        let deep = format!("{}0{}", "[".repeat(33), "]".repeat(33));
        assert_eq!(
            scan(deep.as_bytes(), limits(), end),
            Err(Error::ResourceLimit)
        );
        let escaped = format!("\"{}\"", "\\u0061".repeat(100));
        let mut small = limits();
        small.strings = 99;
        assert_eq!(
            scan(escaped.as_bytes(), small, end),
            Err(Error::ResourceLimit)
        );
        for bytes in [b"{\"x\":".as_slice(), b"{}{}", b"[NaN]"] {
            assert_eq!(scan(bytes, limits(), end), Err(Error::InvalidJson));
        }
        assert_eq!(scan(b"{}", limits(), Instant::now()), Err(Error::Timeout));
        assert!(native_peak(1_048_576, normal).unwrap() > 4 * 1024 * 1024);
    }
}
