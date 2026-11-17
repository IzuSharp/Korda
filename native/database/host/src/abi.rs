//! Exact database operation ABI, with module-local owner validation and Core retirement.
use crate::{
    Error, Result,
    control::{self, Fields},
    operation::{DbOperation, Value},
    runtime::{BUFFERS, Source},
    session::{CursorOwner, SavepointOwner, Session, TransactionOwner},
    sql::SqlPlan,
};
use futures_util::FutureExt;
use nativeweave_data_transactions::{
    AdmissionToken, Isolation, Options as TxOptions, RecoveryIdentity,
};
use nativeweave_interop_abi::*;
use nativeweave_query_binding::ModelBinding;
use std::{
    collections::HashMap,
    ffi::c_void,
    panic::{AssertUnwindSafe, catch_unwind},
    sync::{
        Arc, Mutex, OnceLock,
        atomic::{AtomicUsize, Ordering},
    },
};

pub enum OwnerValue {
    Source(Arc<Source>),
    Session(Arc<Session>),
    Cursor(Arc<CursorOwner>),
    Transaction(Arc<TransactionOwner>),
    Savepoint(Arc<SavepointOwner>),
    Model(Arc<Source>, ModelBinding),
    SqlPlan(Arc<SqlPlan>),
}
impl OwnerValue {
    pub fn source(&self) -> Arc<Source> {
        match self {
            Self::Source(v) => v.clone(),
            Self::Session(v) => v.source.clone(),
            Self::Cursor(v) => v.session.source.clone(),
            Self::Transaction(v) => v.session.source.clone(),
            Self::Savepoint(v) => v.transaction.session.source.clone(),
            Self::Model(v, _) => v.clone(),
            Self::SqlPlan(v) => v.source.clone(),
        }
    }
    pub fn kind(&self) -> u32 {
        match self {
            Self::Session(_) => 1,
            Self::Cursor(_) => 2,
            Self::Transaction(_) => 3,
            Self::Savepoint(_) => 4,
            _ => 0,
        }
    }
}
impl Drop for OwnerValue {
    fn drop(&mut self) {
        match self {
            Self::Source(v) => v.request_close(),
            Self::Session(v) => v.request_close(),
            Self::Cursor(v) => v.request_close(),
            Self::Transaction(v) => {
                v.transaction.request_close();
                let retained = v.clone();
                v.session.source.runtime.handle.spawn(async move {
                    retained.transaction.join().await;
                });
            }
            Self::Savepoint(v) => {
                let retained = v.clone();
                v.transaction
                    .session
                    .source
                    .runtime
                    .handle
                    .spawn(async move {
                        let _ = retained
                            .transaction
                            .transaction
                            .dispose_savepoint(retained.token)
                            .await;
                    });
            }
            _ => {}
        }
    }
}
enum Stored {
    Owner(Arc<OwnerValue>),
    Operation(Arc<DbOperation>),
    Buffer(Arc<Buffer>),
}
static HANDLES: OnceLock<Mutex<HashMap<usize, Stored>>> = OnceLock::new();
static NEXT_HANDLE: AtomicUsize = AtomicUsize::new(0x1000);
fn handles() -> &'static Mutex<HashMap<usize, Stored>> {
    HANDLES.get_or_init(|| Mutex::new(HashMap::new()))
}
// Opaque references are monotonically allocated module-local identities, never dereferenced addresses.
// No owner ID is reused within process lifetime; reserved zero cannot name an owner.
fn publish(value: Stored) -> Result<*mut c_void> {
    let id = NEXT_HANDLE
        .try_update(Ordering::AcqRel, Ordering::Acquire, |n| n.checked_add(16))
        .map_err(|_| Error::capacity())?;
    handles()
        .lock()
        .unwrap_or_else(|e| e.into_inner())
        .insert(id, value);
    Ok(id as *mut c_void)
}
pub fn owner(raw: *mut c_void) -> Result<Arc<OwnerValue>> {
    match handles()
        .lock()
        .unwrap_or_else(|e| e.into_inner())
        .get(&(raw as usize))
    {
        Some(Stored::Owner(v)) => Ok(v.clone()),
        _ => Err(Error::state("invalid or retired database owner")),
    }
}
fn operation(raw: *mut c_void) -> Result<Arc<DbOperation>> {
    match handles()
        .lock()
        .unwrap_or_else(|e| e.into_inner())
        .get(&(raw as usize))
    {
        Some(Stored::Operation(v)) => Ok(v.clone()),
        _ => Err(Error::state("invalid operation owner")),
    }
}
fn buffer(raw: *mut c_void) -> Result<Arc<Buffer>> {
    match handles()
        .lock()
        .unwrap_or_else(|e| e.into_inner())
        .get(&(raw as usize))
    {
        Some(Stored::Buffer(v)) => Ok(v.clone()),
        _ => Err(Error::state("invalid buffer owner")),
    }
}
pub fn publish_owner(value: OwnerValue) -> Result<*mut c_void> {
    publish(Stored::Owner(Arc::new(value)))
}
pub fn publish_operation(value: Arc<DbOperation>) -> Result<*mut c_void> {
    publish(Stored::Operation(value))
}
pub enum BufferData {
    Bytes(Vec<u8>),
    Batch(nativeweave_data_streaming::OwnedBatch),
}
pub struct Buffer {
    pub source: Arc<Source>,
    pub data: BufferData,
}
impl Buffer {
    pub fn bytes(source: Arc<Source>, bytes: Vec<u8>) -> Self {
        BUFFERS.fetch_add(1, Ordering::Relaxed);
        Self {
            source,
            data: BufferData::Bytes(bytes),
        }
    }
    pub fn batch(source: Arc<Source>, batch: nativeweave_data_streaming::OwnedBatch) -> Self {
        BUFFERS.fetch_add(1, Ordering::Relaxed);
        Self {
            source,
            data: BufferData::Batch(batch),
        }
    }
    fn view(&self) -> &[u8] {
        match &self.data {
            BufferData::Bytes(b) => b,
            BufferData::Batch(b) => b.bytes(),
        }
    }
}
impl Drop for Buffer {
    fn drop(&mut self) {
        BUFFERS.fetch_sub(1, Ordering::Relaxed);
    }
}
pub fn run(error: *mut NwErrorInfoV1, action: impl FnOnce() -> Result<()>) -> u32 {
    let result = catch_unwind(AssertUnwindSafe(action));
    let failure = match result {
        Ok(Ok(())) => return NW_OK,
        Ok(Err(e)) => e,
        Err(_) => Error::new(NW_NATIVE_PANIC, 8, "native database boundary panic"),
    };
    // SAFETY: every public ABI caller supplies the canonical optional error record contract.
    unsafe { nativeweave_interop_errors::write_error(failure.status, error, failure.fields()) }
}
/// # Safety
/// A nonempty view is readable for exactly its length for this synchronous call.
pub unsafe fn view<'a>(value: NwBytesViewV1) -> Result<&'a [u8]> {
    let len = usize::try_from(value.length).map_err(|_| Error::capacity())?;
    if len > nativeweave_data_contracts::MAX_BYTES {
        return Err(Error::capacity());
    }
    if len == 0 {
        return Ok(&[]);
    }
    if value.data.is_null() {
        return Err(Error::invalid("null byte view"));
    }
    // SAFETY: caller guarantees the admitted bounded range; it is copied before asynchronous use.
    Ok(unsafe { std::slice::from_raw_parts(value.data, len) })
}
/// # Safety
/// Output is a nonoverlapping, aligned writable pointer slot.
unsafe fn clear(out: *mut *mut c_void) -> Result<()> {
    if out.is_null() || !out.is_aligned() {
        return Err(Error::invalid("owner output"));
    }
    // SAFETY: checked alignment and caller-owned writable slot.
    unsafe { out.write(std::ptr::null_mut()) };
    Ok(())
}
pub fn snapshot(
    state: u32,
    outcome: u32,
    attempted: bool,
    cleanup: u32,
    disposition: u32,
    released: bool,
    deadline: bool,
    correlation: u64,
    provider: Option<&str>,
) -> Vec<u8> {
    let mut f = vec![
        (1, 1, state.to_le_bytes().to_vec()),
        (2, 1, outcome.to_le_bytes().to_vec()),
        (3, 1, (attempted as u32).to_le_bytes().to_vec()),
        (4, 1, cleanup.to_le_bytes().to_vec()),
        (5, 1, disposition.to_le_bytes().to_vec()),
        (6, 1, (released as u32).to_le_bytes().to_vec()),
        (7, 1, (deadline as u32).to_le_bytes().to_vec()),
        (8, 2, correlation.to_le_bytes().to_vec()),
    ];
    if let Some(p) = provider {
        f.push((9, 4, p.as_bytes().to_vec()));
    }
    control::encode(7, &f)
}
pub fn source_snapshot(source: &Source) -> Vec<u8> {
    let closed = source.runtime.is_joined();
    snapshot(
        0,
        0,
        false,
        if closed {
            2
        } else if source.closing.load(Ordering::Acquire) {
            1
        } else {
            0
        },
        if closed { 3 } else { 0 },
        closed,
        false,
        0,
        None,
    )
}
fn session_snapshot(session: &Session) -> Vec<u8> {
    let closed = session.retired.load(Ordering::Acquire);
    snapshot(
        0,
        0,
        false,
        if closed {
            2
        } else if session.closing.load(Ordering::Acquire) {
            1
        } else {
            0
        },
        if closed { 3 } else { 0 },
        closed,
        false,
        0,
        None,
    )
}
fn tx_snapshot(tx: &TransactionOwner) -> Vec<u8> {
    let s = tx.snapshot();
    snapshot(
        s.state as u32,
        s.outcome as u32,
        s.commit_attempted,
        s.cleanup as u32,
        s.disposition as u32,
        s.local_resources_released,
        s.deadline_exceeded,
        s.correlation_id,
        s.provider_code.as_deref(),
    )
}

