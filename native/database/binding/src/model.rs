use crate::{BindingError, Result, invalid, state};
use nativeweave_data_contracts::{Codec, MAX_BYTES, MAX_NODES, Model, Profile, Projection};
use std::{collections::HashMap, sync::Arc};

/// Creating-engine/source generation identity supplied by B04, never an ABI pointer.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct SourceIdentity {
    pub engine: u64,
    pub source: u64,
    pub generation: u32,
    pub profile: Profile,
}
impl SourceIdentity {
    pub fn validate(self) -> Result<()> {
        if self.engine == 0 || self.source == 0 || self.generation == 0 {
            Err(invalid("zero source identity"))
        } else {
            Ok(())
        }
    }
}
/// Finite validation budgets, independent from SQLx/provider result budgets.
#[derive(Clone, Copy, Debug)]
pub struct Limits {
    pub nodes: usize,
    pub depth: u32,
    pub bytes: usize,
    pub parameters: usize,
}
impl Default for Limits {
    fn default() -> Self {
        Self {
            nodes: MAX_NODES,
            depth: 64,
            bytes: MAX_BYTES,
            parameters: MAX_NODES,
        }
    }
}
impl Limits {
    pub fn validate(self) -> Result<()> {
        if self.nodes == 0
            || self.nodes > MAX_NODES
            || self.depth == 0
            || self.depth > 256
            || self.bytes == 0
            || self.bytes > MAX_BYTES
            || self.parameters == 0
            || self.parameters > MAX_NODES
        {
            Err(invalid("invalid binding limits"))
        } else {
            Ok(())
        }
    }
}
/// Provider capabilities admitted by the engine profile; no name implies support.
#[derive(Clone, Copy, Debug)]
pub struct Capabilities {
    pub metadata: bool,
    pub scalar_codecs: bool,
    pub explicit_relationships: bool,
    pub recursive_cte: bool,
    pub set_operations: bool,
    pub query: bool,
    pub null_safe_equal: bool,
    pub windows: bool,
    pub cte: bool,
    pub row_lock: bool,
    pub prepared: bool,
}
impl Capabilities {
    /// Exact engine-local bits from pinned database.md section9.3. The host passes
    /// its admitted package/provider/version/profile intersection, not guessed support.
    pub fn from_mask(mask: u64, profile: Profile) -> Result<Self> {
        if mask >> 25 != 0 {
            return Err(invalid("reserved database capability bits"));
        }
        let has = |bit: u32| mask & (1u64 << bit) != 0u64;
        Ok(Self {
            metadata: has(0),
            query: has(1),
            scalar_codecs: has(23),
            explicit_relationships: has(17),
            recursive_cte: has(21),
            set_operations: has(22),
            null_safe_equal: has(12),
            windows: has(10),
            cte: has(9),
            row_lock: has(11) && profile == Profile::PostgreSql,
            prepared: has(18),
        })
    }
}
/// The source's existing codec registrations, copied as immutable binding identity.
/// This does not encode provider values or implement a second codec registry ABI.
#[derive(Clone, Debug)]
pub struct CodecBindings {
    profile: Profile,
    entries: HashMap<u32, Codec>,
}
impl CodecBindings {
    pub fn new(profile: Profile, configured: &[(u32, Codec)]) -> Result<Self> {
        if configured.len() > MAX_NODES {
            return Err(invalid("codec binding count"));
        }
        let mut entries = HashMap::new();
        for id in 1..=17 {
            if matches!(id, 13 | 15) || profile == Profile::PostgreSql && id == 14 {
                continue;
            }
            entries.insert(
                id,
                Codec {
                    id,
                    configuration: None,
                    nullable: false,
                },
            );
        }
        for (bound, codec) in configured {
            codec.validate(profile).map_err(BindingError::from)?;
            if *bound < 1024
                || codec.configuration.is_none()
                || codec.nullable
                || entries.contains_key(bound)
                || entries.values().any(|existing| existing.same_scalar(codec))
            {
                return Err(invalid("invalid configured codec binding"));
            }
            entries.insert(*bound, codec.clone());
        }
        Ok(Self { profile, entries })
    }
    pub fn get(&self, bound: u32, nullable: bool) -> Result<Codec> {
        let mut codec = self
            .entries
            .get(&bound)
            .cloned()
            .ok_or_else(|| invalid("unknown source codec"))?;
        codec.nullable = nullable;
        Ok(codec)
    }
    pub fn bound(&self, codec: &Codec) -> Result<u32> {
        codec.validate(self.profile).map_err(BindingError::from)?;
        self.entries
            .iter()
            .find_map(|(id, c)| c.same_scalar(codec).then_some(*id))
            .ok_or_else(|| invalid("unregistered codec identity"))
    }
}
/// A reference to B01's immutable canonical model plus source-bound codec IDs.
#[derive(Clone, Debug)]
pub struct ModelBinding {
    pub source: SourceIdentity,
    pub model: Arc<Model>,
    pub codecs: Arc<CodecBindings>,
}
impl ModelBinding {
    pub fn new(
        source: SourceIdentity,
        model: Arc<Model>,
        codecs: Arc<CodecBindings>,
    ) -> Result<Self> {
        source.validate()?;
        if source.profile != codecs.profile {
            return Err(invalid("source codec profile"));
        }
        for column in model.columns() {
            codecs.bound(&column.codec)?;
        }
        Ok(Self {
            source,
            model,
            codecs,
        })
    }
    pub fn ensure_source(&self, source: SourceIdentity) -> Result<()> {
        if self.source != source {
            Err(state("model belongs to another source generation"))
        } else {
            Ok(())
        }
    }
}
#[derive(Clone, Debug)]
pub struct ProjectionBinding {
    pub source: SourceIdentity,
    pub id: u32,
    pub projection: Arc<Projection>,
    pub codecs: Arc<CodecBindings>,
}
impl ProjectionBinding {
    pub fn new(
        source: SourceIdentity,
        id: u32,
        projection: Arc<Projection>,
        codecs: Arc<CodecBindings>,
    ) -> Result<Self> {
        source.validate()?;
        if source.profile != codecs.profile {
            return Err(invalid("source codec profile"));
        }
        if id == 0 {
            return Err(invalid("zero projection ID"));
        }
        for field in projection.fields() {
            codecs.bound(&field.codec)?;
        }
        Ok(Self {
            source,
            id,
            projection,
            codecs,
        })
    }
}
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ParameterIdentity {
    pub id: u32,
    pub name: String,
    pub codec: Codec,
    pub bound_codec: u32,
}
impl ParameterIdentity {
    pub fn validate(&self, codecs: &CodecBindings) -> Result<()> {
        if self.id == 0
            || self.name.is_empty()
            || self.name.len() > 128
            || !self
                .name
                .bytes()
                .enumerate()
                .all(|(i, c)| c == b'_' || c.is_ascii_alphabetic() || i > 0 && c.is_ascii_digit())
        {
            return Err(invalid("invalid prepared parameter identity"));
        }
        if codecs.get(self.bound_codec, self.codec.nullable)? != self.codec {
            return Err(invalid("parameter codec mismatch"));
        }
        Ok(())
    }
}

/// Source-generation parameter identities. B04 owns this finite registration
/// object; separate query arenas share it, including clone/import composition.
pub struct ParameterRegistry {
    pub source: SourceIdentity,
    max: usize,
    identities: std::sync::Mutex<HashMap<u32, ParameterIdentity>>,
}
impl ParameterRegistry {
    pub fn new(source: SourceIdentity, max: usize) -> Result<Self> {
        source.validate()?;
        if max == 0 || max > MAX_NODES {
            return Err(invalid("parameter registry bound"));
        }
        Ok(Self {
            source,
            max,
            identities: std::sync::Mutex::new(HashMap::new()),
        })
    }
    pub(crate) fn register(&self, value: &ParameterIdentity) -> Result<()> {
        let mut entries = self
            .identities
            .lock()
            .map_err(|_| state("parameter registry poisoned"))?;
        if let Some(old) = entries.get(&value.id) {
            return if old == value {
                Ok(())
            } else {
                Err(invalid(
                    "source parameter ID reused with different identity",
                ))
            };
        }
        if entries.len() == self.max {
            return Err(crate::capacity("source parameter count"));
        }
        entries.insert(value.id, value.clone());
        Ok(())
    }
}
