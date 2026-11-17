use crate::{
    Error, Result,
    config::{Config, ProviderConfig},
};
use nativeweave_data_contracts::{Codec, Model};
use nativeweave_query_binding::{
    CodecBindings, ModelBinding, ParameterRegistry, ProjectionBinding, SourceIdentity,
};
use std::{
    collections::HashMap,
    sync::{
        Arc, Condvar, Mutex, Weak,
        atomic::{AtomicBool, AtomicU64, AtomicUsize, Ordering},
    },
};
use tokio::{
    runtime::Handle,
    sync::{Notify, Semaphore},
};

static NEXT_SOURCE: AtomicU64 = AtomicU64::new(1);
pub static SOURCES: AtomicUsize = AtomicUsize::new(0);
pub static SESSIONS: AtomicUsize = AtomicUsize::new(0);
pub static OPERATIONS: AtomicUsize = AtomicUsize::new(0);
pub static CURSORS: AtomicUsize = AtomicUsize::new(0);
pub static BUFFERS: AtomicUsize = AtomicUsize::new(0);

/// One owner thread creates and eventually joins one engine-local Tokio runtime.
pub struct RuntimeOwner {
    pub handle: Handle,
    shutdown: Mutex<Option<tokio::sync::oneshot::Sender<()>>>,
    joined: Arc<(Mutex<bool>, Condvar)>,
    thread: Mutex<Option<std::thread::JoinHandle<()>>>,
}
impl RuntimeOwner {
    fn create() -> Result<Self> {
        let (send, recv) = std::sync::mpsc::sync_channel(1);
        let (stop, stopped) = tokio::sync::oneshot::channel();
        let joined = Arc::new((Mutex::new(false), Condvar::new()));
        let done = joined.clone();
        let thread = std::thread::Builder::new()
            .name("nw-db-owner".into())
            .spawn(move || {
                match tokio::runtime::Builder::new_multi_thread()
                    .worker_threads(2)
                    .max_blocking_threads(4)
                    .thread_name("nw-db-worker")
                    .enable_all()
                    .build()
                {
                    Ok(runtime) => {
                        let _ = send.send(Ok(runtime.handle().clone()));
                        runtime.block_on(async {
                            let _ = stopped.await;
                        });
                        drop(runtime);
                    }
                    Err(_) => {
                        let _ = send.send(Err(Error::capacity()));
                    }
                }
                let (lock, changed) = &*done;
                *lock.lock().unwrap_or_else(|e| e.into_inner()) = true;
                changed.notify_all();
            })
            .map_err(|_| Error::capacity())?;
        let handle = recv.recv().map_err(|_| Error::capacity())??;
        Ok(Self {
            handle,
            shutdown: Mutex::new(Some(stop)),
            joined,
            thread: Mutex::new(Some(thread)),
        })
    }
    pub fn request_shutdown(&self) {
        if let Some(stop) = self
            .shutdown
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .take()
        {
            let _ = stop.send(());
        }
    }
    /// Called only by the bounded close-operation waiter, never an ABI release/callback.
    pub fn join(&self) {
        let (lock, changed) = &*self.joined;
        let mut done = lock.lock().unwrap_or_else(|e| e.into_inner());
        while !*done {
            done = changed.wait(done).unwrap_or_else(|e| e.into_inner());
        }
        drop(done);
        if let Some(thread) = self.thread.lock().unwrap_or_else(|e| e.into_inner()).take() {
            let _ = thread.join();
        }
    }
    pub fn is_joined(&self) -> bool {
        *self.joined.0.lock().unwrap_or_else(|e| e.into_inner())
    }
}
impl Drop for RuntimeOwner {
    fn drop(&mut self) {
        self.request_shutdown();
    }
}