/// Stateless negotiated bootstrap; no runtime or database connection is created.
/// # Safety
/// The output follows the existing A01 writable bootstrap contract.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_database_get_abi_v1(
    major: u32,
    capacity: u32,
    output: *mut NwAbiInfoV1,
) -> u32 {
    let mask =
        (1 << 0) | (1 << 2) | (1 << 3) | (1 << 4) | (1 << 13) | (1 << 16) | (1 << 23) | (1 << 24);
    // SAFETY: exact forwarding to the canonical ABI writer.
    unsafe { write_abi_v1(major, capacity, output, 0, mask) }
}
/// # Safety
/// All views/output records obey the canonical ABI borrowing contract.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_source_create_v1(
    config: NwBytesViewV1,
    out: *mut *mut c_void,
    error: *mut NwErrorInfoV1,
) -> u32 {
    run(error, || {
        // SAFETY: caller supplies independent valid view/output ranges.
        unsafe {
            clear(out)?;
            let source = Source::create(crate::config::Config::parse(view(config)?)?)?;
            out.write(publish_owner(OwnerValue::Source(source))?);
        }
        Ok(())
    })
}
/// # Safety
/// Output/error slots are valid and independent of input owners.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_session_open_create_v1(
    raw: *mut c_void,
    out: *mut *mut c_void,
    error: *mut NwErrorInfoV1,
) -> u32 {
    run(error, || {
        // SAFETY: output slot is caller-owned.
        unsafe {
            clear(out)?;
        }
        let owner = owner(raw)?;
        let OwnerValue::Source(source) = &*owner else {
            return Err(Error::state("source required"));
        };
        let source = source.clone();
        let captured = source.clone();
        let op = DbOperation::create(
            source,
            1,
            false,
            Box::new(move |cancel| {
                async move {
                    Ok(Value::Owner(OwnerValue::Session(
                        Session::open(captured, cancel).await?,
                    )))
                }
                .boxed()
            }),
        )?;
        // SAFETY: output was cleared and remains writable through this call.
        unsafe {
            out.write(publish_operation(op)?);
        }
        Ok(())
    })
}
/// # Safety
/// Callback/error records are readable/writable and callback root remains until retirement.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_operation_start_v1(
    raw: *mut c_void,
    callbacks: *const NwOperationCallbacksV1,
    error: *mut NwErrorInfoV1,
) -> u32 {
    run(error, || {
        if callbacks.is_null() || !callbacks.is_aligned() {
            return Err(Error::invalid("callback record"));
        }
        let op = operation(raw)?;
        // SAFETY: valid callback table and retained managed root are guaranteed by the caller.
        let status = unsafe { op.start(callbacks.read()) };
        if status != NW_OK {
            return Err(Error::new(status, 8, "operation start rejected"));
        }
        Ok(())
    })
}
/// # Safety
/// The operation reference must be from this creating module.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_operation_request_cancel_v1(raw: *mut c_void) -> u32 {
    match operation(raw) {
        Ok(op) => {
            op.cancel.request();
            op.core.request_cancel()
        }
        Err(e) => e.status,
    }
}
/// # Safety
/// The operation reference must be live throughout this control call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_operation_try_retire_v1(raw: *mut c_void) -> u32 {
    match operation(raw) {
        Ok(op) => op.core.try_retire(),
        Err(e) => e.status,
    }
}
/// # Safety
/// Only a never-started or proven-retired operation may be released once.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_operation_release_v1(raw: *mut c_void) {
    if let Ok(op) = operation(raw) {
        if op.core.can_release() {
            let removed = handles()
                .lock()
                .unwrap_or_else(|e| e.into_inner())
                .remove(&(raw as usize));
            drop(removed);
        } else {
            op.source.poisoned.store(true, Ordering::Release);
        }
    }
}
/// # Safety
/// Caller owns the writable slot and one successful typed transfer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_operation_take_owner_v1(
    raw: *mut c_void,
    kind: u32,
    out: *mut *mut c_void,
    error: *mut NwErrorInfoV1,
) -> u32 {
    run(error, || {
        // SAFETY: canonical independent output slot.
        unsafe {
            clear(out)?;
        }
        let op = operation(raw)?;
        if !(1..=4).contains(&kind) || op.result_kind != kind {
            return Err(Error::state("wrong result owner kind"));
        }
        let result = op
            .core
            .take_result()
            .map_err(|s| Error::new(s, 8, "owner result unavailable"))?;
        let Value::Owner(owner) = result else {
            return Err(Error::state("owner result required"));
        };
        // SAFETY: output remains writable; ownership moves only after full validation.
        unsafe {
            out.write(publish_owner(owner)?);
        }
        Ok(())
    })
}
/// # Safety
/// Caller supplies a writable Int64 output independent of the operation.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_operation_take_count_v1(
    raw: *mut c_void,
    out: *mut i64,
    error: *mut NwErrorInfoV1,
) -> u32 {
    run(error, || {
        if out.is_null() || !out.is_aligned() {
            return Err(Error::invalid("count output"));
        }
        // SAFETY: caller guarantees writable aligned output.
        unsafe {
            out.write(0);
        }
        let op = operation(raw)?;
        if op.result_kind != 100 {
            return Err(Error::state("count result required"));
        }
        let Value::Count(count) = op
            .core
            .take_result()
            .map_err(|s| Error::new(s, 8, "count unavailable"))?
        else {
            return Err(Error::state("count result required"));
        };
        // SAFETY: the initialized output remains valid.
        unsafe {
            out.write(count);
        }
        Ok(())
    })
}
/// # Safety
/// Output/error records follow the canonical ABI ownership contract.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_operation_take_buffer_v1(
    raw: *mut c_void,
    out: *mut *mut c_void,
    error: *mut NwErrorInfoV1,
) -> u32 {
    run(error, || {
        // SAFETY: independent writable owner slot.
        unsafe {
            clear(out)?;
        }
        let op = operation(raw)?;
        if op.result_kind != 101 {
            return Err(Error::state("buffer result required"));
        }
        let Value::Buffer(buffer) = op
            .core
            .take_result()
            .map_err(|s| Error::new(s, 8, "buffer unavailable"))?
        else {
            return Err(Error::state("buffer result required"));
        };
        // SAFETY: one transfer through the previously cleared output.
        unsafe {
            out.write(publish(Stored::Buffer(Arc::new(buffer)))?);
        }
        Ok(())
    })
}
/// # Safety
/// Rich-error ownership transfers independently of the primary result.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_operation_take_error_v1(
    raw: *mut c_void,
    out: *mut *mut c_void,
    error: *mut NwErrorInfoV1,
) -> u32 {
    run(error, || {
        // SAFETY: canonical output slot.
        unsafe {
            clear(out)?;
        }
        let op = operation(raw)?;
        let value = op
            .error
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .clone()
            .ok_or_else(|| Error::state("no rich error"))?;
        if op.error_taken.swap(true, Ordering::AcqRel) {
            return Err(Error::state("rich error already taken"));
        }
        let bytes = snapshot(
            0,
            0,
            false,
            0,
            0,
            false,
            false,
            value.correlation,
            value.provider.as_deref(),
        );
        let buffer = Buffer::bytes(op.source.clone(), bytes);
        // SAFETY: one owned immutable error payload transfer.
        unsafe {
            out.write(publish(Stored::Buffer(Arc::new(buffer)))?);
        }
        Ok(())
    })
}
/// # Safety
/// Configuration view is borrowed synchronously; output is writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_codec_register_v1(
    raw: *mut c_void,
    logical: u32,
    configuration: NwBytesViewV1,
    out: *mut u32,
    error: *mut NwErrorInfoV1,
) -> u32 {
    run(error, || {
        if out.is_null() || !out.is_aligned() {
            return Err(Error::invalid("codec output"));
        }
        // SAFETY: independent view/output ranges supplied by caller.
        unsafe {
            out.write(0);
        }
        let owner = owner(raw)?;
        let OwnerValue::Source(source) = &*owner else {
            return Err(Error::state("source required"));
        };
        // SAFETY: synchronous borrowed view.
        let bytes = unsafe { view(configuration)? };
        let configuration = if bytes.is_empty() {
            None
        } else {
            Some(
                std::str::from_utf8(bytes)
                    .map_err(|_| Error::invalid("codec UTF8"))?
                    .to_owned(),
            )
        };
        let id = source.register_codec(logical, configuration)?;
        // SAFETY: validated output slot.
        unsafe {
            out.write(id);
        }
        Ok(())
    })
}
/// # Safety
/// Metadata is copied before this synchronous call returns.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_metadata_register_v1(
    raw: *mut c_void,
    metadata: NwBytesViewV1,
    out: *mut *mut c_void,
    error: *mut NwErrorInfoV1,
) -> u32 {
    run(error, || {
        // SAFETY: canonical synchronous view/output.
        unsafe {
            clear(out)?;
        }
        let parent = owner(raw)?;
        let OwnerValue::Source(source) = &*parent else {
            return Err(Error::state("source required"));
        };
        // SAFETY: borrowed metadata is read only during this call.
        let model = source.register_model(unsafe { view(metadata)? })?;
        // SAFETY: successful ownership transfer.
        unsafe {
            out.write(publish_owner(OwnerValue::Model(source.clone(), model))?);
        }
        Ok(())
    })
}
/// # Safety
/// Projection record is borrowed synchronously; output is writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_projection_register_v1(
    raw: *mut c_void,
    projection: NwBytesViewV1,
    out: *mut u32,
    error: *mut NwErrorInfoV1,
) -> u32 {
    run(error, || {
        if out.is_null() || !out.is_aligned() {
            return Err(Error::invalid("projection output"));
        }
        // SAFETY: canonical independent output slot.
        unsafe {
            out.write(0);
        }
        let parent = owner(raw)?;
        let OwnerValue::Source(source) = &*parent else {
            return Err(Error::state("source required"));
        };
        // SAFETY: synchronous view and output remain live for this call.
        unsafe {
            let projection = source.register_projection(view(projection)?)?;
            out.write(projection.id);
        }
        Ok(())
    })
}
/// # Safety
/// Every input view remains readable for this synchronous snapshot.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_sql_create_v1(
    raw: *mut c_void,
    template: NwBytesViewV1,
    parameters: NwBytesViewV1,
    options: NwBytesViewV1,
    out: *mut *mut c_void,
    error: *mut NwErrorInfoV1,
) -> u32 {
    run(error, || {
        // SAFETY: output initialized before any input validation.
        unsafe {
            clear(out)?;
        }
        let parent = owner(raw)?;
        let OwnerValue::Session(session) = &*parent else {
            return Err(Error::state("session required"));
        };
        session.source.admit()?;
        // SAFETY: inputs are copied into a complete native immutable plan before return.
        let plan = unsafe {
            SqlPlan::parse(
                session.source.clone(),
                view(template)?,
                view(parameters)?,
                view(options)?,
            )?
        };
        // SAFETY: successful native owner transfer.
        unsafe {
            out.write(publish_owner(OwnerValue::SqlPlan(Arc::new(plan)))?);
        }
        Ok(())
    })
}
/// # Safety
/// The session and plan must remain alive during creation; values are copied synchronously.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_execute_create_v1(
    session: *mut c_void,
    plan: *mut c_void,
    parameters: NwBytesViewV1,
    out: *mut *mut c_void,
    error: *mut NwErrorInfoV1,
) -> u32 {
    run(error, || {
        // SAFETY: output/view ranges meet ABI preconditions.
        unsafe {
            clear(out)?;
        }
        let s = owner(session)?;
        let p = owner(plan)?;
        let OwnerValue::Session(session) = &*s else {
            return Err(Error::state("session required"));
        };
        let OwnerValue::SqlPlan(plan) = &*p else {
            return Err(Error::state("execution plan required"));
        };
        // SAFETY: exact raw command snapshot is checked while its view remains borrowed.
        let arguments = unsafe { view(parameters)? };
        if !arguments.is_empty() && arguments != plan.parameters {
            return Err(Error::invalid("raw command arguments changed"));
        }
        let gate = session.admit(false)?;
        plan.submit(&session.source)?;
        let result_kind = if plan.result_kind == 0 { 100 } else { 2 };
        let session = session.clone();
        let plan = plan.clone();
        let op = DbOperation::create(
            session.source.clone(),
            result_kind,
            false,
            Box::new(move |cancel| {
                async move { session.execute(plan, cancel, gate).await }.boxed()
            }),
        )?;
        // SAFETY: successful Created-operation transfer.
        unsafe {
            out.write(publish_operation(op)?);
        }
        Ok(())
    })
}
/// # Safety
/// Cursor input and operation output follow canonical owner rules.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_cursor_fetch_create_v1(
    raw: *mut c_void,
    max_rows: u32,
    max_bytes: u64,
    out: *mut *mut c_void,
    error: *mut NwErrorInfoV1,
) -> u32 {
    run(error, || {
        // SAFETY: writable operation output.
        unsafe {
            clear(out)?;
        }
        let parent = owner(raw)?;
        let OwnerValue::Cursor(cursor) = &*parent else {
            return Err(Error::state("cursor required"));
        };
        if max_rows == 0
            || max_rows as usize > cursor.session.source.config.limits.batch_rows
            || max_bytes == 0
            || max_bytes > cursor.session.source.config.limits.batch_bytes as u64
        {
            return Err(Error::invalid("fetch capacity"));
        }
        let cursor = cursor.clone();
        let source = cursor.session.source.clone();
        let op = DbOperation::create(
            source.clone(),
            101,
            false,
            Box::new(move |cancel| {
                async move {
                    let batch = cursor.fetch(cancel).await?;
                    Ok(Value::Buffer(Buffer::batch(source, batch)))
                }
                .boxed()
            }),
        )?;
        // SAFETY: transfer only after full creation succeeds.
        unsafe {
            out.write(publish_operation(op)?);
        }
        Ok(())
    })
}
/// # Safety
/// Close never borrows application memory beyond the synchronous call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_close_create_v1(
    raw: *mut c_void,
    out: *mut *mut c_void,
    error: *mut NwErrorInfoV1,
) -> u32 {
    run(error, || {
        // SAFETY: canonical output slot.
        unsafe {
            clear(out)?;
        }
        let value = owner(raw)?;
        let source = value.source();
        let op = match &*value {
            OwnerValue::Source(s) => DbOperation::close_source(s.clone())?,
            OwnerValue::Session(s) => {
                let s = s.clone();
                DbOperation::create(
                    source.clone(),
                    101,
                    true,
                    Box::new(move |_| {
                        async move {
                            s.request_close();
                            s.join().await;
                            Ok(Value::Buffer(Buffer::bytes(
                                s.source.clone(),
                                session_snapshot(&s),
                            )))
                        }
                        .boxed()
                    }),
                )?
            }
            OwnerValue::Cursor(c) => {
                let c = c.clone();
                DbOperation::create(
                    source.clone(),
                    101,
                    true,
                    Box::new(move |_| {
                        async move {
                            c.request_close();
                            c.join().await;
                            Ok(Value::Buffer(Buffer::bytes(
                                c.session.source.clone(),
                                snapshot(0, 0, false, 2, 0, true, false, 0, None),
                            )))
                        }
                        .boxed()
                    }),
                )?
            }
            OwnerValue::Transaction(t) => {
                let t = t.clone();
                DbOperation::create(
                    source.clone(),
                    101,
                    true,
                    Box::new(move |_| {
                        async move {
                            t.transaction.request_close();
                            t.transaction.join().await;
                            Ok(Value::Buffer(Buffer::bytes(
                                t.session.source.clone(),
                                tx_snapshot(&t),
                            )))
                        }
                        .boxed()
                    }),
                )?
            }
            _ => return Err(Error::state("owner has no close operation")),
        };
        // SAFETY: created control operation is transferred once.
        unsafe {
            out.write(publish_operation(op)?);
        }
        Ok(())
    })
}
/// # Safety
/// Snapshot query/output views obey the synchronous borrowing contract.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_snapshot_v1(
    raw: *mut c_void,
    query: NwBytesViewV1,
    out: *mut *mut c_void,
    error: *mut NwErrorInfoV1,
) -> u32 {
    run(error, || {
        // SAFETY: canonical output and borrowed query.
        unsafe {
            clear(out)?;
            if !view(query)?.is_empty() {
                return Err(Error::invalid("snapshot query"));
            }
        }
        let value = owner(raw)?;
        let bytes = match &*value {
            OwnerValue::Source(s) => source_snapshot(s),
            OwnerValue::Session(s) => session_snapshot(s),
            OwnerValue::Transaction(t) => tx_snapshot(t),
            OwnerValue::Cursor(c) => snapshot(
                0,
                0,
                false,
                if c.retired.load(Ordering::Acquire) {
                    2
                } else {
                    1
                },
                0,
                c.retired.load(Ordering::Acquire),
                false,
                0,
                None,
            ),
            _ => return Err(Error::state("owner has no snapshot")),
        };
        // SAFETY: newly allocated immutable result transfer.
        unsafe {
            out.write(publish(Stored::Buffer(Arc::new(Buffer::bytes(
                value.source(),
                bytes,
            ))))?);
        }
        Ok(())
    })
}
/// # Safety
/// Release consumes one valid owner, once, after outstanding managed calls settle.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_owner_release_v1(raw: *mut c_void) {
    let value = {
        let mut all = handles().lock().unwrap_or_else(|e| e.into_inner());
        if matches!(all.get(&(raw as usize)), Some(Stored::Owner(_))) {
            all.remove(&(raw as usize))
        } else {
            None
        }
    };
    drop(value);
}
/// # Safety
/// Buffer owner remains retained until the caller finishes copying its borrowed bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_buffer_view_v1(
    raw: *mut c_void,
    out: *mut NwBytesViewV1,
    error: *mut NwErrorInfoV1,
) -> u32 {
    run(error, || {
        if out.is_null() || !out.is_aligned() {
            return Err(Error::invalid("buffer view output"));
        }
        // SAFETY: caller supplies writable independent view record.
        unsafe {
            out.write(NwBytesViewV1 {
                data: std::ptr::null(),
                length: 0,
            });
        }
        let b = buffer(raw)?;
        let data = b.view();
        // SAFETY: backing lives under the separate caller-owned Buffer handle after the local Arc drops.
        unsafe {
            out.write(NwBytesViewV1 {
                data: data.as_ptr(),
                length: data.len() as u64,
            });
        }
        Ok(())
    })
}
/// # Safety
/// One successful buffer transfer is consumed once, after all byte borrows end.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_buffer_release_v1(raw: *mut c_void) {
    let value = {
        let mut all = handles().lock().unwrap_or_else(|e| e.into_inner());
        if matches!(all.get(&(raw as usize)), Some(Stored::Buffer(_))) {
            all.remove(&(raw as usize))
        } else {
            None
        }
    };
    drop(value);
}

