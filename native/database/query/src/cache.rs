use crate::*;
use nativeweave_data_contracts::{MAX_NODES, wire::Expectation};
use sha2::{Digest, Sha256};
use std::collections::VecDeque;

/// Immutable compiled SQL and its ordered parameter schema. It owns no values,
/// session, SQLx Arguments, callback, runtime, or managed owner.
#[derive(Clone)]
pub struct CompiledShape {
    pub(crate) sql: Arc<str>,
    pub(crate) expectation: Expectation,
    pub(crate) identity: Arc<[u8]>,
    pub(crate) charge: usize,
}
impl CompiledShape {
    /// The SQL is generated exclusively by SeaQuery from checked operations.
    pub fn sql(&self) -> &str {
        &self.sql
    }
    /// Retained bytes charged to the finite source cache, including owned codecs.
    pub fn retained_bytes(&self) -> usize {
        self.charge
    }
}

struct Entry {
    key: [u8; 32],
    shape: Arc<CompiledShape>,
}
/// A source-owned LRU of immutable compiled shapes. B04 supplies the exact module
/// ABI fingerprint and holds its source admission lock; no global cache exists.
/// Eviction drops only the cache root, preserving leases held by active calls.
pub struct ShapeCache {
    abi: [u8; 32],
    entries: VecDeque<Entry>,
    max_entries: usize,
    max_bytes: usize,
    bytes: usize,
}
impl ShapeCache {
    /// Finite limits are required even when a source has few application models.
    pub fn new(max_entries: usize, max_bytes: usize, abi: [u8; 32]) -> Result<Self> {
        if max_entries == 0 || max_entries > MAX_NODES || max_bytes == 0 || max_bytes > (1 << 30) {
            return Err(invalid("compiled shape cache bounds"));
        }
        if abi == [0; 32] {
            return Err(invalid("compiled shape ABI identity"));
        }
        Ok(Self {
            abi,
            entries: VecDeque::new(),
            max_entries,
            max_bytes,
            bytes: 0,
        })
    }
    /// Current retained cache roots; in-use evicted leases are deliberately external.
    pub fn counts(&self) -> (usize, usize) {
        (self.entries.len(), self.bytes)
    }
    /// Releases cache roots during source close without invalidating in-use shapes.
    pub fn clear(&mut self) {
        self.entries.clear();
        self.bytes = 0;
    }
    /// Interns a plan's compiled, immutable SQL/schema. Ordinary values are not in
    /// the identity. Exact identity comparison follows hashing, so a collision is
    /// an admission error rather than a chance to substitute another SQL shape.
    pub fn acquire(&mut self, plan: &NativePlan) -> Result<Arc<CompiledShape>> {
        let shape = Arc::clone(&plan.compiled()?.shape);
        let mut hash = Sha256::new();
        hash.update(self.abi);
        hash.update(&shape.identity);
        let key: [u8; 32] = hash.finalize().into();
        if let Some(index) = self.entries.iter().position(|e| e.key == key) {
            let entry = self.entries.remove(index).expect("position was checked");
            if entry.shape.identity != shape.identity {
                self.bytes -= entry.shape.charge;
                return Err(invalid("compiled shape hash collision"));
            }
            let result = Arc::clone(&entry.shape);
            self.entries.push_back(entry);
            return Ok(result);
        }
        // An individually oversized shape is executable within its own query
        // limits, but never defeats the smaller cache budget or evicts all roots.
        if shape.charge > self.max_bytes {
            return Ok(shape);
        }
        while self.entries.len() >= self.max_entries
            || shape.charge > self.max_bytes.saturating_sub(self.bytes)
        {
            let entry = self.entries.pop_front().expect("nonempty bounded cache");
            self.bytes -= entry.shape.charge;
        }
        self.bytes += shape.charge;
        self.entries.push_back(Entry {
            key,
            shape: Arc::clone(&shape),
        });
        Ok(shape)
    }
}