pub enum Pool {
    #[cfg(feature = "postgres")]
    Postgres(sqlx::PgPool),
    #[cfg(feature = "sqlite")]
    Sqlite(sqlx::SqlitePool),
}
struct Registry {
    configured: Vec<(u32, Codec)>,
    models: HashMap<[u8; 32], Arc<Model>>,
    projections: HashMap<[u8; 32], ProjectionBinding>,
    bytes: usize,
    next_projection: u32,
}
/// Mandatory source owner; no static executor, pool, connection or application metadata.
pub struct Source {
    pub config: Config,
    pub identity: SourceIdentity,
    pub pool: Pool,
    pub runtime: RuntimeOwner,
    pub sessions: Arc<Semaphore>,
    pub administration: Arc<Semaphore>,
    pub administration_waiting: AtomicUsize,
    pub waiting: AtomicUsize,
    pub pending: AtomicUsize,
    pub close_waiters: AtomicUsize,
    dispatch_stopped: Mutex<bool>,
    pub changed: Notify,
    pub closing: AtomicBool,
    pub retired: AtomicBool,
    pub poisoned: AtomicBool,
    children: Mutex<Vec<Weak<crate::session::Session>>>,
    registry: Mutex<Registry>,
    pub parameters: Arc<ParameterRegistry>,
    pub shapes: Mutex<nativeweave_query_native::ShapeCache>,
    pub stream_budget: nativeweave_data_streaming::Budget,
}
impl Source {
    pub fn create(config: Config) -> Result<Arc<Self>> {
        let runtime = RuntimeOwner::create()?;
        let entered = runtime.handle.enter();
        let pool = match &config.provider {
            #[cfg(feature = "postgres")]
            ProviderConfig::Postgres(p) => Pool::Postgres(
                sqlx::postgres::PgPoolOptions::new()
                    .max_connections(config.limits.connections)
                    .min_connections(0)
                    .acquire_timeout(config.limits.admission.min(p.connect_timeout))
                    .idle_timeout(None)
                    .max_lifetime(None)
                    .connect_lazy_with(p.options()?),
            ),
            #[cfg(feature = "sqlite")]
            ProviderConfig::Sqlite { path, options } => {
                Pool::Sqlite(config.limits.sqlite().pool_options()?.connect_lazy_with(
                    options.connect_options(std::path::Path::new(path), config.limits.sqlite())?,
                ))
            }
        };
        drop(entered);
        let id = NEXT_SOURCE
            .try_update(Ordering::AcqRel, Ordering::Acquire, |x| x.checked_add(1))
            .map_err(|_| Error::capacity())?;
        let identity = SourceIdentity {
            engine: crate::abi::module_identity(),
            source: id,
            generation: 1,
            profile: config.profile,
        };
        let parameters = Arc::new(ParameterRegistry::new(identity, config.limits.nodes)?);
        let stream_bytes = config
            .limits
            .batch_bytes
            .checked_mul(8)
            .and_then(|n| n.checked_mul(config.limits.connections as usize))
            .ok_or_else(Error::capacity)?;
        let stream_budget = nativeweave_data_streaming::Budget::new(
            config.limits.connections as usize,
            stream_bytes,
        )?;
        let sessions = Arc::new(Semaphore::new(config.limits.connections as usize));
        use sha2::{Digest, Sha256};
        let fingerprint: [u8; 32] =
            Sha256::digest(include_bytes!("../../binding/signatures-v1.json")).into();
        let shapes = Mutex::new(nativeweave_query_native::ShapeCache::new(
            config.limits.prepared_plans,
            config.limits.prepared_bytes,
            fingerprint,
        )?);
        let value = Arc::new(Self {
            config,
            identity,
            pool,
            runtime,
            sessions,
            administration: Arc::new(Semaphore::new(1)),
            administration_waiting: AtomicUsize::new(0),
            waiting: AtomicUsize::new(0),
            pending: AtomicUsize::new(0),
            close_waiters: AtomicUsize::new(0),
            dispatch_stopped: Mutex::new(false),
            changed: Notify::new(),
            closing: AtomicBool::new(false),
            retired: AtomicBool::new(false),
            poisoned: AtomicBool::new(false),
            children: Mutex::new(Vec::new()),
            registry: Mutex::new(Registry {
                configured: Vec::new(),
                models: HashMap::new(),
                projections: HashMap::new(),
                bytes: 0,
                next_projection: 1,
            }),
            parameters,
            shapes,
            stream_budget,
        });
        SOURCES.fetch_add(1, Ordering::Relaxed);
        Ok(value)
    }
    pub fn admit(&self) -> Result<()> {
        if self.closing.load(Ordering::Acquire) {
            Err(Error::closed())
        } else if self.poisoned.load(Ordering::Acquire) {
            Err(Error::new(
                nativeweave_interop_abi::NW_ENGINE_POISONED,
                8,
                "database engine poisoned",
            ))
        } else {
            Ok(())
        }
    }
    // The stop decision and new work reservation share a gate. Cleanup after the runtime
    // stopped is permitted only as an already-completed, synchronously polled control.
    pub fn reserve_operation(&self, control: bool) -> Result<bool> {
        let stopped = self
            .dispatch_stopped
            .lock()
            .unwrap_or_else(|e| e.into_inner());
        if !control {
            self.admit()?;
        }
        if *stopped {
            return if control {
                Ok(false)
            } else {
                Err(Error::closed())
            };
        }
        let max = self.config.limits.nodes
            + self.config.limits.connections as usize
            + self.config.limits.waiters;
        self.pending
            .try_update(Ordering::AcqRel, Ordering::Acquire, |n| {
                (n < max).then_some(n + 1)
            })
            .map_err(|_| Error::capacity())?;
        Ok(true)
    }
    pub fn release_operation(&self) {
        self.pending.fetch_sub(1, Ordering::AcqRel);
        self.changed.notify_waiters();
    }
    pub fn add_session(&self, session: &Arc<crate::session::Session>) {
        let mut children = self.children.lock().unwrap_or_else(|e| e.into_inner());
        children.retain(|s| {
            s.upgrade()
                .is_some_and(|s| !s.retired.load(Ordering::Acquire))
        });
        children.push(Arc::downgrade(session));
    }
    pub fn request_close(self: &Arc<Self>) {
        if self.closing.swap(true, Ordering::AcqRel) {
            return;
        }
        self.sessions.close();
        self.administration.close();
        self.changed.notify_waiters();
        self.shapes
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .clear();
        {
            let mut registry = self.registry.lock().unwrap_or_else(|e| e.into_inner());
            registry.models.clear();
            registry.projections.clear();
            registry.configured.clear();
            registry.bytes = 0;
        }
        let children = self
            .children
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .iter()
            .filter_map(Weak::upgrade)
            .collect::<Vec<_>>();
        for session in children {
            session.request_close();
        }
        let source = self.clone();
        self.runtime.handle.spawn(async move {
            match &source.pool {
                #[cfg(feature = "postgres")]
                Pool::Postgres(pool) => pool.close().await,
                #[cfg(feature = "sqlite")]
                Pool::Sqlite(pool) => pool.close().await,
            }
            loop {
                let notified = source.changed.notified();
                tokio::pin!(notified);
                notified.as_mut().enable();
                {
                    let mut stopped = source
                        .dispatch_stopped
                        .lock()
                        .unwrap_or_else(|e| e.into_inner());
                    if source.pending.load(Ordering::Acquire) == 0 {
                        *stopped = true;
                        break;
                    }
                }
                notified.await;
            }
            source.retired.store(true, Ordering::Release);
            source.runtime.request_shutdown();
        });
    }
    pub fn codecs(&self) -> Result<Arc<CodecBindings>> {
        let registry = self.registry.lock().unwrap_or_else(|e| e.into_inner());
        Ok(Arc::new(CodecBindings::new(
            self.config.profile,
            &registry.configured,
        )?))
    }
    pub fn register_codec(&self, id: u32, configuration: Option<String>) -> Result<u32> {
        self.admit()?;
        let codec = Codec {
            id,
            configuration,
            nullable: false,
        };
        codec.validate(self.config.profile)?;
        if codec.configuration.is_none() {
            let _ = CodecBindings::new(self.config.profile, &[])?.get(id, false)?;
            return Ok(id);
        }
        let mut registry = self.registry.lock().unwrap_or_else(|e| e.into_inner());
        self.admit()?;
        if let Some((id, _)) = registry
            .configured
            .iter()
            .find(|(_, c)| c.same_scalar(&codec))
        {
            return Ok(*id);
        }
        let bytes = codec.configuration.as_ref().map_or(0, String::len);
        if registry.configured.len() + registry.models.len() + registry.projections.len()
            >= self.config.limits.nodes
            || registry.bytes + bytes > self.config.limits.parameter_bytes
        {
            return Err(Error::capacity());
        }
        let bound = 1024u32
            .checked_add(registry.configured.len() as u32)
            .ok_or_else(Error::capacity)?;
        registry.configured.push((bound, codec));
        registry.bytes += bytes;
        Ok(bound)
    }
    pub fn register_model(&self, bytes: &[u8]) -> Result<ModelBinding> {
        use sha2::{Digest, Sha256};
        self.admit()?;
        let fields = crate::control::Fields::parse(bytes, 2, &[(1, 5, true), (2, 5, true)])?;
        let canonical = fields.bytes(1)?;
        let hash: [u8; 32] = Sha256::digest(canonical).into();
        if fields.bytes(2)? != hash {
            return Err(Error::invalid("metadata content hash"));
        }
        let parsed = Arc::new(Model::parse(canonical, self.config.profile)?);
        let codecs = self.codecs()?;
        let mut registry = self.registry.lock().unwrap_or_else(|e| e.into_inner());
        self.admit()?;
        if let Some(model) = registry.models.get(&hash) {
            if model.bytes() != canonical {
                return Err(Error::invalid("metadata hash collision"));
            }
            return Ok(ModelBinding::new(self.identity, model.clone(), codecs)?);
        }
        if registry
            .models
            .values()
            .any(|m| m.table_id() == parsed.table_id())
        {
            return Err(Error::invalid("table ID collision"));
        }
        if registry.models.len() + registry.projections.len() + registry.configured.len()
            >= self.config.limits.nodes
            || registry.bytes + canonical.len() > self.config.limits.parameter_bytes
        {
            return Err(Error::capacity());
        }
        let result = ModelBinding::new(self.identity, parsed.clone(), codecs)?;
        registry.bytes += canonical.len();
        registry.models.insert(hash, parsed);
        Ok(result)
    }
    pub fn register_projection(&self, bytes: &[u8]) -> Result<ProjectionBinding> {
        self.admit()?;
        let codecs = self.codecs()?;
        let mut registry = self.registry.lock().unwrap_or_else(|e| e.into_inner());
        self.admit()?;
        let value = nativeweave_query_binding::projection_record(
            bytes,
            self.identity,
            registry.next_projection,
            codecs,
            None,
        )?;
        let hash = value.projection.hash();
        if let Some(previous) = registry.projections.get(&hash) {
            if previous.projection.bytes() != value.projection.bytes() {
                return Err(Error::invalid("projection hash collision"));
            }
            return Ok(previous.clone());
        }
        if registry.models.len() + registry.projections.len() + registry.configured.len()
            >= self.config.limits.nodes
            || registry.bytes + bytes.len() > self.config.limits.parameter_bytes
        {
            return Err(Error::capacity());
        }
        registry.next_projection = registry
            .next_projection
            .checked_add(1)
            .ok_or_else(Error::capacity)?;
        registry.bytes += bytes.len();
        registry.projections.insert(hash, value.clone());
        Ok(value)
    }
    pub fn resolve_projection(&self, binding: ProjectionBinding) -> Result<ProjectionBinding> {
        self.admit()?;
        if binding.source != self.identity {
            return Err(Error::state("mixed projection source"));
        }
        let mut registry = self.registry.lock().unwrap_or_else(|e| e.into_inner());
        self.admit()?;
        let hash = binding.projection.hash();
        if let Some(previous) = registry.projections.get(&hash) {
            if previous.projection.bytes() != binding.projection.bytes() {
                return Err(Error::invalid("projection hash collision"));
            }
            return Ok(previous.clone());
        }
        let bytes = binding.projection.bytes().len();
        if registry.models.len() + registry.projections.len() + registry.configured.len()
            >= self.config.limits.nodes
            || registry.bytes + bytes > self.config.limits.parameter_bytes
        {
            return Err(Error::capacity());
        }
        let value = ProjectionBinding::new(
            self.identity,
            registry.next_projection,
            binding.projection,
            binding.codecs,
        )?;
        registry.next_projection = registry
            .next_projection
            .checked_add(1)
            .ok_or_else(Error::capacity)?;
        registry.bytes += bytes;
        registry.projections.insert(hash, value.clone());
        Ok(value)
    }
    pub fn projection(&self, id: u32) -> Result<ProjectionBinding> {
        self.registry
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .projections
            .values()
            .find(|p| p.id == id)
            .cloned()
            .ok_or_else(|| Error::invalid("unregistered projection"))
    }
}
impl Drop for Source {
    fn drop(&mut self) {
        SOURCES.fetch_sub(1, Ordering::Relaxed);
    }
}
