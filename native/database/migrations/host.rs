//! B08's exact Host ABI bridge, compiled in the owning B04 crate.
use crate::{
    Error, Result,
    abi::{self, Buffer, OwnerValue},
    config::ProviderConfig,
    control::{self, Fields},
    operation::{DbOperation, Value},
    runtime::Source,
};
use futures_util::FutureExt;
use nativeweave_data_migrations as migrations;
use nativeweave_interop_abi::*;
use sha2::{Digest, Sha256};
use std::{
    ffi::c_void,
    sync::{Arc, atomic::Ordering},
    time::Duration,
};

fn failure(value: migrations::Error) -> Error {
    use migrations::ErrorKind as K;
    let (status, code) = match value.kind {
        K::Invalid => (NW_INVALID_ARGUMENT, 0),
        K::Checksum => (NW_DOMAIN_ERROR, 15),
        K::Dirty => (NW_DOMAIN_ERROR, 8),
        K::Capability => (NW_NOT_SUPPORTED, 5),
        K::Capacity => (NW_DOMAIN_ERROR, 6),
        K::Canceled => (NW_CANCELED, 8),
        K::Timeout => (NW_TIMEOUT, 1),
        _ => (NW_DOMAIN_ERROR, 1),
    };
    let mut result = Error::new(status, code, value.message);
    result.provider = value.provider;
    result.cleanup = value.cleanup.map(|cleanup| Box::new(failure(*cleanup)));
    result
}
fn bundle(bytes: &[u8], source: &Source) -> Result<migrations::Bundle> {
    let fields = Fields::parse(bytes, 12, &[(1, 1, true), (2, 5, true), (3, 5, true)])?;
    let provider = fields.u32(1)?;
    let sequence = fields.bytes(2)?;
    let canonical = control::encode(
        12,
        &[
            (1, 1, provider.to_le_bytes().to_vec()),
            (2, 5, sequence.to_vec()),
        ],
    );
    let hash: [u8; 32] = Sha256::digest(&canonical).into();
    if fields.bytes(3)? != hash {
        return Err(Error::invalid("migration bundle hash mismatch"));
    }
    let entries = control::sequence(sequence)?;
    if entries.len() > source.config.limits.nodes {
        return Err(Error::capacity());
    }
    let mut migrations = Vec::with_capacity(entries.len());
    for entry in entries {
        let fields = Fields::parse(
            entry,
            11,
            &[
                (1, 2, true),
                (2, 4, true),
                (3, 4, true),
                (4, 1, true),
                (5, 5, true),
            ],
        )?;
        let version = i64::try_from(fields.u64(1)?)
            .map_err(|_| Error::invalid("migration version overflow"))?;
        let mode = fields.boolean(4)?;
        let value = migrations::Migration::new(
            version,
            fields.text(2)?.to_owned(),
            fields.text(3)?.to_owned(),
            mode,
        )
        .map_err(failure)?;
        if fields.bytes(5)? != value.sql_hash {
            return Err(Error::invalid("migration SQL hash mismatch"));
        }
        migrations.push(value);
    }
    migrations::Bundle::new(provider, migrations, hash).map_err(failure)
}
fn parse(
    action: u32,
    bytes: &[u8],
    source: &Source,
) -> Result<(
    migrations::Request,
    migrations::Limits,
    migrations::ConnectionOptions,
)> {
    if bytes.len() > source.config.limits.parameter_bytes {
        return Err(Error::capacity());
    }
    let fields = Fields::parse(
        bytes,
        5,
        &[
            (1, 1, true),
            (2, 5, false),
            (3, 2, false),
            (4, 3, false),
            (5, 1, false),
            (6, 4, false),
        ],
    )?;
    let provider = match source.config.provider {
        #[cfg(feature = "postgres")]
        ProviderConfig::Postgres(_) => 0,
        #[cfg(feature = "sqlite")]
        ProviderConfig::Sqlite { .. } => 1,
    };
    if fields.u32(1)? != provider {
        return Err(Error::invalid(
            "administration provider differs from source",
        ));
    }
    let request = migrations::Request {
        action,
        bundle: fields
            .get(2)
            .map(|bytes| bundle(bytes, source))
            .transpose()?,
        target: fields
            .get(3)
            .map(|_| {
                fields.u64(3).and_then(|x| {
                    i64::try_from(x).map_err(|_| Error::invalid("target version overflow"))
                })
            })
            .transpose()?,
        lock_timeout: fields
            .get(4)
            .map(|_| fields.u64(4).map(Duration::from_nanos))
            .transpose()?
            .unwrap_or(Duration::from_secs(30)),
        allow_non_transactional: fields
            .get(5)
            .map(|_| fields.boolean(5))
            .transpose()?
            .unwrap_or(false),
        schema: fields
            .get(6)
            .map(|_| fields.text(6).map(str::to_owned))
            .transpose()?,
    };
    let connect_timeout = match &source.config.provider {
        #[cfg(feature = "postgres")]
        ProviderConfig::Postgres(config) => {
            source.config.limits.admission.min(config.connect_timeout)
        }
        #[cfg(feature = "sqlite")]
        ProviderConfig::Sqlite { .. } => source.config.limits.admission,
    };
    let limits = migrations::Limits {
        rows: source.config.limits.buffered_rows,
        bytes: source.config.limits.buffered_bytes,
        field_bytes: source.config.limits.field_bytes,
        command_timeout: source.config.limits.command,
        connect_timeout,
    };
    request.validate(provider, &limits).map_err(failure)?;
    if limits.bytes < if action == 0 { 100 } else { 36 } || action == 0 && limits.rows == 0 {
        return Err(Error::capacity());
    }
    let options = match &source.config.provider {
        #[cfg(feature = "postgres")]
        ProviderConfig::Postgres(config) => {
            migrations::ConnectionOptions::Postgres(config.options()?)
        }
        #[cfg(feature = "sqlite")]
        ProviderConfig::Sqlite { path, options } => {
            if options.mode == nativeweave_data_sqlite::OpenMode::Memory || path == ":memory:" {
                return Err(Error::unsupported());
            }
            migrations::ConnectionOptions::Sqlite(
                options
                    .connect_options(std::path::Path::new(path), source.config.limits.sqlite())?,
            )
        }
    };
    Ok((request, limits, options))
}
fn sequence(entries: Vec<Vec<u8>>) -> Vec<u8> {
    let mut output = (entries.len() as u32).to_le_bytes().to_vec();
    for entry in entries {
        output.extend_from_slice(&(entry.len() as u32).to_le_bytes());
        output.extend_from_slice(&entry);
    }
    output
}
fn encode(output: migrations::Output) -> Vec<u8> {
    let u32_field = |id, value: u32| (id, 1, value.to_le_bytes().to_vec());
    let u64_field = |id, value: i64| (id, 2, value.to_le_bytes().to_vec());
    match output {
        migrations::Output::Report { applied, current } => {
            let mut fields = vec![u32_field(1, 0), u32_field(3, applied)];
            if let Some(version) = current {
                fields.push(u64_field(4, version));
            }
            fields.push(u32_field(5, 1));
            control::encode(19, &fields)
        }
        migrations::Output::Validated => control::encode(19, &[u32_field(1, 2)]),
        migrations::Output::Status(entries) => {
            let records = entries
                .into_iter()
                .map(|entry| {
                    control::encode(
                        16,
                        &[
                            u64_field(1, entry.version),
                            (2, 4, entry.description.into_bytes()),
                            (3, 4, entry.checksum.into_bytes()),
                            u32_field(4, u32::from(entry.applied)),
                        ],
                    )
                })
                .collect();
            control::encode(19, &[u32_field(1, 1), (2, 5, sequence(records))])
        }
        migrations::Output::Tables(tables) => {
            let records = tables
                .into_iter()
                .map(|table| {
                    let columns = table
                        .columns
                        .into_iter()
                        .map(|column| {
                            control::encode(
                                17,
                                &[
                                    (1, 4, column.name.into_bytes()),
                                    (2, 4, column.store_type.into_bytes()),
                                    u32_field(3, u32::from(column.nullable)),
                                    u32_field(4, u32::from(column.primary_key)),
                                ],
                            )
                        })
                        .collect();
                    let mut fields = Vec::new();
                    if let Some(schema) = table.schema {
                        fields.push((1, 4, schema.into_bytes()));
                    }
                    fields.push((2, 4, table.name.into_bytes()));
                    fields.push((3, 5, sequence(columns)));
                    control::encode(18, &fields)
                })
                .collect();
            control::encode(19, &[u32_field(1, 3), (2, 5, sequence(records))])
        }
    }
}
struct Waiter(Arc<Source>);
impl Drop for Waiter {
    fn drop(&mut self) {
        self.0.administration_waiting.fetch_sub(1, Ordering::AcqRel);
    }
}
async fn closed(source: &Source) {
    loop {
        let changed = source.changed.notified();
        tokio::pin!(changed);
        changed.as_mut().enable();
        if source.closing.load(Ordering::Acquire) {
            return;
        }
        changed.await;
    }
}
async fn execute(
    source: Arc<Source>,
    request: migrations::Request,
    limits: migrations::Limits,
    options: migrations::ConnectionOptions,
    cancel: Arc<crate::operation::Cancel>,
) -> Result<Value> {
    let _permit = match source.administration.clone().try_acquire_owned() {
        Ok(permit) => permit,
        Err(_) => {
            source
                .administration_waiting
                .try_update(Ordering::AcqRel, Ordering::Acquire, |x| {
                    (x < source.config.limits.waiters).then_some(x + 1)
                })
                .map_err(|_| Error::capacity())?;
            let _waiting = Waiter(source.clone());
            tokio::select! {
                result = tokio::time::timeout(source.config.limits.admission,source.administration.clone().acquire_owned()) => result.map_err(|_| Error::new(NW_TIMEOUT,1,"administrative admission timed out"))?.map_err(|_| Error::closed())?,
                _ = cancel.notified() => return Err(Error::canceled()),
                _ = closed(&source) => return Err(Error::closed()),
            }
        }
    };
    source.admit()?;
    let cancellation = Arc::new(migrations::Cancellation::default());
    let stop = cancellation.clone();
    cancel.install(Arc::new(move || stop.cancel()));
    let work = migrations::run(options, request, limits, cancellation.clone());
    tokio::pin!(work);
    let result = tokio::select! {
        result = &mut work => result,
        _ = closed(&source) => { cancellation.cancel(); work.await }
    }
    .map_err(failure)?;
    let bytes = encode(result);
    if bytes.len()
        > source
            .config
            .limits
            .buffered_bytes
            .min(nativeweave_data_contracts::MAX_BYTES)
    {
        return Err(Error::capacity());
    }
    Ok(Value::Buffer(Buffer::bytes(source, bytes)))
}
/// # Safety
/// Input is a borrowed canonical kind5 record. Output is writable and transfers
/// only on OK; all script/configuration bytes are copied before synchronous return.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_db_admin_create_v1(
    raw: *mut c_void,
    action: u32,
    payload: NwBytesViewV1,
    out: *mut *mut c_void,
    error: *mut NwErrorInfoV1,
) -> u32 {
    abi::run(error, || {
        // SAFETY: exact ABI requires an aligned, nonoverlapping writable output slot.
        unsafe {
            abi::clear(out)?;
        }
        let owner = abi::owner(raw)?;
        let OwnerValue::Source(source) = &*owner else {
            return Err(Error::state("administration requires a source owner"));
        };
        source.admit()?;
        // SAFETY: payload is borrowed synchronously and no input pointer escapes.
        let (request, limits, options) = parse(action, unsafe { abi::view(payload)? }, source)?;
        let captured = source.clone();
        let operation = DbOperation::create(
            source.clone(),
            101,
            false,
            Box::new(move |cancel| execute(captured, request, limits, options, cancel).boxed()),
        )?;
        // SAFETY: one owner transfer through the cleared caller-owned slot.
        unsafe {
            out.write(abi::publish_operation(operation)?);
        }
        Ok(())
    })
}
