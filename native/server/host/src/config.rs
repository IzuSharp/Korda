use crate::wire::*;
use crate::{ApiError, Result, invalid};
use http::{HeaderMap, HeaderName, HeaderValue, Method};
use nativeweave_interop_abi::NwBytesViewV1;
use std::{
    collections::HashSet,
    net::{IpAddr, Ipv4Addr, Ipv6Addr, SocketAddr},
    sync::{Arc, RwLock},
    time::Duration,
};

pub struct Config {
    pub _request_timeout: Duration,
    pub _shutdown_timeout: Duration,
    pub header_timeout: Duration,
    pub keepalive: Duration,
    pub _max_request_bytes: u64,
    pub _max_json_bytes: u32,
    pub max_header_bytes: usize,
    pub max_chunk_bytes: usize,
    pub max_connections: usize,
    pub max_h2_streams: u32,
    pub active_requests: usize,
    pub queued_requests: usize,
    pub native_payload_bytes: u64,
    pub managed_payload_bytes: u64,
    pub request_retained_bytes: u64,
    pub listeners: Vec<Listener>,
    pub trusted_proxies: HashSet<IpAddr>,
    pub forward_limit: usize,
}
pub struct Listener {
    pub name: String,
    pub address: SocketAddr,
    pub protocols: u32,
    pub tls: RwLock<Option<Arc<TlsSnapshot>>>,
}
pub struct TlsSnapshot {
    pub config: Arc<rustls::ServerConfig>,
    pub handshake_timeout: Duration,
    pub handshakes: Arc<tokio::sync::Semaphore>,
}
#[derive(Clone)]
pub struct Route {
    pub id: u64,
    pub kind: u32,
    pub canonical: String,
    pub parameters: Vec<String>,
    pub methods: Vec<Method>,
    pub timeout: Duration,
    pub max_request_bytes: u64,
    pub max_json_bytes: u32,
    pub mcp: Option<Arc<nativeweave_protocol_mcp::profile::Profile>>,
}

/// Caller guarantees live ABI storage. Bounds are checked before forming slices.
pub unsafe fn bytes<'a>(value: NwBytesViewV1, maximum: usize) -> Result<&'a [u8]> {
    if value.length > maximum as u64 || (value.length != 0 && value.data.is_null()) {
        return Err(invalid());
    }
    if value.length == 0 {
        return Ok(&[]);
    }
    // SAFETY: the ABI caller supplies the declared live, nonoverlapping input range.
    Ok(unsafe { std::slice::from_raw_parts(value.data, value.length as usize) })
}
pub unsafe fn text(value: NwBytesViewV1, maximum: usize) -> Result<String> {
    // SAFETY: forwarded validated ABI input contract.
    let data = unsafe { bytes(value, maximum)? };
    let text = std::str::from_utf8(data).map_err(|_| invalid())?;
    if text.contains('\0') {
        return Err(invalid());
    }
    Ok(text.to_owned())
}
pub unsafe fn record<T: Copy>(value: *const T) -> Result<T> {
    if value.is_null() || !value.is_aligned() {
        return Err(invalid());
    }
    // SAFETY: only the caller-declared readable prefix is examined before the complete copy.
    if unsafe { value.cast::<u32>().read() } < size_of::<T>() as u32 {
        return Err(invalid());
    }
    // SAFETY: truthful ABI capacity and alignment establish the full readable record.
    Ok(unsafe { value.read() })
}
pub fn duration(ns: u64, zero: bool) -> Result<Duration> {
    if (!zero && ns == 0) || ns > 86_400_000_000_000 {
        return Err(invalid());
    }
    Ok(Duration::from_nanos(ns))
}
pub fn normalized(ip: IpAddr) -> IpAddr {
    match ip {
        IpAddr::V6(x) => x.to_ipv4_mapped().map(IpAddr::V4).unwrap_or(ip),
        _ => ip,
    }
}

