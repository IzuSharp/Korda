use base64::{
    Engine,
    engine::general_purpose::{STANDARD, URL_SAFE_NO_PAD},
};
use hmac::{Hmac, KeyInit, Mac};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::{
    fmt, io,
    time::{SystemTime, UNIX_EPOCH},
};
use subtle::ConstantTimeEq;

pub const CREATE: &str = "POST /v1/items";
pub const ATTACH: &str = "PUT /v1/items/{id}/attachment";
pub const MAX_ATTACHMENT: i64 = 64 * 1024 * 1024;
pub type Result<T> = std::result::Result<T, Error>;

#[derive(Debug)]
pub struct Error(pub u16, pub &'static str);
impl fmt::Display for Error {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{} {}", self.0, self.1)
    }
}
impl std::error::Error for Error {}
impl From<io::Error> for Error {
    fn from(_: io::Error) -> Self {
        Self(500, "storage_failure")
    }
}
impl From<sqlx::Error> for Error {
    fn from(error: sqlx::Error) -> Self {
        eprintln!(
            "B23_DATABASE_FAILURE {}",
            match error {
                sqlx::Error::PoolTimedOut => "pool_timeout",
                sqlx::Error::Database(_) => "database",
                _ => "transport_or_codec",
            }
        );
        Self(500, "database_failure")
    }
}
impl From<serde_json::Error> for Error {
    fn from(_: serde_json::Error) -> Self {
        Self(400, "invalid_json")
    }
}

