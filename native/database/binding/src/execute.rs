use crate::{control::quote, *};
use nativeweave_data_contracts::wire::{Batch, Descriptor, Expectation};
use sha2::{Digest, Sha256};
use std::sync::Arc;

impl PlanReference {
    /// Exact logical kind1 schema identity. Bound source IDs remain in descriptors;
    /// logical codec configuration remains in this canonical content hash.
    pub fn parameter_schema(&self) -> Vec<u8> {
        let entries = self
            .parameters
            .values()
            .map(|p| {
                format!(
                    "{{\"id\":{},\"name\":{},\"codec\":{},\"codec_config\":{},\"nullable\":{}}}",
                    p.id,
                    quote(&p.name),
                    p.codec.id,
                    p.codec.configuration.as_deref().unwrap_or("null"),
                    p.codec.nullable
                )
            })
            .collect::<Vec<_>>()
            .join(",");
        format!("{{\"version\":1,\"parameters\":[{entries}]}}").into_bytes()
    }
    pub fn parameter_expectation(&self) -> Expectation {
        Expectation {
            kind: 1,
            profile: self.source.profile,
            hash: Sha256::digest(self.parameter_schema()).into(),
            entity_id: 0,
            projection_id: 0,
            descriptors: self
                .parameters
                .values()
                .map(|p| Descriptor {
                    id: p.id,
                    codec: p.codec.clone(),
                    bound_codec: p.bound_codec,
                })
                .collect(),
            exact_rows: Some(1),
        }
    }
    /// Copies only after exact sealed IDs/name-derived hash/source/codec validation.
    /// This is the create-time argument snapshot, before provider execution.
    pub fn execution_arguments(
        self: &Arc<Self>,
        session: SourceIdentity,
        borrowed: &[u8],
        limits: Limits,
    ) -> Result<ExecutionArguments> {
        limits.validate()?;
        if self.source != session {
            return Err(state("plan/session source mismatch"));
        }
        if borrowed.len() > limits.bytes || self.parameters.len() > limits.parameters {
            return Err(capacity("execution parameter bound"));
        }
        Batch::parse(borrowed, self.parameter_expectation())?;
        Ok(ExecutionArguments {
            plan: Arc::clone(self),
            parameters: Arc::from(borrowed),
        })
    }
    /// Observable retained references for ownership tests; no native SQL tree is exposed.
    pub fn retained_counts(&self) -> (usize, usize) {
        (self.dependencies.len(), self.values.len())
    }
}
#[derive(Debug)]
pub struct ExecutionArguments {
    pub plan: Arc<PlanReference>,
    pub parameters: Arc<[u8]>,
}
