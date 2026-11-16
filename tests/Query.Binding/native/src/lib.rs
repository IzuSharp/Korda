//! Test-only exact ABI adapter around the production B09 validators. There is no
//! SQL executor, product source host, runtime or distributed native asset here.
#![deny(unsafe_op_in_unsafe_fn)]
use nativeweave_data_contracts::{
    Model, Profile,
    wire::{Batch, Descriptor, Expectation, control_fields},
};
use nativeweave_interop_abi::*;
use nativeweave_query_binding::{signatures::*, *};
use std::{
    collections::HashMap,
    sync::{
        Arc, Mutex,
        atomic::{AtomicU64, AtomicUsize, Ordering},
    },
};
static SOURCES: AtomicU64 = AtomicU64::new(1);
static OWNERS: AtomicUsize = AtomicUsize::new(0);
static OPERATIONS: AtomicUsize = AtomicUsize::new(0);
static BUFFERS: AtomicUsize = AtomicUsize::new(0);
struct Source {
    identity: SourceIdentity,
    codecs: Arc<CodecBindings>,
    parameters: Arc<ParameterRegistry>,
    projections: Mutex<HashMap<u32, ProjectionBinding>>,
    relations: Mutex<RelationRegistry>,
}
struct Cursor {
    projection: ProjectionBinding,
    retained: Vec<u8>,
    _relation: Option<RelationLoad>,
    _execution: Option<ExecutionArguments>,
    _session: Arc<Source>,
}
enum Value {
    Source(Arc<Source>),
    Session(Arc<Source>),
    Model(ModelBinding),
    Query(Box<Mutex<QueryArena>>),
    Plan(Arc<PlanReference>),
    Relation(Arc<RelationBinding>),
    Cte(Arc<CteReference>),
    Cursor(Cursor),
    Operation(Mutex<Operation>),
}
struct Owner {
    value: Value,
}
enum ResultValue {
    Owner(Box<Owner>, u32),
    Buffer(Box<Buffer>),
    Empty,
}
struct Operation {
    started: bool,
    retired: bool,
    taken: bool,
    canceled: bool,
    terminal_canceled: bool,
    callback_active: bool,
    result: Option<ResultValue>,
    retained: Vec<u8>,
}
struct Buffer {
    bytes: Vec<u8>,
}
impl Drop for Owner {
    fn drop(&mut self) {
        if matches!(self.value, Value::Operation(_)) {
            OPERATIONS.fetch_sub(1, Ordering::Relaxed);
        }
        OWNERS.fetch_sub(1, Ordering::Relaxed);
    }
}
impl Drop for Buffer {
    fn drop(&mut self) {
        BUFFERS.fetch_sub(1, Ordering::Relaxed);
    }
}
fn owned(value: Value) -> Box<Owner> {
    OWNERS.fetch_add(1, Ordering::Relaxed);
    if matches!(value, Value::Operation(_)) {
        OPERATIONS.fetch_add(1, Ordering::Relaxed);
    }
    Box::new(Owner { value })
}
fn buffer(bytes: Vec<u8>) -> Box<Buffer> {
    BUFFERS.fetch_add(1, Ordering::Relaxed);
    Box::new(Buffer { bytes })
}
fn operation(result: ResultValue, retained: Vec<u8>) -> Box<Owner> {
    owned(Value::Operation(Mutex::new(Operation {
        started: false,
        retired: false,
        taken: false,
        canceled: false,
        terminal_canceled: false,
        callback_active: false,
        result: Some(result),
        retained,
    })))
}
fn bad() -> BindingError {
    BindingError {
        status: NW_INVALID_ARGUMENT,
        detail: "fixture argument",
    }
}
fn wrong() -> BindingError {
    BindingError {
        status: NW_INVALID_STATE,
        detail: "fixture owner/state",
    }
}
unsafe fn reference<'a>(pointer: *mut DbOwner) -> Result<&'a Owner> {
    if pointer.is_null() {
        return Err(bad());
    } // SAFETY: every nonnull owner input is an active pointer produced by this fixture, per the ABI precondition.
    Ok(unsafe { &*pointer.cast::<Owner>() })
}
unsafe fn view<'a>(view: NwBytesViewV1) -> Result<&'a [u8]> {
    if view.length > nativeweave_data_contracts::MAX_BYTES as u64
        || view.length > isize::MAX as u64
        || view.length != 0 && view.data.is_null()
    {
        return Err(bad());
    }
    if view.length == 0 {
        return Ok(&[]);
    } // SAFETY: caller lends the stated readable extent until this synchronous call returns; finite size checked above.
    Ok(unsafe { std::slice::from_raw_parts(view.data, view.length as usize) })
}
unsafe fn output<T: Default>(pointer: *mut T) -> Result<()> {
    if pointer.is_null() {
        return Err(bad());
    } // SAFETY: the caller supplies a writable aligned output; it is zeroed before other validation.
    unsafe { pointer.write(T::default()) };
    Ok(())
}
unsafe fn finish(error: *mut NwErrorInfoV1, action: impl FnOnce() -> Result<()>) -> NwStatus {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(action));
    let status = match result {
        Ok(Ok(())) => NW_OK,
        Ok(Err(e)) => e.status,
        Err(_) => NW_NATIVE_PANIC,
    };
    if !error.is_null() {
        // SAFETY: optional diagnostic is a writable full canonical A01 record supplied by the test adapter.
        unsafe {
            error.write(NwErrorInfoV1 {
                struct_size: std::mem::size_of::<NwErrorInfoV1>() as u32,
                domain: if status == NW_OK { 0 } else { 9 },
                code: status as i32,
                flags: 0,
                correlation_id: if status == NW_OK { 0 } else { 109 },
            })
        };
    }
    status
}
unsafe fn source(pointer: *mut DbOwner, session: bool) -> Result<Arc<Source>> {
    // SAFETY: forwards the same active-owner precondition to reference.
    match &unsafe { reference(pointer)? }.value {
        Value::Source(source) if !session => Ok(Arc::clone(source)),
        Value::Session(source) if session => Ok(Arc::clone(source)),
        _ => Err(wrong()),
    }
}
unsafe fn model(pointer: *mut DbOwner) -> Result<ModelBinding> {
    // SAFETY: active fixture owner precondition.
    match &unsafe { reference(pointer)? }.value {
        Value::Model(model) => Ok(model.clone()),
        _ => Err(wrong()),
    }
}
unsafe fn query<R>(
    pointer: *mut DbOwner,
    action: impl FnOnce(&mut QueryArena) -> Result<R>,
) -> Result<R> {
    // SAFETY: active fixture owner precondition; mutex independently enforces semantic single-writer admission.
    let Value::Query(query) = &unsafe { reference(pointer)? }.value else {
        return Err(wrong());
    };
    let mut guard = query.try_lock().map_err(|_| wrong())?;
    action(&mut guard)
}
/// Exact test-only entry forwarding to the production binding validator.
/// # Safety
/// Inputs must be active creating-fixture owners and valid borrowed extents; outputs writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_source_create_v1(
    config: NwBytesViewV1,
    out: *mut *mut DbOwner,
    error: *mut NwErrorInfoV1,
) -> NwStatus {
    // SAFETY: exact ABI entry validates kinds, extents and zeroes outputs before semantic admission.
    unsafe {
        finish(error, || {
            output(out)?;
            let data = view(config)?;
            let profile = match data {
                [1] => Profile::Sqlite,
                [2] => Profile::PostgreSql,
                _ => return Err(bad()),
            };
            let identity = SourceIdentity {
                engine: 0xb09,
                source: SOURCES.fetch_add(1, Ordering::Relaxed),
                generation: 1,
                profile,
            };
            let source = Arc::new(Source {
                identity,
                codecs: Arc::new(CodecBindings::new(profile, &[])?),
                parameters: Arc::new(ParameterRegistry::new(identity, 4096)?),
                projections: Mutex::new(HashMap::new()),
                relations: Mutex::new(RelationRegistry::new(
                    identity,
                    4096,
                    4 * 1024 * 1024,
                    relation_caps(),
                )?),
            });
            *out = Box::into_raw(owned(Value::Source(source))).cast();
            Ok(())
        })
    }
}
// Fixture-only synchronous session adoption; source_create above is deliberately not a production config implementation.
/// Exact test-only entry forwarding to the production binding validator.
/// # Safety
/// Inputs must be active creating-fixture owners and valid borrowed extents; outputs writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_test_session_v1(
    source_ref: *mut DbOwner,
    out: *mut *mut DbOwner,
    error: *mut NwErrorInfoV1,
) -> NwStatus {
    // SAFETY: exact ABI entry validates kinds, extents and zeroes outputs before semantic admission.
    unsafe {
        finish(error, || {
            output(out)?;
            *out = Box::into_raw(owned(Value::Session(source(source_ref, false)?))).cast();
            Ok(())
        })
    }
}
/// Exact test-only entry forwarding to the production binding validator.
/// # Safety
/// Inputs must be active creating-fixture owners and valid borrowed extents; outputs writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_metadata_register_v1(
    source_ref: *mut DbOwner,
    metadata: NwBytesViewV1,
    out: *mut *mut DbOwner,
    error: *mut NwErrorInfoV1,
) -> NwStatus {
    // SAFETY: exact ABI entry validates kinds, extents and zeroes outputs before semantic admission.
    unsafe {
        finish(error, || {
            output(out)?;
            let source = source(source_ref, false)?;
            let fields = control_fields(view(metadata)?, 2, &[(1, 5), (2, 5)])?;
            let model = Arc::new(Model::parse(fields[0], source.identity.profile)?);
            if fields[1] != model.hash() {
                return Err(bad());
            }
            *out = Box::into_raw(owned(Value::Model(ModelBinding::new(
                source.identity,
                model,
                Arc::clone(&source.codecs),
            )?)))
            .cast();
            Ok(())
        })
    }
}
/// Exact test-only entry forwarding to the production binding validator.
/// # Safety
/// Inputs must be active creating-fixture owners and valid borrowed extents; outputs writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_projection_register_v1(
    source_ref: *mut DbOwner,
    bytes: NwBytesViewV1,
    out: *mut u32,
    error: *mut NwErrorInfoV1,
) -> NwStatus {
    // SAFETY: exact ABI entry validates kinds, extents and zeroes outputs before semantic admission.
    unsafe {
        finish(error, || {
            output(out)?;
            let source = source(source_ref, false)?;
            let mut entries = source.projections.lock().map_err(|_| wrong())?;
            let id = entries.len() as u32 + 1;
            let projection = projection_record(
                view(bytes)?,
                source.identity,
                id,
                Arc::clone(&source.codecs),
                None,
            )?;
            entries.insert(id, projection);
            *out = id;
            Ok(())
        })
    }
}
/// Exact test-only entry forwarding to the production binding validator.
/// # Safety
/// Inputs must be active creating-fixture owners and valid borrowed extents; outputs writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_relation_register_v1(
    source_ref: *mut DbOwner,
    parent: *mut DbOwner,
    child: *mut DbOwner,
    definition: NwBytesViewV1,
    out: *mut *mut DbOwner,
    error: *mut NwErrorInfoV1,
) -> NwStatus {
    // SAFETY: exact ABI entry validates kinds, extents and zeroes outputs before semantic admission.
    unsafe {
        finish(error, || {
            output(out)?;
            let source = source(source_ref, false)?;
            let relation = source.relations.lock().map_err(|_| wrong())?.register(
                model(parent)?,
                model(child)?,
                view(definition)?,
            )?;
            *out = Box::into_raw(owned(Value::Relation(relation))).cast();
            Ok(())
        })
    }
}
/// Exact test-only entry forwarding to the production binding validator.
/// # Safety
/// Inputs must be active creating-fixture owners and valid borrowed extents; outputs writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_query_create_v1(
    session: *mut DbOwner,
    model_ref: *mut DbOwner,
    table_id: u32,
    out: *mut *mut DbOwner,
    error: *mut NwErrorInfoV1,
) -> NwStatus {
    // SAFETY: exact ABI entry validates kinds, extents and zeroes outputs before semantic admission.
    unsafe {
        finish(error, || {
            output(out)?;
            let source = source(session, true)?;
            let root = model(model_ref)?;
            root.ensure_source(source.identity)?;
            let query = QueryArena::new(
                root,
                Arc::clone(&source.parameters),
                table_id,
                Limits::default(),
                full_caps(source.identity.profile),
                1_000_000_000,
            )?;
            *out = Box::into_raw(owned(Value::Query(Box::new(Mutex::new(query))))).cast();
            Ok(())
        })
    }
}
/// Exact test-only entry forwarding to the production binding validator.
/// # Safety
/// Inputs must be active creating-fixture owners and valid borrowed extents; outputs writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_expr_column_v1(
    owner: *mut DbOwner,
    alias: u64,
    column: u32,
    out: *mut u64,
    error: *mut NwErrorInfoV1,
) -> NwStatus {
    // SAFETY: exact ABI entry validates kinds, extents and zeroes outputs before semantic admission.
    unsafe {
        finish(error, || {
            output(out)?;
            *out = query(owner, |q| q.column(alias, column))?;
            Ok(())
        })
    }
}
/// Exact test-only entry forwarding to the production binding validator.
/// # Safety
/// Inputs must be active creating-fixture owners and valid borrowed extents; outputs writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_expr_value_v1(
    owner: *mut DbOwner,
    bytes: NwBytesViewV1,
    out: *mut u64,
    error: *mut NwErrorInfoV1,
) -> NwStatus {
    // SAFETY: exact ABI entry validates kinds, extents and zeroes outputs before semantic admission.
    unsafe {
        finish(error, || {
            output(out)?;
            *out = query(owner, |q| q.value(view(bytes)?))?;
            Ok(())
        })
    }
}
/// Exact test-only entry forwarding to the production binding validator.
/// # Safety
/// Inputs must be active creating-fixture owners and valid borrowed extents; outputs writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_expr_parameter_v1(
    owner: *mut DbOwner,
    id: u32,
    name: NwBytesViewV1,
    codec: u32,
    nullable: u32,
    out: *mut u64,
    error: *mut NwErrorInfoV1,
) -> NwStatus {
    // SAFETY: exact ABI entry validates kinds, extents and zeroes outputs before semantic admission.
    unsafe {
        finish(error, || {
            output(out)?;
            if nullable > 1 {
                return Err(bad());
            }
            let name = std::str::from_utf8(view(name)?)
                .map_err(|_| bad())?
                .to_owned();
            *out = query(owner, |q| {
                let profile = q.source().profile;
                let bindings = CodecBindings::new(profile, &[])?;
                q.parameter(ParameterIdentity {
                    id,
                    name,
                    codec: bindings.get(codec, nullable == 1)?,
                    bound_codec: codec,
                })
            })?;
            Ok(())
        })
    }
}
/// Exact test-only entry forwarding to the production binding validator.
/// # Safety
/// Inputs must be active creating-fixture owners and valid borrowed extents; outputs writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_expr_binary_v1(
    owner: *mut DbOwner,
    op: u32,
    left: u64,
    right: u64,
    out: *mut u64,
    error: *mut NwErrorInfoV1,
) -> NwStatus {
    // SAFETY: exact ABI entry validates kinds, extents and zeroes outputs before semantic admission.
    unsafe {
        finish(error, || {
            output(out)?;
            *out = query(owner, |q| q.binary(op, left, right))?;
            Ok(())
        })
    }
}
/// Exact test-only entry forwarding to the production binding validator.
/// # Safety
/// Inputs must be active creating-fixture owners and valid borrowed extents; outputs writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_expr_unary_v1(
    owner: *mut DbOwner,
    op: u32,
    input: u64,
    out: *mut u64,
    error: *mut NwErrorInfoV1,
) -> NwStatus {
    // SAFETY: exact ABI entry validates kinds, extents and zeroes outputs before semantic admission.
    unsafe {
        finish(error, || {
            output(out)?;
            *out = query(owner, |q| q.unary(op, input))?;
            Ok(())
        })
    }
}
/// Exact test-only entry forwarding to the production binding validator.
/// # Safety
/// Inputs must be active creating-fixture owners and valid borrowed extents; outputs writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_expr_list_v1(
    owner: *mut DbOwner,
    op: u32,
    input: u64,
    bytes: NwBytesViewV1,
    out: *mut u64,
    error: *mut NwErrorInfoV1,
) -> NwStatus {
    // SAFETY: exact ABI entry validates kinds, extents and zeroes outputs before semantic admission.
    unsafe {
        finish(error, || {
            output(out)?;
            *out = query(owner, |q| q.list(op, input, view(bytes)?))?;
            Ok(())
        })
    }
}
/// Exact test-only entry forwarding to the production binding validator.
/// # Safety
/// Inputs must be active creating-fixture owners and valid borrowed extents; outputs writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_query_import_v1(
    owner: *mut DbOwner,
    dependency: *mut DbOwner,
    kind: u32,
    out: *mut u64,
    error: *mut NwErrorInfoV1,
) -> NwStatus {
    // SAFETY: exact ABI entry validates kinds, extents and zeroes outputs before semantic admission.
    unsafe {
        finish(error, || {
            output(out)?;
            let dependency = reference(dependency)?;
            *out = query(owner, |q| match (&dependency.value, kind) {
                (Value::Model(model), 1) => q.import_model(model.clone()),
                (Value::Plan(plan), 2) => q.import_plan(Arc::clone(plan)),
                (Value::Cte(cte), 3) => q.import_cte(Arc::clone(cte)),
                _ => Err(wrong()),
            })?;
            Ok(())
        })
    }
}
/// Exact test-only entry forwarding to the production binding validator.
/// # Safety
/// Inputs must be active creating-fixture owners and valid borrowed extents; outputs writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_query_clause_v1(
    owner: *mut DbOwner,
    op: u32,
    bytes: NwBytesViewV1,
    out: *mut u64,
    error: *mut NwErrorInfoV1,
) -> NwStatus {
    // SAFETY: exact ABI entry validates kinds, extents and zeroes outputs before semantic admission.
    unsafe {
        finish(error, || {
            output(out)?;
            *out = query(owner, |q| q.clause(op, view(bytes)?))?;
            Ok(())
        })
    }
}
/// Exact test-only entry forwarding to the production binding validator.
/// # Safety
/// Inputs must be active creating-fixture owners and valid borrowed extents; outputs writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_query_seal_v1(
    owner: *mut DbOwner,
    terminal: u32,
    out: *mut *mut DbOwner,
    error: *mut NwErrorInfoV1,
) -> NwStatus {
    // SAFETY: exact ABI entry validates kinds, extents and zeroes outputs before semantic admission.
    unsafe {
        finish(error, || {
            output(out)?;
            let plan = query(owner, |q| q.seal(terminal))?;
            *out = Box::into_raw(owned(Value::Plan(plan))).cast();
            Ok(())
        })
    }
}
/// Exact test-only entry forwarding to the production binding validator.
/// # Safety
/// Inputs must be active creating-fixture owners and valid borrowed extents; outputs writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_query_clone_v1(
    owner: *mut DbOwner,
    out: *mut *mut DbOwner,
    error: *mut NwErrorInfoV1,
) -> NwStatus {
    // SAFETY: exact ABI entry validates kinds, extents and zeroes outputs before semantic admission.
    unsafe {
        finish(error, || {
            output(out)?;
            let clone = query(owner, |q| q.clone_selected())?;
            *out = Box::into_raw(owned(Value::Query(Box::new(Mutex::new(clone))))).cast();
            Ok(())
        })
    }
}
/// Exact test-only entry forwarding to the production binding validator.
/// # Safety
/// Inputs must be active creating-fixture owners and valid borrowed extents; outputs writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_cte_create_v1(
    declared: *mut DbOwner,
    table_id: u32,
    body: *mut DbOwner,
    recursive: u32,
    out: *mut *mut DbOwner,
    error: *mut NwErrorInfoV1,
) -> NwStatus {
    // SAFETY: exact ABI entry validates kinds, extents and zeroes outputs before semantic admission.
    unsafe {
        finish(error, || {
            output(out)?;
            let cte = query(body, |q| {
                CteReference::create(model(declared)?, table_id, q, recursive)
            })?;
            *out = Box::into_raw(owned(Value::Cte(cte))).cast();
            Ok(())
        })
    }
}
/// Exact test-only entry forwarding to the production binding validator.
/// # Safety
/// Inputs must be active creating-fixture owners and valid borrowed extents; outputs writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_execute_create_v1(
    session: *mut DbOwner,
    plan: *mut DbOwner,
    bytes: NwBytesViewV1,
    out: *mut *mut DbOwner,
    error: *mut NwErrorInfoV1,
) -> NwStatus {
    // SAFETY: exact ABI entry validates kinds, extents and zeroes outputs before semantic admission.
    unsafe {
        finish(error, || {
            output(out)?;
            let source = source(session, true)?;
            let Value::Plan(plan) = &reference(plan)?.value else {
                return Err(wrong());
            };
            let arguments =
                plan.execution_arguments(source.identity, view(bytes)?, Limits::default())?;
            let projection = plan.projection.clone().ok_or_else(bad)?;
            let retained = arguments.parameters.to_vec();
            let cursor = owned(Value::Cursor(Cursor {
                projection,
                retained: retained.clone(),
                _relation: None,
                _execution: Some(arguments),
                _session: Arc::clone(&source),
            }));
            *out = Box::into_raw(operation(ResultValue::Owner(cursor, 2), retained)).cast();
            Ok(())
        })
    }
}
/// Exact test-only entry forwarding to the production binding validator.
/// # Safety
/// Inputs must be active creating-fixture owners and valid borrowed extents; outputs writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_relation_load_create_v1(
    session: *mut DbOwner,
    relation: *mut DbOwner,
    key: NwBytesViewV1,
    projection_id: u32,
    out: *mut *mut DbOwner,
    error: *mut NwErrorInfoV1,
) -> NwStatus {
    // SAFETY: exact ABI entry validates kinds, extents and zeroes outputs before semantic admission.
    unsafe {
        finish(error, || {
            output(out)?;
            let source = source(session, true)?;
            let Value::Relation(relation) = &reference(relation)?.value else {
                return Err(wrong());
            };
            let projection = source
                .projections
                .lock()
                .map_err(|_| wrong())?
                .get(&projection_id)
                .cloned()
                .ok_or_else(bad)?;
            let arguments = relation.load(
                source.identity,
                view(key)?,
                projection,
                Limits::default(),
                1_000_000_000,
            )?;
            let retained = arguments.key.to_vec();
            let cursor = owned(Value::Cursor(Cursor {
                projection: arguments.projection.clone(),
                retained: retained.clone(),
                _relation: Some(arguments),
                _execution: None,
                _session: Arc::clone(&source),
            }));
            *out = Box::into_raw(operation(ResultValue::Owner(cursor, 2), retained)).cast();
            Ok(())
        })
    }
}
/// Exact test-only entry forwarding to the production binding validator.
/// # Safety
/// Inputs must be active creating-fixture owners and valid borrowed extents; outputs writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_cursor_fetch_create_v1(
    cursor: *mut DbOwner,
    max_rows: u32,
    max_bytes: u64,
    out: *mut *mut DbOwner,
    error: *mut NwErrorInfoV1,
) -> NwStatus {
    // SAFETY: exact ABI entry validates kinds, extents and zeroes outputs before semantic admission.
    unsafe {
        finish(error, || {
            output(out)?;
            if max_rows == 0 || max_bytes == 0 || max_bytes > 4 * 1024 * 1024 {
                return Err(bad());
            }
            let Value::Cursor(cursor) = &reference(cursor)?.value else {
                return Err(wrong());
            };
            let projection = &cursor.projection;
            let descriptors = projection
                .projection
                .fields()
                .iter()
                .enumerate()
                .map(|(i, f)| {
                    Ok(Descriptor {
                        id: i as u32,
                        codec: f.codec.clone(),
                        bound_codec: projection.codecs.bound(&f.codec)?,
                    })
                })
                .collect::<Result<Vec<_>>>()?;
            let expected = Expectation {
                kind: 2,
                profile: projection.source.profile,
                hash: projection.projection.hash(),
                entity_id: 0,
                projection_id: projection.id,
                descriptors,
                exact_rows: Some(0),
            };
            let bytes = Batch::encode(&expected, 0, &[])?;
            if bytes.len() as u64 > max_bytes {
                return Err(bad());
            }
            *out = Box::into_raw(operation(
                ResultValue::Buffer(buffer(bytes)),
                cursor.retained.clone(),
            ))
            .cast();
            Ok(())
        })
    }
}
/// Exact test-only entry forwarding to the production binding validator.
/// # Safety
/// Inputs must be active creating-fixture owners and valid borrowed extents; outputs writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_close_create_v1(
    owner: *mut DbOwner,
    out: *mut *mut DbOwner,
    error: *mut NwErrorInfoV1,
) -> NwStatus {
    // SAFETY: exact ABI entry validates kinds, extents and zeroes outputs before semantic admission.
    unsafe {
        finish(error, || {
            output(out)?;
            reference(owner)?;
            *out = Box::into_raw(operation(ResultValue::Empty, Vec::new())).cast();
            Ok(())
        })
    }
}
/// Exact test-only entry forwarding to the production binding validator.
/// # Safety
/// Inputs must be active creating-fixture owners and valid borrowed extents; outputs writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_snapshot_v1(
    owner: *mut DbOwner,
    _query: NwBytesViewV1,
    out: *mut *mut DbBuffer,
    error: *mut NwErrorInfoV1,
) -> NwStatus {
    // SAFETY: exact ABI entry validates kinds, extents and zeroes outputs before semantic admission.
    unsafe {
        finish(error, || {
            output(out)?;
            let bytes = match &reference(owner)?.value {
                Value::Operation(op) => op.lock().map_err(|_| wrong())?.retained.clone(),
                Value::Plan(plan) => plan.parameter_schema(),
                Value::Query(q) => {
                    let q = q.lock().map_err(|_| wrong())?;
                    format!("{:?}:{:?}", q.state(), q.counts()).into_bytes()
                }
                _ => return Err(wrong()),
            };
            *out = Box::into_raw(buffer(bytes)).cast();
            Ok(())
        })
    }
}
/// Exact test-only entry forwarding to the production binding validator.
/// # Safety
/// Inputs must be active creating-fixture owners and valid borrowed extents; outputs writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_operation_start_v1(
    owner: *mut DbOwner,
    callbacks: *const NwOperationCallbacksV1,
    error: *mut NwErrorInfoV1,
) -> NwStatus {
    // SAFETY: exact ABI entry validates kinds, extents and zeroes outputs before semantic admission.
    unsafe {
        finish(error, || {
            let Value::Operation(op) = &reference(owner)?.value else {
                return Err(wrong());
            };
            if callbacks.is_null() {
                return Err(bad());
            }
            let callbacks = &*callbacks;
            if callbacks.struct_size != std::mem::size_of::<NwOperationCallbacksV1>() as u32
                || callbacks.flags != 0
            {
                return Err(bad());
            }
            let mut op = op.lock().map_err(|_| wrong())?;
            if op.started {
                return Err(wrong());
            }
            op.started = true;
            op.terminal_canceled = op.canceled;
            op.callback_active = true;
            let status = if op.terminal_canceled {
                NW_CANCELED
            } else {
                NW_OK
            };
            drop(op);
            if let Some(callback) = callbacks.on_terminal {
                let info = NwErrorInfoV1 {
                    struct_size: std::mem::size_of::<NwErrorInfoV1>() as u32,
                    domain: 0,
                    code: 0,
                    flags: 0,
                    correlation_id: 0,
                };
                callback(callbacks.cookie, status, &info);
            }
            let Value::Operation(operation) = &reference(owner)?.value else {
                unreachable!()
            };
            operation.lock().map_err(|_| wrong())?.callback_active = false;
            Ok(())
        })
    }
}
/// Exact test-only entry forwarding to the production binding validator.
/// # Safety
/// Inputs must be active creating-fixture owners and valid borrowed extents; outputs writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_operation_request_cancel_v1(owner: *mut DbOwner) -> NwStatus {
    // SAFETY: exact ABI entry validates kinds, extents and zeroes outputs before semantic admission.
    unsafe {
        finish(std::ptr::null_mut(), || {
            let Value::Operation(op) = &reference(owner)?.value else {
                return Err(wrong());
            };
            op.lock().map_err(|_| wrong())?.canceled = true;
            Ok(())
        })
    }
}
/// Exact test-only entry forwarding to the production binding validator.
/// # Safety
/// Inputs must be active creating-fixture owners and valid borrowed extents; outputs writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_operation_take_owner_v1(
    owner: *mut DbOwner,
    kind: u32,
    out: *mut *mut DbOwner,
    error: *mut NwErrorInfoV1,
) -> NwStatus {
    // SAFETY: exact ABI entry validates kinds, extents and zeroes outputs before semantic admission.
    unsafe {
        finish(error, || {
            output(out)?;
            let Value::Operation(op) = &reference(owner)?.value else {
                return Err(wrong());
            };
            let mut op = op.lock().map_err(|_| wrong())?;
            if !op.started || op.taken || op.terminal_canceled {
                return Err(wrong());
            }
            if !matches!(op.result,Some(ResultValue::Owner(_,expected)) if expected==kind) {
                return Err(wrong());
            }
            let Some(ResultValue::Owner(result, _)) = op.result.take() else {
                unreachable!()
            };
            op.taken = true;
            *out = Box::into_raw(result).cast();
            Ok(())
        })
    }
}
/// Exact test-only entry forwarding to the production binding validator.
/// # Safety
/// Inputs must be active creating-fixture owners and valid borrowed extents; outputs writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_operation_take_buffer_v1(
    owner: *mut DbOwner,
    out: *mut *mut DbBuffer,
    error: *mut NwErrorInfoV1,
) -> NwStatus {
    // SAFETY: exact ABI entry validates kinds, extents and zeroes outputs before semantic admission.
    unsafe {
        finish(error, || {
            output(out)?;
            let Value::Operation(op) = &reference(owner)?.value else {
                return Err(wrong());
            };
            let mut op = op.lock().map_err(|_| wrong())?;
            if !op.started
                || op.taken
                || op.terminal_canceled
                || !matches!(op.result, Some(ResultValue::Buffer(_)))
            {
                return Err(wrong());
            }
            let Some(ResultValue::Buffer(result)) = op.result.take() else {
                unreachable!()
            };
            op.taken = true;
            *out = Box::into_raw(result).cast();
            Ok(())
        })
    }
}
/// Exact test-only entry forwarding to the production binding validator.
/// # Safety
/// Inputs must be active creating-fixture owners and valid borrowed extents; outputs writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_operation_try_retire_v1(owner: *mut DbOwner) -> NwStatus {
    // SAFETY: exact ABI entry validates kinds, extents and zeroes outputs before semantic admission.
    unsafe {
        finish(std::ptr::null_mut(), || {
            let Value::Operation(op) = &reference(owner)?.value else {
                return Err(wrong());
            };
            let mut op = op.lock().map_err(|_| wrong())?;
            if !op.started || op.callback_active {
                return Err(BindingError {
                    status: NW_PENDING,
                    detail: "not started",
                });
            }
            op.retired = true;
            Ok(())
        })
    }
}
/// Exact test-only entry forwarding to the production binding validator.
/// # Safety
/// Inputs must be active creating-fixture owners and valid borrowed extents; outputs writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_buffer_view_v1(
    owner: *mut DbBuffer,
    out: *mut NwBytesViewV1,
    error: *mut NwErrorInfoV1,
) -> NwStatus {
    // SAFETY: exact ABI entry validates kinds, extents and zeroes outputs before semantic admission.
    unsafe {
        finish(error, || {
            if out.is_null() {
                return Err(bad());
            }
            out.write(NwBytesViewV1 {
                data: std::ptr::null(),
                length: 0,
            });
            if owner.is_null() {
                return Err(bad());
            }
            let buffer = &*owner.cast::<Buffer>();
            out.write(NwBytesViewV1 {
                data: buffer.bytes.as_ptr(),
                length: buffer.bytes.len() as u64,
            });
            Ok(())
        })
    }
}
/// Consumes one creating-fixture owner after its access/retirement precondition.
/// # Safety
/// The pointer must be active, of the matching kind, and released exactly once with no live access.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_owner_release_v1(owner: *mut DbOwner) {
    if !owner.is_null() {
        // SAFETY: valid release consumes one active creating-fixture non-operation owner exactly once.
        unsafe { drop(Box::from_raw(owner.cast::<Owner>())) };
    }
}
/// Consumes one creating-fixture owner after its access/retirement precondition.
/// # Safety
/// The pointer must be active, of the matching kind, and released exactly once with no live access.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_operation_release_v1(owner: *mut DbOwner) {
    if !owner.is_null() {
        // SAFETY: test harness releases Created or proven Retired owners only, with no active calls.
        unsafe {
            let value = Box::from_raw(owner.cast::<Owner>());
            if let Value::Operation(op) = &value.value {
                let op = op.lock().unwrap();
                assert!(!op.started || op.retired);
            }
            drop(value)
        };
    }
}
/// Consumes one creating-fixture owner after its access/retirement precondition.
/// # Safety
/// The pointer must be active, of the matching kind, and released exactly once with no live access.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_buffer_release_v1(owner: *mut DbBuffer) {
    if !owner.is_null() {
        // SAFETY: one active immutable fixture buffer is consumed after all synchronous views end.
        unsafe { drop(Box::from_raw(owner.cast::<Buffer>())) };
    }
}
#[unsafe(no_mangle)]
pub extern "C" fn nw_test_live_v1(kind: u32) -> u64 {
    match kind {
        1 => OWNERS.load(Ordering::Relaxed) as u64,
        2 => OPERATIONS.load(Ordering::Relaxed) as u64,
        3 => BUFFERS.load(Ordering::Relaxed) as u64,
        _ => 0,
    }
}
// Rust compiler checks the separately implemented function against the generated normative alias.
const _: NwDbRelationRegisterV1 = nw_db_relation_register_v1;
const _: NwDbRelationLoadCreateV1 = nw_db_relation_load_create_v1;
const _: NwDbQueryCreateV1 = nw_db_query_create_v1;
const _: NwDbExprColumnV1 = nw_db_expr_column_v1;
const _: NwDbExprValueV1 = nw_db_expr_value_v1;
const _: NwDbExprParameterV1 = nw_db_expr_parameter_v1;
const _: NwDbExprBinaryV1 = nw_db_expr_binary_v1;
const _: NwDbExprUnaryV1 = nw_db_expr_unary_v1;
const _: NwDbExprListV1 = nw_db_expr_list_v1;
const _: NwDbQueryImportV1 = nw_db_query_import_v1;
const _: NwDbQueryClauseV1 = nw_db_query_clause_v1;
const _: NwDbQuerySealV1 = nw_db_query_seal_v1;
const _: NwDbQueryCloneV1 = nw_db_query_clone_v1;
const _: NwDbCteCreateV1 = nw_db_cte_create_v1;
const _: NwDbExecuteCreateV1 = nw_db_execute_create_v1;
const _: NwDbOperationStartV1 = nw_db_operation_start_v1;
const _: NwDbOperationRequestCancelV1 = nw_db_operation_request_cancel_v1;
const _: NwDbOperationTakeOwnerV1 = nw_db_operation_take_owner_v1;
const _: NwDbOperationTryRetireV1 = nw_db_operation_try_retire_v1;
const _: NwDbOperationReleaseV1 = nw_db_operation_release_v1;
#[cfg(test)]
mod tests;