fn yes() -> bool {
    true
}
fn address() -> String {
    "127.0.0.1".into()
}
fn uploads() -> usize {
    4
}
fn disk() -> i64 {
    256 * 1024 * 1024
}
fn grace() -> i64 {
    3600
}
fn request_timeout() -> u64 {
    120
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Config {
    pub provider: String,
    pub database: String,
    pub blob_directory: String,
    pub consumer_database: String,
    pub cursor_key: String,
    pub principals: Vec<Principal>,
    #[serde(default = "address")]
    pub listen_address: String,
    #[serde(default)]
    pub port: u16,
    #[serde(default)]
    pub tls_certificate_pem: Option<String>,
    #[serde(default)]
    pub tls_private_key_pem: Option<String>,
    #[serde(default)]
    pub allow_local_database_tls_disable: bool,
    #[serde(default = "uploads")]
    pub max_uploads: usize,
    #[serde(default = "disk")]
    pub max_blob_bytes: i64,
    #[serde(default = "grace")]
    pub orphan_grace_seconds: i64,
    #[serde(default = "request_timeout")]
    pub request_timeout_seconds: u64,
    #[serde(default = "yes")]
    pub run_workers: bool,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Principal {
    pub token: String,
    pub tenant: String,
    pub principal: String,
    #[serde(default = "yes")]
    pub can_write: bool,
}
#[derive(Clone)]
pub struct Identity {
    pub tenant: String,
    pub principal: String,
    pub can_write: bool,
}
impl Config {
    pub fn validate(&self) -> Result<()> {
        if !matches!(self.provider.as_str(), "sqlite" | "postgresql")
            || self.database.is_empty()
            || self.consumer_database.is_empty()
            || self.blob_directory.is_empty()
            || self.listen_address.parse::<std::net::IpAddr>().is_err()
            || !(1..=16).contains(&self.max_uploads)
            || self.max_blob_bytes < MAX_ATTACHMENT
            || !(60..=604800).contains(&self.orphan_grace_seconds)
            || !(1..=3600).contains(&self.request_timeout_seconds)
            || self.tls_certificate_pem.is_some() != self.tls_private_key_pem.is_some()
            || !(1..=128).contains(&self.principals.len())
            || !(32..=64).contains(
                &STANDARD
                    .decode(&self.cursor_key)
                    .map_err(|_| Error(400, "invalid_configuration"))?
                    .len(),
            )
        {
            return Err(Error(400, "invalid_configuration"));
        }
        let mut tokens = std::collections::HashSet::new();
        for principal in &self.principals {
            if !(16..=256).contains(&principal.token.len())
                || !principal.token.bytes().all(|x| (b'!'..=b'~').contains(&x))
                || !tokens.insert(&principal.token)
                || [&principal.tenant, &principal.principal].iter().any(|x| {
                    x.trim().is_empty() || x.len() > 128 || x.chars().any(char::is_control)
                })
            {
                return Err(Error(400, "invalid_identity"));
            }
        }
        if self.provider == "sqlite" && self.database == self.consumer_database {
            return Err(Error(400, "consumer_must_be_independent"));
        }
        Ok(())
    }
}
pub struct Authentication(Vec<([u8; 32], Identity)>);
impl Authentication {
    pub fn new(config: &Config) -> Self {
        Self(
            config
                .principals
                .iter()
                .map(|x| {
                    (
                        Sha256::digest(x.token.as_bytes()).into(),
                        Identity {
                            tenant: x.tenant.clone(),
                            principal: x.principal.clone(),
                            can_write: x.can_write,
                        },
                    )
                })
                .collect(),
        )
    }
    pub fn authenticate(&self, headers: &http::HeaderMap) -> Result<Identity> {
        let value = header(headers, "authorization")
            .map_err(|_| Error(401, "unauthenticated"))?
            .ok_or(Error(401, "unauthenticated"))?;
        let token = value
            .strip_prefix("Bearer ")
            .filter(|x| x.len() <= 256)
            .ok_or(Error(401, "unauthenticated"))?;
        let hash: [u8; 32] = Sha256::digest(token.as_bytes()).into();
        let mut found = None;
        for (candidate, identity) in &self.0 {
            if bool::from(candidate.ct_eq(&hash)) {
                found = Some(identity.clone());
            }
        }
        found.ok_or(Error(401, "unauthenticated"))
    }
}
pub fn header<'a>(headers: &'a http::HeaderMap, name: &str) -> Result<Option<&'a str>> {
    let mut values = headers.get_all(name).iter();
    let first = values.next();
    if values.next().is_some() {
        return Err(Error(400, "ambiguous_header"));
    }
    first
        .map(|x| x.to_str().map_err(|_| Error(400, "invalid_header")))
        .transpose()
}
pub fn now() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .expect("clock after epoch")
        .as_millis() as i64
}
pub fn uuid() -> String {
    uuid::Uuid::new_v4().to_string()
}
pub fn id(value: &str) -> Result<&str> {
    if uuid::Uuid::parse_str(value).is_ok_and(|x| x.to_string() == value) {
        Ok(value)
    } else {
        Err(Error(400, "invalid_identifier"))
    }
}
pub fn key(value: Option<&str>) -> Result<String> {
    let value = value.ok_or(Error(400, "invalid_idempotency_key"))?;
    if !(1..=128).contains(&value.len()) || !value.bytes().all(|x| (b'!'..=b'~').contains(&x)) {
        return Err(Error(400, "invalid_idempotency_key"));
    }
    Ok(value.into())
}
pub fn title(value: &str) -> Result<()> {
    if value.trim().is_empty() || value.len() > 1024 || value.contains('\0') {
        Err(Error(400, "invalid_title"))
    } else {
        Ok(())
    }
}
pub fn hex(bytes: &[u8]) -> String {
    const DIGITS: &[u8] = b"0123456789abcdef";
    let mut out = String::with_capacity(bytes.len() * 2);
    for b in bytes {
        out.push(DIGITS[(b >> 4) as usize] as char);
        out.push(DIGITS[(b & 15) as usize] as char);
    }
    out
}
pub fn hash(bytes: &[u8]) -> String {
    hex(&Sha256::digest(bytes))
}
pub fn frame(fields: &[&str], integers: &[i64]) -> Vec<u8> {
    let mut output = Vec::with_capacity(
        4 + fields.iter().map(|x| 4 + x.len()).sum::<usize>() + 8 * integers.len(),
    );
    output.extend_from_slice(&1u32.to_le_bytes());
    for field in fields {
        output.extend_from_slice(&(field.len() as u32).to_le_bytes());
        output.extend_from_slice(field.as_bytes());
    }
    for value in integers {
        output.extend_from_slice(&value.to_le_bytes());
    }
    output
}
pub fn scope(identity: &Identity, semantic: &str) -> String {
    hash(&frame(
        &["scope.v1", &identity.tenant, &identity.principal, semantic],
        &[],
    ))
}
pub fn create_hash(identity: &Identity, title: &str) -> String {
    hash(&frame(
        &[CREATE, &identity.tenant, &identity.principal, title],
        &[],
    ))
}
pub fn attachment_hash(
    identity: &Identity,
    item: &str,
    digest: &str,
    length: i64,
    version: i64,
) -> String {
    hash(&frame(
        &[ATTACH, &identity.tenant, &identity.principal, item, digest],
        &[length, version],
    ))
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct CreateItem {
    pub title: String,
}
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CreatedItem {
    pub response_version: i64,
    pub operation_id: String,
    pub item_id: String,
    pub title: String,
    pub created_at: i64,
    pub version: i64,
}
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AttachedItem {
    pub response_version: i64,
    pub operation_id: String,
    pub item_id: String,
    pub content_hash: String,
    pub content_length: i64,
    pub version: i64,
}
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ItemView {
    pub item_id: String,
    pub title: String,
    pub created_at: i64,
    pub version: i64,
}
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ItemPage {
    pub items: Vec<ItemView>,
    pub next_cursor: Option<String>,
}
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Problem<'a> {
    pub code: &'a str,
    pub operation_id: Option<&'a str>,
    pub recovery_uri: Option<String>,
    pub correlation_id: Option<u64>,
}
pub struct Replay {
    pub status: u16,
    pub bytes: Vec<u8>,
    pub operation_id: String,
}
pub struct Operation {
    pub scope: String,
    pub key: String,
    pub hash: String,
    pub operation_id: String,
    pub tenant: String,
    pub principal: String,
    pub semantic: String,
    pub response_version: i64,
    pub status: i64,
    pub bytes: Vec<u8>,
    pub completed_at: i64,
}
pub struct Attachment {
    pub hash: String,
    pub length: i64,
    pub version: i64,
}

