use crate::protocol::{
    self, Attachment, Config, Error, Identity, MAX_ATTACHMENT, Operation, Result,
};
use futures_util::StreamExt;
use serde::Serialize;
use sha2::{Digest, Sha256};
use std::{
    collections::HashSet,
    fs::{self, File, OpenOptions},
    io::{Read, Write},
    path::{Path, PathBuf},
    sync::{Arc, Mutex},
};
use tokio::io::{AsyncReadExt, AsyncWriteExt};

#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct UploadManifest {
    pub version: i64,
    pub operation_id: String,
    pub tenant: String,
    pub principal: String,
    pub item_id: String,
    pub request_key: String,
    pub expected_version: i64,
    pub content_hash: Option<String>,
    pub content_length: i64,
    pub state: String,
    pub created_at: i64,
}
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct RecoveryManifest<'a> {
    version: i64,
    operation_id: &'a str,
    scope: &'a str,
    request_key: &'a str,
    request_hash: &'a str,
    created_at: i64,
}
struct Budget {
    active: HashSet<String>,
    reserved: i64,
}
pub struct Blobs {
    root: PathBuf,
    staging: PathBuf,
    blobs: PathBuf,
    recovery: PathBuf,
    _owner: File,
    gate: Mutex<Budget>,
    max_uploads: usize,
    max_bytes: i64,
}
pub struct Upload {
    owner: Arc<Blobs>,
    pub manifest: UploadManifest,
    reservation: i64,
    binding: bool,
    complete: bool,
}

pub fn directory_sync(path: &Path) -> Result<()> {
    File::open(path)?.sync_all()?;
    Ok(())
}
fn valid_hash(hash: &str) -> bool {
    hash.len() == 64
        && hash
            .bytes()
            .all(|x| x.is_ascii_digit() || (b'a'..=b'f').contains(&x))
}
fn remove(path: &Path) -> Result<()> {
    match fs::remove_file(path) {
        Ok(()) => Ok(()),
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(()),
        Err(error) => Err(error.into()),
    }
}

