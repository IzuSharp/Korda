//! B14's exact canonical exports, composed inside the existing B04 Host.
use crate::{
    Error, Result,
    abi::{self, OwnerValue},
    operation::{DbOperation, Value},
    runtime::Source,
    sql::SqlPlan,
};
use futures_util::FutureExt;
use nativeweave_interop_abi::*;
use nativeweave_query_binding::{ModelBinding, RelationBinding, RelationCapabilities};
use std::{ffi::c_void, sync::Arc, time::Instant};

/// Exact kind15 fields for the failed current native batch. The managed bulk
/// adapter combines these with already acknowledged preceding batches.
#[derive(Clone, Debug)]
pub struct NativeBulkProgress {
    pub rows: u64,
    pub batches: u32,
    pub committed: bool,
    pub outcome: u32,
}
impl NativeBulkProgress {
    pub fn encode(&self) -> Vec<u8> {
        crate::control::encode(
            15,
            &[
                (1, 2, self.rows.to_le_bytes().to_vec()),
                (2, 1, self.batches.to_le_bytes().to_vec()),
                (3, 1, u32::from(self.committed).to_le_bytes().to_vec()),
                (4, 1, self.outcome.to_le_bytes().to_vec()),
            ],
        )
    }
}

fn binding_failure(error: nativeweave_query_binding::BindingError) -> Error {
    match error.status {
        NW_DOMAIN_ERROR => Error::new(NW_DOMAIN_ERROR, 6, error.detail),
        NW_NOT_SUPPORTED => Error::unsupported(),
        _ => Error::from(error),
    }
}
fn capabilities() -> RelationCapabilities {
    let mask = crate::query::capability_mask();
    RelationCapabilities {
        metadata: mask & (1 << 0) != 0,
        scalar_codecs: mask & (1 << 23) != 0,
        query: mask & (1 << 1) != 0,
        streaming: mask & (1 << 4) != 0,
        explicit_relationships: mask & (1 << 17) != 0,
    }
}
impl Source {
    pub fn register_relation(
        &self,
        parent: ModelBinding,
        child: ModelBinding,
        bytes: &[u8],
    ) -> Result<Arc<RelationBinding>> {
        self.admit()?;
        let candidate =
            RelationBinding::register(self.identity, parent, child, bytes, capabilities())
                .map_err(binding_failure)?;
        let mut registry = self.registry.lock().unwrap_or_else(|e| e.into_inner());
        self.admit()?;
        let hash = candidate.relation.hash();
        if let Some(existing) = registry.relations.get(&hash) {
            if existing.relation.bytes() != candidate.relation.bytes() {
                return Err(Error::invalid("relation hash collision"));
            }
            return Ok(existing.clone());
        }
        if registry.configured.len()
            + registry.models.len()
            + registry.projections.len()
            + registry.relations.len()
            >= self.config.limits.nodes
            || bytes.len()
                > self
                    .config
                    .limits
                    .parameter_bytes
                    .saturating_sub(registry.bytes)
        {
            return Err(Error::capacity());
        }
        let relation = Arc::new(candidate);
        registry.bytes += bytes.len();
        registry.relations.insert(hash, relation.clone());
        Ok(relation)
    }
}

/// # Safety
/// Inputs are live creating-module owners and synchronous borrowed bytes; outputs
/// and error storage are independently writable according to the pinned ABI.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_relation_register_v1(
    source: *mut c_void,
    parent: *mut c_void,
    child: *mut c_void,
    definition: NwBytesViewV1,
    out: *mut *mut c_void,
    error: *mut NwErrorInfoV1,
) -> u32 {
    abi::run(error, || {
        // SAFETY: canonical independent output slot and borrowed definition.
        unsafe {
            abi::clear(out)?;
        }
        let source_owner = abi::owner(source)?;
        let parent_owner = abi::owner(parent)?;
        let child_owner = abi::owner(child)?;
        let OwnerValue::Source(source) = &*source_owner else {
            return Err(Error::state("source required"));
        };
        let OwnerValue::Model(parent_source, parent) = &*parent_owner else {
            return Err(Error::state("parent model required"));
        };
        let OwnerValue::Model(child_source, child) = &*child_owner else {
            return Err(Error::state("child model required"));
        };
        if source.identity != parent_source.identity || source.identity != child_source.identity {
            return Err(Error::new(NW_INVALID_STATE, 13, "mixed relation source"));
        }
        // SAFETY: caller's borrowed view is copied before publication.
        let relation = source.register_relation(parent.clone(), child.clone(), unsafe {
            abi::view(definition)?
        })?;
        // SAFETY: successful ownership transfer through a cleared output.
        unsafe {
            out.write(abi::publish_owner(OwnerValue::Relation(
                source.clone(),
                relation,
            ))?);
        }
        Ok(())
    })
}