// Match the accepted System.Text.Json default safe-ASCII wire encoding. Field
// order comes from explicit structs. This is serializer configuration, not a
// precomputed-response path; golden bytes exercise this same serializer.
struct WireFormatter;
impl serde_json::ser::Formatter for WireFormatter {
    fn write_string_fragment<W: io::Write + ?Sized>(
        &mut self,
        w: &mut W,
        s: &str,
    ) -> io::Result<()> {
        for c in s.chars() {
            if c.is_ascii() && !matches!(c, '\'' | '&' | '+' | '<' | '>' | '`' | '\u{7f}') {
                w.write_all(&[c as u8])?;
            } else {
                for unit in c.encode_utf16(&mut [0; 2]) {
                    write!(w, "\\u{unit:04X}")?;
                }
            }
        }
        Ok(())
    }
    fn write_char_escape<W: io::Write + ?Sized>(
        &mut self,
        w: &mut W,
        c: serde_json::ser::CharEscape,
    ) -> io::Result<()> {
        use serde_json::ser::CharEscape::*;
        match c {
            Quote => w.write_all(b"\\u0022"),
            AsciiControl(value) => write!(w, "\\u{value:04X}"),
            _ => serde_json::ser::CompactFormatter.write_char_escape(w, c),
        }
    }
}
pub fn json<T: Serialize>(value: &T) -> Vec<u8> {
    let mut bytes = Vec::new();
    value
        .serialize(&mut serde_json::Serializer::with_formatter(
            &mut bytes,
            WireFormatter,
        ))
        .expect("bounded concrete DTO serializes");
    bytes
}
pub fn problem(code: &str, operation: Option<&str>) -> Vec<u8> {
    json(&Problem {
        code,
        operation_id: operation,
        recovery_uri: operation.map(|x| format!("/v1/operations/{x}")),
        correlation_id: None,
    })
}