fn transaction_options(bytes: &[u8]) -> Result<TxOptions> {
    let f = Fields::parse(
        bytes,
        3,
        &[
            (1, 1, true),
            (2, 1, true),
            (3, 3, true),
            (4, 3, true),
            (5, 4, false),
        ],
    )?;
    let isolation = match f.u32(1)? {
        0 => None,
        1 => Some(Isolation::ReadCommitted),
        2 => Some(Isolation::RepeatableRead),
        3 => Some(Isolation::Serializable),
        _ => return Err(Error::unsupported()),
    };
    Ok(TxOptions {
        isolation,
        read_only: f.boolean(2)?,
        commit_timeout: std::time::Duration::from_nanos(f.u64(3)?),
        cleanup_timeout: std::time::Duration::from_nanos(f.u64(4)?),
        recovery: f
            .get(5)
            .map(|b| {
                std::str::from_utf8(b).map(|s| RecoveryIdentity {
                    key: s.to_owned(),
                    scope: None,
                    request_hash: None,
                })
            })
            .transpose()
            .map_err(|_| Error::invalid("recovery key"))?,
    })
}
/// # Safety
/// Transaction options are synchronously borrowed; owner result is zero on failure.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_tx_begin_create_v1(
    raw: *mut c_void,
    options: NwBytesViewV1,
    out: *mut *mut c_void,
    error: *mut NwErrorInfoV1,
) -> u32 {
    run(error, || {
        // SAFETY: canonical input/output ranges.
        unsafe {
            clear(out)?;
        }
        let parent = owner(raw)?;
        let OwnerValue::Session(session) = &*parent else {
            return Err(Error::state("session required"));
        };
        // SAFETY: options copied before return.
        let options = transaction_options(unsafe { view(options)? })?;
        let gate = session.admit(true)?;
        let session = session.clone();
        let op = DbOperation::create(
            session.source.clone(),
            3,
            false,
            Box::new(move |cancel| {
                async move {
                    Ok(Value::Owner(OwnerValue::Transaction(
                        session.begin(options, cancel, gate).await?,
                    )))
                }
                .boxed()
            }),
        )?;
        // SAFETY: one Created-operation transfer.
        unsafe {
            out.write(publish_operation(op)?);
        }
        Ok(())
    })
}
/// # Safety
/// The transaction reference is retained and the output is a writable owner slot.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_tx_finish_create_v1(
    raw: *mut c_void,
    action: u32,
    out: *mut *mut c_void,
    error: *mut NwErrorInfoV1,
) -> u32 {
    run(error, || {
        // SAFETY: canonical output.
        unsafe {
            clear(out)?;
        }
        if action > 1 {
            return Err(Error::invalid("transaction finish action"));
        }
        let value = owner(raw)?;
        let OwnerValue::Transaction(tx) = &*value else {
            return Err(Error::state("transaction required"));
        };
        let gate = tx.session.admit(true)?;
        let tx = tx.clone();
        let op = DbOperation::create(
            tx.session.source.clone(),
            101,
            false,
            Box::new(move |cancel| {
                async move {
                    let token = AdmissionToken::default();
                    if cancel.requested() {
                        token.cancel();
                    }
                    let result = if action == 0 {
                        tx.transaction.commit(&token).await
                    } else {
                        tx.transaction.rollback(&token).await
                    };
                    gate.complete(result.is_err());
                    result?;
                    Ok(Value::Buffer(Buffer::bytes(
                        tx.session.source.clone(),
                        tx_snapshot(&tx),
                    )))
                }
                .boxed()
            }),
        )?;
        // SAFETY: success transfers a complete operation.
        unsafe {
            out.write(publish_operation(op)?);
        }
        Ok(())
    })
}
/// # Safety
/// Transaction and result owner slots obey the creating-module contract.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_savepoint_create_v1(
    raw: *mut c_void,
    out: *mut *mut c_void,
    error: *mut NwErrorInfoV1,
) -> u32 {
    run(error, || {
        // SAFETY: canonical output.
        unsafe {
            clear(out)?;
        }
        let value = owner(raw)?;
        let OwnerValue::Transaction(tx) = &*value else {
            return Err(Error::state("transaction required"));
        };
        let gate = tx.session.admit(true)?;
        let tx = tx.clone();
        let op = DbOperation::create(
            tx.session.source.clone(),
            4,
            false,
            Box::new(move |cancel| {
                async move {
                    let admission = AdmissionToken::default();
                    if cancel.requested() {
                        admission.cancel();
                    }
                    let token = tx.transaction.create_savepoint(&admission).await?;
                    gate.complete(false);
                    Ok(Value::Owner(OwnerValue::Savepoint(Arc::new(
                        SavepointOwner {
                            transaction: tx,
                            token,
                        },
                    ))))
                }
                .boxed()
            }),
        )?;
        // SAFETY: fully created operation transfer.
        unsafe {
            out.write(publish_operation(op)?);
        }
        Ok(())
    })
}
/// # Safety
/// Savepoint/action/output must obey retained owner and borrowed-slot rules.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_savepoint_action_create_v1(
    raw: *mut c_void,
    action: u32,
    out: *mut *mut c_void,
    error: *mut NwErrorInfoV1,
) -> u32 {
    run(error, || {
        // SAFETY: initialize output before input checks.
        unsafe {
            clear(out)?;
        }
        if action > 1 {
            return Err(Error::invalid("savepoint action"));
        }
        let value = owner(raw)?;
        let OwnerValue::Savepoint(savepoint) = &*value else {
            return Err(Error::state("savepoint required"));
        };
        let gate = savepoint.transaction.session.admit(true)?;
        let savepoint = savepoint.clone();
        let op = DbOperation::create(
            savepoint.transaction.session.source.clone(),
            101,
            false,
            Box::new(move |cancel| {
                async move {
                    let admission = AdmissionToken::default();
                    if cancel.requested() {
                        admission.cancel();
                    }
                    if action == 0 {
                        savepoint
                            .transaction
                            .transaction
                            .rollback_savepoint(savepoint.token, &admission)
                            .await?;
                    } else {
                        savepoint
                            .transaction
                            .transaction
                            .release_savepoint(savepoint.token, &admission)
                            .await?;
                    }
                    gate.complete(false);
                    Ok(Value::Buffer(Buffer::bytes(
                        savepoint.transaction.session.source.clone(),
                        tx_snapshot(&savepoint.transaction),
                    )))
                }
                .boxed()
            }),
        )?;
        // SAFETY: successful operation transfer.
        unsafe {
            out.write(publish_operation(op)?);
        }
        Ok(())
    })
}

/// Fixed owner counters for actual integration diagnostics; no process address or application data.
#[unsafe(no_mangle)]
pub extern "C" fn nw_db_diagnostic_count_v1(kind: u32) -> u64 {
    use crate::runtime::*;
    (match kind {
        0 => SOURCES.load(Ordering::Acquire),
        1 => SESSIONS.load(Ordering::Acquire),
        2 => OPERATIONS.load(Ordering::Acquire),
        3 => CURSORS.load(Ordering::Acquire),
        4 => BUFFERS.load(Ordering::Acquire),
        5 => handles().lock().unwrap_or_else(|e| e.into_inner()).len(),
        _ => 0,
    }) as u64
}
