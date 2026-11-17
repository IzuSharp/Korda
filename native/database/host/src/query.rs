//! B04's creating-module ownership and admission around the B11 SeaQuery engine.
use crate::{
    Error, Result,
    abi::{self, Buffer, OwnerValue, Stored},
    runtime::Source,
};
use nativeweave_interop_abi::*;
use nativeweave_query_binding::{Capabilities, ParameterIdentity};
use nativeweave_query_native::{NativeCte, NativeQuery};
use std::{
    ffi::c_void,
    panic::{AssertUnwindSafe, catch_unwind},
    sync::{Arc, Mutex, TryLockError},
};

pub fn capability_mask() -> u64 {
    let mask = (1 << 0)
        | (1 << 1)
        | (1 << 2)
        | (1 << 3)
        | (1 << 4)
        | (1 << 9)
        | (1 << 10)
        | (1 << 12)
        | (1 << 13)
        | (1 << 16)
        | (1 << 17)
        | (1 << 18)
        | (1 << 20)
        | (1 << 21)
        | (1 << 22)
        | (1 << 23)
        | (1 << 24);
    mask | if cfg!(feature = "postgres") {
        (1 << 11) | (1 << 14)
    } else {
        0
    } | if cfg!(feature = "sqlite") { 1 << 15 } else { 0 }
}
fn with_query<T>(
    raw: *mut c_void,
    f: impl FnOnce(&Arc<Source>, &mut NativeQuery) -> Result<T>,
) -> Result<T> {
    let owner = abi::owner(raw)?;
    let OwnerValue::Query(source, query) = &*owner else {
        return Err(Error::state("query required"));
    };
    source.admit()?;
    let mut query = match query.try_lock() {
        Ok(v) => v,
        Err(TryLockError::WouldBlock) => {
            return Err(Error::new(
                NW_DOMAIN_ERROR,
                7,
                "concurrent native query call",
            ));
        }
        Err(TryLockError::Poisoned(_)) => {
            return Err(Error::new(NW_ENGINE_POISONED, 8, "query poisoned"));
        }
    };
    match catch_unwind(AssertUnwindSafe(|| f(source, &mut query))) {
        Ok(result) => result,
        Err(_) => {
            source
                .poisoned
                .store(true, std::sync::atomic::Ordering::Release);
            query.poison();
            Err(Error::new(
                NW_NATIVE_PANIC,
                8,
                "native query panic contained",
            ))
        }
    }
}
unsafe fn clear_token(out: *mut u64) -> Result<()> {
    if out.is_null() || !out.is_aligned() {
        return Err(Error::invalid("token output"));
    }
    // SAFETY: the caller owns an independent aligned writable token slot.
    unsafe {
        out.write(0);
    }
    Ok(())
}
/// # Safety
/// All owners are creating-module references and output/error slots follow the exact ABI.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_query_create_v1(
    session: *mut c_void,
    model: *mut c_void,
    table_id: u32,
    out: *mut *mut c_void,
    error: *mut NwErrorInfoV1,
) -> u32 {
    abi::run(error, || {
        // SAFETY: independent caller-owned output slot.
        unsafe {
            abi::clear(out)?;
        }
        let session = abi::owner(session)?;
        let model = abi::owner(model)?;
        let OwnerValue::Session(session) = &*session else {
            return Err(Error::state("session required"));
        };
        let OwnerValue::Model(source, model) = &*model else {
            return Err(Error::state("model required"));
        };
        session.source.admit()?;
        if session.closing.load(std::sync::atomic::Ordering::Acquire)
            || source.identity != session.source.identity
        {
            return Err(Error::state("closed or mixed query session"));
        }
        let query = NativeQuery::new(
            model.clone(),
            source.parameters.clone(),
            table_id,
            source.config.limits.binding(),
            Capabilities::from_mask(capability_mask(), source.config.profile)?,
            source
                .config
                .limits
                .command
                .as_nanos()
                .try_into()
                .map_err(|_| Error::capacity())?,
        )?;
        // SAFETY: one complete query owner transfers through the cleared slot.
        unsafe {
            out.write(abi::publish_owner(OwnerValue::Query(
                source.clone(),
                Box::new(Mutex::new(query)),
            ))?);
        }
        Ok(())
    })
}
/// # Safety
/// Token output and error records are valid; input references are borrowed for the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_expr_column_v1(
    raw: *mut c_void,
    alias: u64,
    column: u32,
    out: *mut u64,
    error: *mut NwErrorInfoV1,
) -> u32 {
    abi::run(error, || {
        // SAFETY: caller-owned token output.
        unsafe {
            clear_token(out)?;
        }
        let token = with_query(raw, |_, q| Ok(q.column(alias, column)?))?;
        // SAFETY: the cleared slot remains writable.
        unsafe {
            out.write(token);
        }
        Ok(())
    })
}
/// # Safety
/// The byte view is synchronously readable and the token output is independent.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_expr_value_v1(
    raw: *mut c_void,
    values: NwBytesViewV1,
    out: *mut u64,
    error: *mut NwErrorInfoV1,
) -> u32 {
    abi::run(error, || {
        // SAFETY: exact borrowed input and output range contract.
        unsafe {
            clear_token(out)?;
        }
        let token = with_query(raw, |_, q| {
            // SAFETY: B11 snapshots all admitted literal cells before return.
            Ok(q.value(unsafe { abi::view(values)? })?)
        })?;
        // SAFETY: initialized output remains valid.
        unsafe {
            out.write(token);
        }
        Ok(())
    })
}
/// # Safety
/// Name bytes are borrowed only for this synchronous registration and outputs are writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_expr_parameter_v1(
    raw: *mut c_void,
    id: u32,
    name: NwBytesViewV1,
    bound: u32,
    nullable: u32,
    out: *mut u64,
    error: *mut NwErrorInfoV1,
) -> u32 {
    abi::run(error, || {
        // SAFETY: writable token and bounded borrowed name.
        unsafe {
            clear_token(out)?;
        }
        if nullable > 1 {
            return Err(Error::invalid("parameter nullability"));
        }
        // SAFETY: name is copied before returning.
        let name = std::str::from_utf8(unsafe { abi::view(name)? })
            .map_err(|_| Error::invalid("parameter UTF8"))?
            .to_owned();
        let token = with_query(raw, |source, q| {
            let codec = source.codecs()?.get(bound, nullable == 1)?;
            Ok(q.parameter(ParameterIdentity {
                id,
                name,
                codec,
                bound_codec: bound,
            })?)
        })?;
        // SAFETY: one scalar token transfer.
        unsafe {
            out.write(token);
        }
        Ok(())
    })
}
/// # Safety
/// Typed expression tokens and output/error pointers obey the canonical call contract.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_expr_binary_v1(
    raw: *mut c_void,
    opcode: u32,
    left: u64,
    right: u64,
    out: *mut u64,
    error: *mut NwErrorInfoV1,
) -> u32 {
    abi::run(error, || {
        // SAFETY: writable output slot.
        unsafe {
            clear_token(out)?;
        }
        let token = with_query(raw, |_, q| Ok(q.binary(opcode, left, right)?))?;
        // SAFETY: output remains valid through call completion.
        unsafe {
            out.write(token);
        }
        Ok(())
    })
}
/// # Safety
/// Typed expression token and output/error pointers obey the canonical call contract.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_expr_unary_v1(
    raw: *mut c_void,
    opcode: u32,
    input: u64,
    out: *mut u64,
    error: *mut NwErrorInfoV1,
) -> u32 {
    abi::run(error, || {
        // SAFETY: writable output slot.
        unsafe {
            clear_token(out)?;
        }
        let token = with_query(raw, |_, q| Ok(q.unary(opcode, input)?))?;
        // SAFETY: output remains valid through call completion.
        unsafe {
            out.write(token);
        }
        Ok(())
    })
}
/// # Safety
/// List bytes are borrowed synchronously and outputs are independently writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_expr_list_v1(
    raw: *mut c_void,
    opcode: u32,
    input: u64,
    values: NwBytesViewV1,
    out: *mut u64,
    error: *mut NwErrorInfoV1,
) -> u32 {
    abi::run(error, || {
        // SAFETY: writable output slot.
        unsafe {
            clear_token(out)?;
        }
        let token = with_query(raw, |_, q| {
            // SAFETY: B11 copies admitted list bytes before returning.
            Ok(q.list(opcode, input, unsafe { abi::view(values)? })?)
        })?;
        // SAFETY: output remains valid through call completion.
        unsafe {
            out.write(token);
        }
        Ok(())
    })
}
/// # Safety
/// Imported dependencies are live typed owners from the same source generation.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_query_import_v1(
    raw: *mut c_void,
    dependency: *mut c_void,
    kind: u32,
    out: *mut u64,
    error: *mut NwErrorInfoV1,
) -> u32 {
    abi::run(error, || {
        // SAFETY: writable token output.
        unsafe {
            clear_token(out)?;
        }
        let dependency = abi::owner(dependency)?;
        let token = with_query(raw, |source, q| {
            if dependency.source().identity != source.identity {
                return Err(Error::state("mixed query dependency"));
            }
            Ok(match (kind, &*dependency) {
                (1, OwnerValue::Model(_, m)) => q.import_model(m.clone())?,
                (2, OwnerValue::Plan(_, p)) => q.import_plan(p.clone())?,
                (3, OwnerValue::Cte(_, c)) => q.import_cte(c.clone())?,
                _ => return Err(Error::state("query dependency kind")),
            })
        })?;
        // SAFETY: transfer after complete validated import.
        unsafe {
            out.write(token);
        }
        Ok(())
    })
}
/// # Safety
/// Exact kind6 clause bytes are readable only during the synchronous call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_query_clause_v1(
    raw: *mut c_void,
    opcode: u32,
    operands: NwBytesViewV1,
    out: *mut u64,
    error: *mut NwErrorInfoV1,
) -> u32 {
    abi::run(error, || {
        // SAFETY: writable output.
        unsafe {
            clear_token(out)?;
        }
        let token = with_query(raw, |_, q| {
            // SAFETY: B11 stages and copies the exact operands before committing.
            Ok(q.clause(opcode, unsafe { abi::view(operands)? })?)
        })?;
        // SAFETY: outputs transfer only after accepted clause commit.
        unsafe {
            out.write(token);
        }
        Ok(())
    })
}
/// # Safety
/// Query input and output/error pointers follow the canonical owner contract.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_query_seal_v1(
    raw: *mut c_void,
    terminal: u32,
    out: *mut *mut c_void,
    error: *mut NwErrorInfoV1,
) -> u32 {
    abi::run(error, || {
        // SAFETY: writable owner output.
        unsafe {
            abi::clear(out)?;
        }
        let value = with_query(raw, |s, q| {
            Ok(OwnerValue::Plan(s.clone(), q.seal(terminal)?))
        })?;
        // SAFETY: complete immutable plan transfer.
        unsafe {
            out.write(abi::publish_owner(value)?);
        }
        Ok(())
    })
}
/// # Safety
/// Query input and output/error pointers follow the canonical owner contract.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_query_clone_v1(
    raw: *mut c_void,
    out: *mut *mut c_void,
    error: *mut NwErrorInfoV1,
) -> u32 {
    abi::run(error, || {
        // SAFETY: writable owner output.
        unsafe {
            abi::clear(out)?;
        }
        let value = with_query(raw, |s, q| {
            Ok(OwnerValue::Query(
                s.clone(),
                Box::new(Mutex::new(q.clone_selected()?)),
            ))
        })?;
        // SAFETY: independent snapshot owner transfer.
        unsafe {
            out.write(abi::publish_owner(value)?);
        }
        Ok(())
    })
}
/// # Safety
/// Query and writable output/error pointers meet the canonical borrowed-call contract.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_query_render_v1(
    raw: *mut c_void,
    out: *mut *mut c_void,
    error: *mut NwErrorInfoV1,
) -> u32 {
    abi::run(error, || {
        // SAFETY: caller-owned output.
        unsafe {
            abi::clear(out)?;
        }
        let value = with_query(raw, |s, q| {
            let mut clone = q.clone_selected()?;
            let plan = clone.seal(8)?;
            Ok(Buffer::bytes(
                s.clone(),
                plan.render_shape()?.0.into_bytes(),
            ))
        })?;
        // SAFETY: immutable SQL diagnostic buffer; values remain placeholders.
        unsafe {
            out.write(abi::publish(Stored::Buffer(Arc::new(value)))?);
        }
        Ok(())
    })
}
/// # Safety
/// Declared model/body query and output/error pointers follow exact creating-source rules.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_cte_create_v1(
    declared: *mut c_void,
    table_id: u32,
    body: *mut c_void,
    recursive: u32,
    out: *mut *mut c_void,
    error: *mut NwErrorInfoV1,
) -> u32 {
    abi::run(error, || {
        // SAFETY: writable owner output.
        unsafe {
            abi::clear(out)?;
        }
        let declared = abi::owner(declared)?;
        let OwnerValue::Model(source, model) = &*declared else {
            return Err(Error::state("CTE declared model required"));
        };
        let cte = with_query(body, |s, q| {
            if source.identity != s.identity {
                return Err(Error::state("mixed CTE source"));
            }
            Ok(NativeCte::create(model.clone(), table_id, q, recursive)?)
        })?;
        // SAFETY: one immutable CTE snapshot transfer.
        unsafe {
            out.write(abi::publish_owner(OwnerValue::Cte(source.clone(), cte))?);
        }
        Ok(())
    })
}