pub struct Cursor(Vec<u8>);
impl Cursor {
    pub fn new(config: &Config) -> Self {
        Self(
            STANDARD
                .decode(&config.cursor_key)
                .expect("validated cursor key"),
        )
    }
    pub fn encode(&self, identity: &Identity, created: i64, item: &str) -> String {
        let mut bytes = frame(
            &["cursor.v1", &identity.tenant, &identity.principal, item],
            &[created],
        );
        let mut mac = Hmac::<Sha256>::new_from_slice(&self.0).expect("HMAC accepts key");
        mac.update(&bytes);
        bytes.extend_from_slice(&mac.finalize().into_bytes());
        URL_SAFE_NO_PAD.encode(bytes)
    }
    pub fn decode(&self, identity: &Identity, text: &str) -> Result<(i64, String)> {
        let bad = || Error(400, "invalid_cursor");
        if text.len() > 2048 {
            return Err(bad());
        }
        let bytes = URL_SAFE_NO_PAD.decode(text).map_err(|_| bad())?;
        if bytes.len() < 36 {
            return Err(bad());
        }
        let (data, signature) = bytes.split_at(bytes.len() - 32);
        let mut mac = Hmac::<Sha256>::new_from_slice(&self.0).expect("HMAC accepts key");
        mac.update(data);
        mac.verify_slice(signature).map_err(|_| bad())?;
        if data[..4] != 1u32.to_le_bytes() {
            return Err(bad());
        }
        let mut offset = 4;
        let mut field = || -> Result<String> {
            let count = u32::from_le_bytes(
                data.get(offset..offset + 4)
                    .ok_or_else(bad)?
                    .try_into()
                    .map_err(|_| bad())?,
            ) as usize;
            offset += 4;
            let value = std::str::from_utf8(data.get(offset..offset + count).ok_or_else(bad)?)
                .map_err(|_| bad())?
                .to_owned();
            offset += count;
            Ok(value)
        };
        if field()? != "cursor.v1" || field()? != identity.tenant || field()? != identity.principal
        {
            return Err(bad());
        }
        let item = field()?;
        id(&item).map_err(|_| bad())?;
        if offset + 8 != data.len() {
            return Err(bad());
        }
        Ok((
            i64::from_le_bytes(data[offset..].try_into().map_err(|_| bad())?),
            item,
        ))
    }
}

pub fn golden() -> serde_json::Value {
    let inputs = [
        ("ascii", "tenant-a", "alice", "hello"),
        ("unicode", "tenant-a", "alice", "世界 🦀"),
        ("composed", "tenant-a", "alice", "é"),
        ("decomposed", "tenant-a", "alice", "e\u{301}"),
        ("field-boundary-a", "ab", "c", "title"),
        ("field-boundary-b", "a", "bc", "title"),
    ];
    let create:Vec<_>=inputs.iter().map(|(name,tenant,principal,title)|{let bytes=frame(&[CREATE,tenant,principal,title],&[]);serde_json::json!({"name":name,"tenant":tenant,"principal":principal,"title":title,"bytes":hex(&bytes),"sha256":hash(&bytes)})}).collect();
    let identity = Identity {
        tenant: "tenant-a".into(),
        principal: "alice".into(),
        can_write: true,
    };
    let item = "00112233-4455-6677-8899-aabbccddeeff";
    let response_item = "11223344-5566-7788-99aa-bbccddeeff00";
    let digest = hash(b"abc");
    let created = json(&CreatedItem {
        response_version: 1,
        operation_id: item.into(),
        item_id: response_item.into(),
        title: "世界 🦀".into(),
        created_at: 1700000000123,
        version: 1,
    });
    let attached = json(&AttachedItem {
        response_version: 1,
        operation_id: "22334455-6677-8899-aabb-ccddeeff0011".into(),
        item_id: response_item.into(),
        content_hash: digest.clone(),
        content_length: 3,
        version: 2,
    });
    serde_json::json!({"version":1,"create":create,"attachment":{"tenant":"tenant-a","principal":"alice","itemId":item,"contentSha256":digest,"contentLength":3,"expectedVersion":7,"sha256":attachment_hash(&identity,item,&digest,3,7)},"responseCreate":{"bytes":hex(&created),"sha256":hash(&created)},"responseAttachment":{"bytes":hex(&attached),"sha256":hash(&attached)}})
}