impl Blobs {
    pub fn new(config: &Config) -> Result<Arc<Self>> {
        if !cfg!(target_os = "linux") {
            return Err(Error(500, "filesystem_profile_not_implemented"));
        }
        let root = PathBuf::from(&config.blob_directory);
        fs::create_dir_all(&root)?;
        let owner = OpenOptions::new()
            .read(true)
            .write(true)
            .create(true)
            .truncate(false)
            .open(root.join("owner.lock"))?;
        owner
            .try_lock()
            .map_err(|_| Error(503, "blob_root_owned"))?;
        let staging = root.join("staging");
        let blobs = root.join("blobs");
        let recovery = root.join("recovery");
        for path in [&staging, &blobs, &recovery] {
            fs::create_dir_all(path)?;
        }
        directory_sync(&root)?;
        Ok(Arc::new(Self {
            root,
            staging,
            blobs,
            recovery,
            _owner: owner,
            gate: Mutex::new(Budget {
                active: HashSet::new(),
                reserved: 0,
            }),
            max_uploads: config.max_uploads,
            max_bytes: config.max_blob_bytes,
        }))
    }
    fn stored(&self) -> Result<i64> {
        let mut total = 0i64;
        for directory in [&self.root, &self.staging, &self.blobs, &self.recovery] {
            for entry in fs::read_dir(directory)? {
                let entry = entry?;
                match entry.metadata() {
                    Ok(value) if value.is_file() => {
                        total = total
                            .checked_add(value.len() as i64)
                            .ok_or(Error(503, "disk_capacity"))?
                    }
                    Ok(_) => {}
                    Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
                    Err(error) => return Err(error.into()),
                }
            }
        }
        Ok(total)
    }
    fn count_json(path: &Path) -> Result<usize> {
        Ok(fs::read_dir(path)?
            .filter_map(|x| x.ok())
            .filter(|x| x.path().extension().is_some_and(|x| x == "json"))
            .take(8192)
            .count())
    }
    fn write_atomic<T: Serialize>(&self, path: &Path, value: &T) -> Result<()> {
        let bytes = protocol::json(value);
        if bytes.len() > 8192 {
            return Err(Error(500, "manifest_capacity"));
        }
        let temporary = path.with_extension("json.new");
        let mut file = OpenOptions::new()
            .create(true)
            .truncate(true)
            .write(true)
            .open(&temporary)?;
        file.write_all(&bytes)?;
        file.sync_all()?;
        drop(file);
        fs::rename(&temporary, path)?;
        directory_sync(path.parent().expect("manifest directory"))
    }
    fn manifest_path(&self, operation: &str) -> PathBuf {
        self.staging.join(format!("{operation}.json"))
    }
    fn part_path(&self, operation: &str) -> PathBuf {
        self.staging.join(format!("{operation}.part"))
    }
    fn write_manifest(&self, manifest: &UploadManifest) -> Result<()> {
        let _gate = self.gate.lock().expect("blob gate");
        self.write_atomic(&self.manifest_path(&manifest.operation_id), manifest)
    }
    pub fn begin(
        self: &Arc<Self>,
        identity: &Identity,
        item: &str,
        key: &str,
        version: i64,
        length: i64,
    ) -> Result<Upload> {
        if !(0..=MAX_ATTACHMENT).contains(&length) {
            return Err(Error(413, "attachment_limit"));
        }
        let operation = protocol::uuid();
        let reservation = length + 16384;
        {
            let mut gate = self.gate.lock().expect("blob gate");
            if gate.active.len() >= self.max_uploads
                || self.stored()? + gate.reserved + reservation + 8192 > self.max_bytes
                || Self::count_json(&self.staging)? >= 8192
            {
                return Err(Error(503, "upload_capacity"));
            }
            gate.active.insert(operation.clone());
            gate.reserved += reservation;
        }
        let manifest = UploadManifest {
            version: 1,
            operation_id: operation,
            tenant: identity.tenant.clone(),
            principal: identity.principal.clone(),
            item_id: item.into(),
            request_key: key.into(),
            expected_version: version,
            content_hash: None,
            content_length: length,
            state: "uploading".into(),
            created_at: protocol::now(),
        };
        let upload = Upload {
            owner: self.clone(),
            manifest,
            reservation,
            binding: false,
            complete: false,
        };
        self.write_manifest(&upload.manifest)?;
        Ok(upload)
    }
    pub fn protect(&self, operation: &Operation) -> Result<()> {
        let gate = self.gate.lock().expect("blob gate");
        if self.stored()? + gate.reserved + 16384 > self.max_bytes
            || Self::count_json(&self.recovery)? >= 8192
        {
            return Err(Error(503, "recovery_capacity"));
        }
        self.write_atomic(
            &self
                .recovery
                .join(format!("{}.json", operation.operation_id)),
            &RecoveryManifest {
                version: 1,
                operation_id: &operation.operation_id,
                scope: &operation.scope,
                request_key: &operation.key,
                request_hash: &operation.hash,
                created_at: operation.completed_at,
            },
        )
    }
    pub fn confirm(&self, operation: &str, upload: bool) {
        let result = (|| -> Result<()> {
            let _gate = self.gate.lock().expect("blob gate");
            let directory = if upload {
                &self.staging
            } else {
                &self.recovery
            };
            let path = directory.join(format!("{operation}.json"));
            remove(&path)?;
            remove(&path.with_extension("json.new"))?;
            directory_sync(directory)
        })();
        if result.is_err() {
            eprintln!("B23_PROTECTION_CLEANUP_PENDING");
        }
    }
    pub async fn open(&self, attachment: &Attachment) -> Result<tokio::fs::File> {
        if !valid_hash(&attachment.hash) {
            return Err(Error(500, "invalid_blob_hash"));
        }
        let file = tokio::fs::File::open(self.blobs.join(&attachment.hash)).await?;
        if file.metadata().await?.len() != attachment.length as u64 {
            return Err(Error(500, "blob_length_mismatch"));
        }
        Ok(file)
    }
}
impl Upload {
    pub async fn stage(&mut self, body: axum::body::Body) -> Result<()> {
        let part = self.owner.part_path(&self.manifest.operation_id);
        let file = tokio::fs::OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&part)
            .await?;
        let mut file = tokio::io::BufWriter::with_capacity(32768, file);
        let stream = body
            .into_data_stream()
            .map(|x| x.map_err(std::io::Error::other));
        let mut reader = tokio_util::io::StreamReader::new(stream);
        let mut buffer = vec![0u8; 32768];
        let mut digest = Sha256::new();
        let mut count = 0i64;
        loop {
            let read = reader.read(&mut buffer).await?;
            if read == 0 {
                break;
            }
            count += read as i64;
            if count > self.manifest.content_length || count > MAX_ATTACHMENT {
                return Err(Error(413, "attachment_limit"));
            }
            digest.update(&buffer[..read]);
            file.write_all(&buffer[..read]).await?;
        }
        if count != self.manifest.content_length {
            return Err(Error(400, "content_length_mismatch"));
        }
        file.flush().await?;
        file.get_ref().sync_all().await?;
        drop(file);
        let hash = protocol::hex(&digest.finalize());
        self.manifest.content_hash = Some(hash.clone());
        self.manifest.state = "prepared".into();
        self.owner.write_manifest(&self.manifest)?;
        // Hash protection is durable before making the immutable blob visible.
        let destination = self.owner.blobs.join(&hash);
        {
            let _gate = self.owner.gate.lock().expect("blob gate");
            if destination.exists() {
                if fs::metadata(&destination)?.len() != count as u64 {
                    return Err(Error(500, "immutable_blob_collision"));
                }
                let mut existing = File::open(&destination)?;
                let mut actual = Sha256::new();
                let mut chunk = [0u8; 32768];
                loop {
                    let n = existing.read(&mut chunk)?;
                    if n == 0 {
                        break;
                    }
                    actual.update(&chunk[..n]);
                }
                if protocol::hex(&actual.finalize()) != hash {
                    return Err(Error(500, "immutable_blob_collision"));
                }
                remove(&part)?;
            } else {
                fs::rename(&part, &destination)?;
            }
            directory_sync(&self.owner.blobs)?;
            directory_sync(&self.owner.staging)?;
        }
        self.manifest.state = "durable".into();
        self.owner.write_manifest(&self.manifest)?;
        Ok(())
    }
    pub fn begin_binding(&mut self) {
        self.binding = true;
    }
    pub fn complete(&mut self, status: u16) {
        if status != 503 {
            self.owner.confirm(&self.manifest.operation_id, true);
            self.complete = true;
        }
    }
}
impl Drop for Upload {
    fn drop(&mut self) {
        if !self.binding && !self.complete {
            self.owner.confirm(&self.manifest.operation_id, true);
            let _ = remove(&self.owner.part_path(&self.manifest.operation_id));
        }
        let mut gate = self.owner.gate.lock().expect("blob gate");
        gate.active.remove(&self.manifest.operation_id);
        gate.reserved -= self.reservation;
    }
}
