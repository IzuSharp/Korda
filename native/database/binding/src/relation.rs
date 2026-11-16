use crate::{clauses::model_row_matches, control::Fields, *};
use nativeweave_data_contracts::{
    MAX_BYTES, MAX_NODES,
    wire::{Batch, Relation},
};
use std::{collections::HashMap, sync::Arc};

/// Engine-profile prerequisites for independent relation registration/loading.
/// B04 derives these booleans from the admitted native capability snapshot.
#[derive(Clone, Copy, Debug)]
pub struct RelationCapabilities {
    pub metadata: bool,
    pub scalar_codecs: bool,
    pub query: bool,
    pub streaming: bool,
    pub explicit_relationships: bool,
}
impl RelationCapabilities {
    pub fn validate(self) -> Result<()> {
        if self.metadata
            && self.scalar_codecs
            && self.query
            && self.streaming
            && self.explicit_relationships
        {
            Ok(())
        } else {
            Err(crate::unsupported(
                "relation metadata/codecs/query/streaming/explicit-relationships capabilities",
            ))
        }
    }
}
/// Source-bound B01 relation; it retains both immutable model/codec bindings.
#[derive(Debug)]
pub struct RelationBinding {
    pub source: SourceIdentity,
    pub relation: Relation,
    pub parent: ModelBinding,
    pub child: ModelBinding,
    key_codecs: Vec<u32>,
    capabilities: RelationCapabilities,
}
impl RelationBinding {
    pub fn register(
        source: SourceIdentity,
        parent: ModelBinding,
        child: ModelBinding,
        definition: &[u8],
        capabilities: RelationCapabilities,
    ) -> Result<Self> {
        capabilities.validate()?;
        parent.ensure_source(source)?;
        child.ensure_source(source)?;
        let relation = Relation::parse(
            definition,
            Arc::clone(&parent.model),
            Arc::clone(&child.model),
        )?;
        let fields = Fields::parse(
            definition,
            23,
            &[
                (1, 1, true),
                (2, 5, true),
                (3, 1, true),
                (4, 5, true),
                (5, 7, true),
                (6, 7, true),
                (7, 7, true),
                (8, 5, true),
            ],
        )?;
        let mut key_codecs = Vec::with_capacity(parent.model.keys().len());
        for (p, c) in fields
            .bytes(5)?
            .as_chunks::<4>()
            .0
            .iter()
            .zip(fields.bytes(6)?.as_chunks::<4>().0.iter())
        {
            let p = parent.model.column(u32::from_le_bytes(*p))?;
            let c = child.model.column(u32::from_le_bytes(*c))?;
            let bound = parent.codecs.bound(&p.codec)?;
            if bound != child.codecs.bound(&c.codec)? {
                return Err(invalid("relation bound codec identity"));
            }
            key_codecs.push(bound);
        }
        Ok(Self {
            source,
            relation,
            parent,
            child,
            key_codecs,
            capabilities,
        })
    }
    /// No SQL/provider work. Copies borrowed key bytes and retains every dependency
    /// before returning an argument snapshot for B04's Created operation.
    pub fn load(
        self: &Arc<Self>,
        session_source: SourceIdentity,
        key: &[u8],
        projection: ProjectionBinding,
        limits: Limits,
        timeout_ns: u64,
    ) -> Result<RelationLoad> {
        self.capabilities.validate()?;
        limits.validate()?;
        if session_source != self.source || projection.source != self.source {
            return Err(state("relation load source generation"));
        }
        if timeout_ns == 0
            || key.len() > limits.bytes
            || self.key_codecs.len() > limits.parameters
            || self
                .key_codecs
                .len()
                .checked_mul(3)
                .is_none_or(|nodes| nodes > limits.nodes)
            || limits.depth < 2
        {
            return Err(invalid("relation load limits/deadline"));
        }
        if !model_row_matches(&self.child, &projection.projection)? {
            return Err(invalid("child Table.Row projection mismatch"));
        }
        let expected = self
            .relation
            .parent_key_expectation(self.source.profile, &self.key_codecs)?;
        Batch::parse(key, expected)?;
        Ok(RelationLoad {
            relation: Arc::clone(self),
            key: Arc::from(key),
            projection,
            timeout_ns,
        })
    }
}
/// Immutable admitted arguments, not a managed relationship plan or async host.
#[derive(Debug)]
pub struct RelationLoad {
    pub relation: Arc<RelationBinding>,
    pub key: Arc<[u8]>,
    pub projection: ProjectionBinding,
    pub timeout_ns: u64,
}
/// Finite source-local relation interning. Closing removes cache ownership only;
/// admitted loads/imports retain their own Arc references until their retirement.
pub struct RelationRegistry {
    source: SourceIdentity,
    max_entries: usize,
    max_bytes: usize,
    bytes: usize,
    closed: bool,
    entries: HashMap<[u8; 32], Arc<RelationBinding>>,
    capabilities: RelationCapabilities,
}
impl RelationRegistry {
    pub fn new(
        source: SourceIdentity,
        max_entries: usize,
        max_bytes: usize,
        capabilities: RelationCapabilities,
    ) -> Result<Self> {
        capabilities.validate()?;
        source.validate()?;
        if max_entries == 0 || max_entries > MAX_NODES || max_bytes == 0 || max_bytes > MAX_BYTES {
            return Err(invalid("relation registry bounds"));
        }
        Ok(Self {
            source,
            max_entries,
            max_bytes,
            bytes: 0,
            closed: false,
            entries: HashMap::new(),
            capabilities,
        })
    }
    pub fn register(
        &mut self,
        parent: ModelBinding,
        child: ModelBinding,
        definition: &[u8],
    ) -> Result<Arc<RelationBinding>> {
        if self.closed {
            return Err(state("relation source closed"));
        }
        let candidate =
            RelationBinding::register(self.source, parent, child, definition, self.capabilities)?;
        let hash = candidate.relation.hash();
        if let Some(existing) = self.entries.get(&hash) {
            if existing.relation.bytes() != candidate.relation.bytes() {
                return Err(invalid("relation identity hash collision"));
            }
            return Ok(Arc::clone(existing));
        }
        let bytes = candidate.relation.bytes().len();
        if self.entries.len() >= self.max_entries
            || bytes > self.max_bytes.saturating_sub(self.bytes)
        {
            return Err(capacity("relation registry budget"));
        }
        let relation = Arc::new(candidate);
        self.entries.insert(hash, Arc::clone(&relation));
        self.bytes += bytes;
        Ok(relation)
    }
    pub fn counts(&self) -> (usize, usize) {
        (self.entries.len(), self.bytes)
    }
    pub fn close(&mut self) {
        self.closed = true;
        self.entries.clear();
        self.bytes = 0;
    }
}