pub unsafe fn config(value: *const NwServerConfigV1) -> Result<Config> {
    // SAFETY: native create borrows a complete fixed record for this synchronous call.
    let c = unsafe { record(value)? };
    if c.version != 1
        || c.reserved != 0
        || c.listener_count == 0
        || c.listeners.is_null()
        || !c.listeners.is_aligned()
        || c.max_request_bytes == 0
        || c.max_json_bytes == 0
        || c.max_json_bytes as u64 > c.max_request_bytes
        || !(1024..=1_048_576).contains(&c.max_header_bytes)
        || c.max_chunk_bytes == 0
        || c.max_connections == 0
        || c.max_h2_streams == 0
        || c.active_requests == 0
        || c.max_chunk_bytes as u64
            > c.native_payload_bytes
                .min(c.managed_payload_bytes)
                .min(c.request_retained_bytes)
        || (c.max_json_bytes as u64)
            .checked_mul(3)
            .and_then(|x| x.checked_add(c.max_chunk_bytes as u64))
            .is_none_or(|x| x > c.managed_payload_bytes.min(c.request_retained_bytes))
        || !(1..=16).contains(&c.forward_limit)
    {
        return Err(invalid());
    }
    let mut listeners = Vec::new();
    let mut names = HashSet::new();
    // SAFETY: the array length is bounded above and the caller supplies all records until return.
    for v in unsafe { std::slice::from_raw_parts(c.listeners, c.listener_count as usize) } {
        // SAFETY: v is one live complete input element.
        let l = unsafe { listener(v)? };
        if !names.insert(l.name.clone()) {
            return Err(invalid());
        }
        listeners.push(l);
    }
    // SAFETY: caller supplies bounded immutable list bytes until create returns.
    let proxies = decode(unsafe { bytes(c.trusted_proxies, 16_384)? }, 256, 0, 64)?;
    let mut trusted_proxies = HashSet::new();
    for (name, value) in proxies {
        let ip = normalized(value.parse().map_err(|_| invalid())?);
        if !name.is_empty() || !trusted_proxies.insert(ip) {
            return Err(invalid());
        }
    }
    Ok(Config {
        _request_timeout: duration(c.request_timeout_ns, false)?,
        _shutdown_timeout: duration(c.shutdown_timeout_ns, true)?,
        header_timeout: duration(c.header_timeout_ns, false)?,
        keepalive: duration(c.keepalive_ns, false)?,
        _max_request_bytes: c.max_request_bytes,
        _max_json_bytes: c.max_json_bytes,
        max_header_bytes: c.max_header_bytes as usize,
        max_chunk_bytes: c.max_chunk_bytes as usize,
        max_connections: c.max_connections as usize,
        max_h2_streams: c.max_h2_streams,
        active_requests: c.active_requests as usize,
        queued_requests: c.queued_requests as usize,
        native_payload_bytes: c.native_payload_bytes,
        managed_payload_bytes: c.managed_payload_bytes,
        request_retained_bytes: c.request_retained_bytes,
        listeners,
        trusted_proxies,
        forward_limit: c.forward_limit as usize,
    })
}
pub unsafe fn listener(value: *const NwServerListenerV1) -> Result<Listener> {
    // SAFETY: caller borrows the fixed listener record synchronously.
    let v = unsafe { record(value)? };
    if v.version != 1
        || v.reserved != 0
        || v.flags & !3 != 0
        || !(1..=3).contains(&v.protocols)
        || v.port > 65535
        || (v.protocols == 3 && v.flags & 1 == 0)
        || (v.flags & 2 != 0 && v.flags & 1 == 0)
    {
        return Err(invalid());
    }
    let ip = match v.address_family {
        4 if v.address[4..] == [0; 12] => IpAddr::V4(Ipv4Addr::new(
            v.address[0],
            v.address[1],
            v.address[2],
            v.address[3],
        )),
        6 => IpAddr::V6(Ipv6Addr::from(v.address)),
        _ => return Err(invalid()),
    };
    // SAFETY: all string views are bounded call borrows, copied before return.
    let name = unsafe { text(v.name, 128)? };
    if name.is_empty() || name.chars().any(char::is_control) {
        return Err(invalid());
    }
    let tls = if v.flags & 1 != 0 {
        // SAFETY: The fixed listener was copied from a complete live record; its borrowed file views remain valid until this synchronous validation returns.
        Some(unsafe { tls(&v)? })
    } else {
        if v.certificate_chain_file.length != 0
            || v.private_key_file.length != 0
            || v.client_ca_file.length != 0
        {
            return Err(invalid());
        }
        None
    };
    Ok(Listener {
        name,
        address: SocketAddr::new(ip, v.port as u16),
        protocols: v.protocols,
        tls: RwLock::new(tls),
    })
}
pub unsafe fn tls(value: &NwServerListenerV1) -> Result<Arc<TlsSnapshot>> {
    let handshake_timeout = duration(value.handshake_timeout_ns, false)?;
    if value.max_handshakes == 0 {
        return Err(invalid());
    }
    // SAFETY: all file names are copied from bounded ABI input before filesystem access.
    let (cert, key, ca) = unsafe {
        (
            text(value.certificate_chain_file, 4096)?,
            text(value.private_key_file, 4096)?,
            text(value.client_ca_file, 4096)?,
        )
    };
    fn load(path: &str) -> Result<Vec<u8>> {
        let meta = std::fs::metadata(path).map_err(|_| ApiError::domain(5))?;
        if !meta.is_file() || meta.len() > 4 * 1024 * 1024 {
            return Err(ApiError::domain(5));
        }
        std::fs::read(path).map_err(|_| ApiError::domain(5))
    }
    let cert = load(&cert)?;
    let key = load(&key)?;
    let certs = rustls_pemfile::certs(&mut &cert[..])
        .collect::<std::result::Result<Vec<_>, _>>()
        .map_err(|_| ApiError::domain(5))?;
    let key = rustls_pemfile::private_key(&mut &key[..])
        .map_err(|_| ApiError::domain(5))?
        .ok_or(ApiError::domain(5))?;
    let provider = Arc::new(rustls::crypto::ring::default_provider());
    let builder = rustls::ServerConfig::builder_with_provider(provider.clone())
        .with_safe_default_protocol_versions()
        .map_err(|_| ApiError::domain(5))?;
    let builder = if value.flags & 2 != 0 {
        if ca.is_empty() {
            return Err(ApiError::domain(5));
        }
        let ca = load(&ca)?;
        let mut roots = rustls::RootCertStore::empty();
        for cert in rustls_pemfile::certs(&mut &ca[..]) {
            roots
                .add(cert.map_err(|_| ApiError::domain(5))?)
                .map_err(|_| ApiError::domain(5))?;
        }
        let verifier =
            rustls::server::WebPkiClientVerifier::builder_with_provider(Arc::new(roots), provider)
                .build()
                .map_err(|_| ApiError::domain(5))?;
        builder.with_client_cert_verifier(verifier)
    } else {
        if !ca.is_empty() {
            return Err(invalid());
        }
        builder.with_no_client_auth()
    };
    let mut result = builder
        .with_single_cert(certs, key)
        .map_err(|_| ApiError::domain(5))?;
    result.alpn_protocols = match value.protocols {
        1 => vec![b"http/1.1".to_vec()],
        2 => vec![b"h2".to_vec()],
        3 => vec![b"h2".to_vec(), b"http/1.1".to_vec()],
        _ => return Err(invalid()),
    };
    Ok(Arc::new(TlsSnapshot {
        config: Arc::new(result),
        handshake_timeout,
        handshakes: Arc::new(tokio::sync::Semaphore::new(value.max_handshakes as usize)),
    }))
}