/// # Safety
/// Live module-local owners and independent writable ABI outputs are required.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_relation_load_create_v1(
    session: *mut c_void,
    relation: *mut c_void,
    key: NwBytesViewV1,
    projection_id: u32,
    out: *mut *mut c_void,
    error: *mut NwErrorInfoV1,
) -> u32 {
    abi::run(error, || {
        // SAFETY: canonical output slot.
        unsafe {
            abi::clear(out)?;
        }
        let session_owner = abi::owner(session)?;
        let relation_owner = abi::owner(relation)?;
        let OwnerValue::Session(session) = &*session_owner else {
            return Err(Error::state("session required"));
        };
        let OwnerValue::Relation(source, relation) = &*relation_owner else {
            return Err(Error::state("relation required"));
        };
        if source.identity != session.source.identity {
            return Err(Error::new(
                NW_INVALID_STATE,
                13,
                "mixed relation load source",
            ));
        }
        let limits = source.config.limits.binding();
        let timeout = source.config.limits.command;
        let admitted = Instant::now();
        if timeout.is_zero() {
            return Err(Error::new(NW_TIMEOUT, 8, "relation deadline expired"));
        }
        // SAFETY: the parent-key view remains borrowed only during admission.
        let key_bytes = unsafe { abi::view(key)? };
        let arity = relation.parent.model.keys().len();
        if key_bytes.len() > limits.bytes
            || arity > limits.parameters
            || arity.saturating_mul(3) > limits.nodes
            || limits.depth < 2
        {
            return Err(Error::capacity());
        }
        // RelationBinding validates and copies every borrowed key cell.
        let load = relation
            .load(
                source.identity,
                key_bytes,
                source.projection(projection_id)?,
                limits,
                u64::try_from(timeout.as_nanos()).map_err(|_| Error::capacity())?,
            )
            .map_err(binding_failure)?;
        let rendered =
            nativeweave_data_relations::compile_relation(&load, limits).map_err(binding_failure)?;
        let mut plan = SqlPlan::from_rendered(source.clone(), rendered)?;
        let gate = session.admit(false)?;
        let session = session.clone();
        let operation = DbOperation::create(
            source.clone(),
            2,
            false,
            Box::new(move |cancel| {
                async move {
                    // Retain both exact model/codec roots until execution returns its
                    // independently retained cursor, even if source cache roots close.
                    let _load = load;
                    plan.timeout = timeout.saturating_sub(admitted.elapsed());
                    session.execute(Arc::new(plan), cancel, gate).await
                }
                .boxed()
            }),
        )?;
        // SAFETY: successful transfer of a Created operation, no SQL has run.
        unsafe {
            out.write(abi::publish_operation(operation)?);
        }
        Ok(())
    })
}

/// # Safety
/// The mutation record is a synchronous borrowed canonical control record; all
/// owner and output preconditions are the pinned database ABI preconditions.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_mutation_create_v1(
    session: *mut c_void,
    model: *mut c_void,
    kind: u32,
    payload: NwBytesViewV1,
    out: *mut *mut c_void,
    error: *mut NwErrorInfoV1,
) -> u32 {
    abi::run(error, || {
        // SAFETY: canonical output slot and borrowed mutation record.
        unsafe {
            abi::clear(out)?;
        }
        let session_owner = abi::owner(session)?;
        let model_owner = abi::owner(model)?;
        let OwnerValue::Session(session) = &*session_owner else {
            return Err(Error::state("session required"));
        };
        let OwnerValue::Model(source, model) = &*model_owner else {
            return Err(Error::state("model required"));
        };
        if source.identity != session.source.identity {
            return Err(Error::new(NW_INVALID_STATE, 13, "mixed mutation source"));
        }
        source.admit()?;
        // SAFETY: compiler copies every cell and validates exact nested identities.
        let payload = unsafe { abi::view(payload)? };
        let projection = if kind == 1 {
            let fields = crate::control::Fields::parse(
                payload,
                4,
                &[(1, 1, true), (2, 5, true), (7, 1, true)],
            )?;
            Some(source.projection(fields.u32(7)?)?)
        } else {
            None
        };
        let timeout = source.config.limits.command;
        let admitted = Instant::now();
        let mutation = nativeweave_data_relations::compile_mutation(
            model,
            kind,
            payload,
            projection,
            source.config.limits.binding(),
            u64::try_from(timeout.as_nanos()).map_err(|_| Error::capacity())?,
        )
        .map_err(binding_failure)?;
        if mutation.input_rows > source.config.limits.batch_rows {
            return Err(Error::capacity());
        }
        let mut plan = SqlPlan::from_rendered(source.clone(), mutation.command)?;
        plan.kind = 1;
        let gate = session.admit(false)?;
        let session = session.clone();
        let operation = DbOperation::create(
            source.clone(),
            if kind == 1 { 2 } else { 100 },
            false,
            Box::new(move |cancel| {
                async move {
                    plan.timeout = timeout.saturating_sub(admitted.elapsed());
                    let value = session
                        .execute(Arc::new(plan), cancel, gate)
                        .await
                        .map_err(|mut failure| {
                            if kind == 0 {
                                let outcome = session
                                    .transaction
                                    .lock()
                                    .unwrap_or_else(|e| e.into_inner())
                                    .as_ref()
                                    .and_then(std::sync::Weak::upgrade)
                                    .map_or(0, |transaction| transaction.snapshot().outcome as u32);
                                failure.bulk = Some(NativeBulkProgress {
                                    rows: 0,
                                    batches: 0,
                                    committed: false,
                                    outcome,
                                });
                            }
                            failure
                        })?;
                    if let Value::Count(rows) = &value {
                        // A declared primary key matching >1 row is an actual schema
                        // constraint failure; code16 denotes the precise 1/0 CAS mismatch.
                        if mutation.key_bound && *rows > 1 {
                            return Err(Error::new(
                                NW_DOMAIN_ERROR,
                                2,
                                "declared key matched multiple rows",
                            ));
                        }
                        if mutation.checked && *rows == 0 {
                            return Err(Error::new(
                                NW_DOMAIN_ERROR,
                                16,
                                "optimistic concurrency conflict",
                            ));
                        }
                    }
                    Ok(value)
                }
                .boxed()
            }),
        )?;
        // SAFETY: successful transfer of the independently retained operation.
        unsafe {
            out.write(abi::publish_operation(operation)?);
        }
        Ok(())
    })
}