fn relation_caps() -> RelationCapabilities {
    RelationCapabilities {
        metadata: true,
        scalar_codecs: true,
        query: true,
        streaming: true,
        explicit_relationships: true,
    }
}

/// Test-only sample helper; returns two ordinary creating-fixture owners.
/// # Safety
/// Both outputs must point to aligned writable pointer slots.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_test_open_v1(
    source_out: *mut *mut DbOwner,
    session_out: *mut *mut DbOwner,
) -> NwStatus {
    // SAFETY: helper validates and initializes both caller output slots before invoking exact fixture entries.
    unsafe {
        if source_out.is_null() || session_out.is_null() {
            return NW_INVALID_ARGUMENT;
        }
        *source_out = std::ptr::null_mut();
        *session_out = std::ptr::null_mut();
        let profile = 1u8;
        let status = nw_db_source_create_v1(
            NwBytesViewV1 {
                data: &profile,
                length: 1,
            },
            source_out,
            std::ptr::null_mut(),
        );
        if status != NW_OK {
            return status;
        }
        let status = nw_test_session_v1(*source_out, session_out, std::ptr::null_mut());
        if status != NW_OK {
            nw_db_owner_release_v1(*source_out);
            *source_out = std::ptr::null_mut();
        }
        status
    }
}

fn full_caps(profile: Profile) -> Capabilities {
    Capabilities::from_mask((1 << 25) - 1, profile).unwrap()
}