pub fn decode(
    data: &[u8],
    count_limit: usize,
    name_limit: usize,
    value_limit: usize,
) -> Result<Vec<(String, String)>> {
    fn number(data: &[u8], offset: &mut usize) -> Result<usize> {
        let end = offset.checked_add(4).ok_or(invalid())?;
        let n = u32::from_le_bytes(
            data.get(*offset..end)
                .ok_or(invalid())?
                .try_into()
                .map_err(|_| invalid())?,
        ) as usize;
        *offset = end;
        Ok(n)
    }
    let mut offset = 0;
    let count = number(data, &mut offset)?;
    if count > count_limit {
        return Err(invalid());
    }
    let mut result = Vec::with_capacity(count);
    for _ in 0..count {
        let n = number(data, &mut offset)?;
        let v = number(data, &mut offset)?;
        if n > name_limit || v > value_limit {
            return Err(invalid());
        }
        let end = offset
            .checked_add(n)
            .and_then(|x| x.checked_add(v))
            .ok_or(invalid())?;
        let span = data.get(offset..end).ok_or(invalid())?;
        let name = std::str::from_utf8(&span[..n]).map_err(|_| invalid())?;
        let value = std::str::from_utf8(&span[n..]).map_err(|_| invalid())?;
        if name.contains('\0') || value.contains('\0') {
            return Err(invalid());
        }
        result.push((name.to_owned(), value.to_owned()));
        offset = end;
    }
    if offset != data.len() {
        return Err(invalid());
    }
    Ok(result)
}
pub fn encode<'a>(values: impl IntoIterator<Item = (&'a str, &'a str)>) -> Vec<u8> {
    let mut bytes = vec![0; 4];
    let mut count = 0u32;
    for (name, value) in values {
        bytes.extend_from_slice(&(name.len() as u32).to_le_bytes());
        bytes.extend_from_slice(&(value.len() as u32).to_le_bytes());
        bytes.extend_from_slice(name.as_bytes());
        bytes.extend_from_slice(value.as_bytes());
        count += 1;
    }
    bytes[..4].copy_from_slice(&count.to_le_bytes());
    bytes
}
pub fn encode_headers(headers: &HeaderMap) -> Result<Vec<u8>> {
    let mut fields = Vec::with_capacity(headers.len());
    for (name, value) in headers {
        fields.push((name.as_str(), value.to_str().map_err(|_| invalid())?));
    }
    Ok(encode(fields))
}
pub fn headers(data: &[u8], maximum: usize, trailers: bool) -> Result<HeaderMap> {
    if data.len() > maximum {
        return Err(invalid());
    }
    let fields = decode(data, 200, 256, maximum)?;
    let mut result = HeaderMap::new();
    for (name, value) in fields {
        let name = HeaderName::from_bytes(name.as_bytes()).map_err(|_| invalid())?;
        if [
            "transfer-encoding",
            "connection",
            "keep-alive",
            "proxy-connection",
            "upgrade",
            "http2-settings",
        ]
        .contains(&name.as_str())
            || (trailers && ["content-length", "host", "trailer"].contains(&name.as_str()))
        {
            return Err(invalid());
        }
        let value = HeaderValue::from_str(&value).map_err(|_| invalid())?;
        result.append(name, value);
    }
    Ok(result)
}
pub unsafe fn route(value: *const NwServerRouteV1) -> Result<Route> {
    // SAFETY: routes_set copies one complete caller-owned record before return.
    let v = unsafe { record(value)? };
    if v.version != 1 || v.reserved != 0 || v.flags & !1 != 0 || v.handler_id == 0 {
        return Err(invalid());
    }
    if v.kind != 0 && v.kind != 2 {
        return Err(ApiError::status(nativeweave_interop_abi::NW_NOT_SUPPORTED));
    }
    // SAFETY: bounded registration text is copied while borrowed.
    let (pattern, name, methods) = unsafe {
        (
            text(v.pattern, 4096)?,
            text(v.endpoint_name, 128)?,
            decode(bytes(v.methods, 8192)?, 32, 0, 256)?,
        )
    };
    if !pattern.starts_with('/')
        || pattern.contains(['?', '#'])
        || name.chars().any(char::is_control)
        || (v.kind == 0 && v.protocol_descriptor.length != 0)
        || methods.is_empty()
    {
        return Err(invalid());
    }
    let mut parameters = Vec::new();
    let mut canonical = Vec::new();
    let parts: Vec<_> = pattern.split('/').collect();
    for (i, part) in parts.iter().enumerate() {
        if part.contains(['{', '}']) {
            let parameter = part
                .strip_prefix('{')
                .and_then(|x| x.strip_suffix('}'))
                .ok_or(invalid())?;
            let (catch, name) = parameter
                .strip_prefix('*')
                .map(|x| (true, x))
                .unwrap_or((false, parameter));
            if name.is_empty()
                || !name
                    .bytes()
                    .next()
                    .is_some_and(|x| x.is_ascii_alphabetic() || x == b'_')
                || !name.bytes().all(|x| x.is_ascii_alphanumeric() || x == b'_')
                || parameters.iter().any(|x| x == name)
                || (catch && i + 1 != parts.len())
            {
                return Err(invalid());
            }
            canonical.push(format!(
                "{{{}nw{}}}",
                if catch { "*" } else { "" },
                parameters.len()
            ));
            parameters.push(name.to_owned());
        } else {
            canonical.push((*part).to_owned());
        }
    }
    let mut parsed = Vec::new();
    for (name, method) in methods {
        if !name.is_empty() {
            return Err(invalid());
        }
        let m = Method::from_bytes(method.as_bytes()).map_err(|_| invalid())?;
        if parsed.contains(&m) {
            return Err(invalid());
        }
        parsed.push(m);
    }
    if v.max_request_bytes == 0
        || (v.kind == 0 && v.max_json_bytes == 0)
        || v.max_json_bytes as u64 > v.max_request_bytes
    {
        return Err(invalid());
    }
    let mcp = if v.kind == 2 {
        if !parameters.is_empty() || parsed != [Method::POST] || v.max_json_bytes != 0 {
            return Err(invalid());
        }
        // SAFETY: exact bounded registration bytes are consumed synchronously.
        let profile = nativeweave_protocol_mcp::profile::Profile::parse(
            unsafe { bytes(v.protocol_descriptor, 4 * 1024 * 1024)? },
            v.handler_id,
        )
        .map_err(|_| invalid())?;
        if profile.limits.wire as u64 != v.max_request_bytes {
            return Err(invalid());
        }
        Some(profile)
    } else {
        None
    };
    Ok(Route {
        id: v.handler_id,
        kind: v.kind,
        canonical: canonical.join("/"),
        parameters,
        methods: parsed,
        timeout: duration(v.timeout_ns, false)?,
        max_request_bytes: v.max_request_bytes,
        max_json_bytes: v.max_json_bytes,
        mcp,
    })
}
