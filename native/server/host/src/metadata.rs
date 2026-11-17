use crate::{
    ApiError, Result,
    config::{self, Config, Route},
    invalid,
    wire::NwServerRequestInfoV1,
};
use http::{HeaderMap, Request, Version};
use nativeweave_interop_abi::NwBytesViewV1;
use std::{
    net::{IpAddr, SocketAddr},
    sync::{
        Arc,
        atomic::{AtomicU32, Ordering},
    },
    time::Instant,
};

pub struct Head {
    pub id: u64,
    pub handler: u64,
    pub kind: u32,
    pub tls: bool,
    pub deadline: Instant,
    pub content_length: Option<u64>,
    pub method: String,
    pub scheme: String,
    pub authority: String,
    pub path: String,
    pub query: String,
    pub headers: Vec<u8>,
    pub routes: Vec<u8>,
    pub queries: Vec<u8>,
    pub trace: Vec<u8>,
    pub trace_state: Vec<u8>,
    pub protocol: u32,
    pub peer: SocketAddr,
    pub cancellation: Arc<AtomicU32>,
}
pub struct Metadata {
    pub head: Option<Arc<Head>>,
    pub block: Vec<u8>,
}
pub fn view(bytes: &[u8]) -> NwBytesViewV1 {
    NwBytesViewV1 {
        data: if bytes.is_empty() {
            std::ptr::null()
        } else {
            bytes.as_ptr()
        },
        length: bytes.len() as u64,
    }
}
impl Head {
    pub fn wire(&self) -> NwServerRequestInfoV1 {
        let mut address = [0u8; 16];
        let family = match self.peer.ip() {
            IpAddr::V4(ip) => {
                address[..4].copy_from_slice(&ip.octets());
                4
            }
            IpAddr::V6(ip) => {
                address = ip.octets();
                6
            }
        };
        NwServerRequestInfoV1 {
            struct_size: size_of::<NwServerRequestInfoV1>() as u32,
            version: 1,
            request_id: self.id,
            handler_id: self.handler,
            remaining_timeout_ns: self
                .deadline
                .saturating_duration_since(Instant::now())
                .as_nanos()
                .min(u64::MAX as u128) as u64,
            kind: self.kind,
            flags: u32::from(self.content_length.is_some()) | (u32::from(self.tls) << 1),
            content_length: self.content_length.unwrap_or(0),
            method: view(self.method.as_bytes()),
            scheme: view(self.scheme.as_bytes()),
            authority: view(self.authority.as_bytes()),
            path: view(self.path.as_bytes()),
            raw_query: view(self.query.as_bytes()),
            headers: view(&self.headers),
            route_values: view(&self.routes),
            query_values: view(&self.queries),
            trace_context: view(&self.trace),
            trace_state: view(&self.trace_state),
            protocol: self.protocol,
            peer_family: family,
            peer_address: address,
            peer_port: self.peer.port() as u32,
            cancellation_status: self.cancellation.load(Ordering::Acquire),
        }
    }
}
pub fn validate_request<B>(request: &Request<B>, config: &Config) -> Result<()> {
    let target = request
        .uri()
        .path_and_query()
        .map(|x| x.as_str())
        .unwrap_or("/");
    if target.len() > 16_384
        || request.headers().len() > 200
        || request.headers().keys().any(|x| x.as_str().len() > 256)
    {
        return Err(invalid());
    }
    let size = request
        .headers()
        .iter()
        .try_fold(0usize, |n, (name, value)| {
            n.checked_add(name.as_str().len() + value.as_bytes().len() + 8)
        })
        .ok_or(invalid())?;
    if size + 4 > config.max_header_bytes {
        return Err(invalid());
    }
    for (_, value) in request.headers() {
        let text = value.to_str().map_err(|_| invalid())?;
        if text.bytes().any(|x| x == 0 || x == b'\r' || x == b'\n') {
            return Err(invalid());
        }
    }
    Ok(())
}
pub fn head<B>(
    request: &Request<B>,
    route: &Route,
    config: &Config,
    id: u64,
    peer: SocketAddr,
    tls: bool,
    parameters: Vec<(String, String)>,
    deadline: Instant,
) -> Result<Arc<Head>> {
    validate_request(request, config)?;
    let content_length = match request.headers().get(http::header::CONTENT_LENGTH) {
        Some(v) => Some(
            v.to_str()
                .map_err(|_| invalid())?
                .parse::<u64>()
                .map_err(|_| invalid())?,
        ),
        None => None,
    };
    if content_length.is_some_and(|n| n > route.max_request_bytes) {
        return Err(ApiError::domain(2));
    }
    let original_authority = request
        .uri()
        .authority()
        .map(|x| x.as_str())
        .or_else(|| {
            request
                .headers()
                .get(http::header::HOST)
                .and_then(|x| x.to_str().ok())
        })
        .unwrap_or("");
    let (scheme, authority) = forwarded(
        request.headers(),
        config,
        peer.ip(),
        if tls { "https" } else { "http" },
        original_authority,
    )?;
    let raw = request.uri().query().unwrap_or("");
    validate_percent(raw)?;
    let pairs: Vec<(String, String)> = form_urlencoded::parse(raw.as_bytes())
        .map(|(a, b)| (a.into_owned(), b.into_owned()))
        .collect();
    if pairs.len() > 1024
        || pairs
            .iter()
            .any(|(a, b)| a.len() > 256 || a.contains('\0') || b.contains('\0'))
        || pairs.iter().map(|(a, b)| a.len() + b.len()).sum::<usize>() > 16_384
    {
        return Err(invalid());
    }
    let params: Vec<(String, String)> = route
        .parameters
        .iter()
        .enumerate()
        .map(|(i, name)| {
            let key = format!("nw{i}");
            (
                name.clone(),
                parameters
                    .iter()
                    .find(|(k, _)| k == &key)
                    .map(|(_, v)| v.clone())
                    .unwrap_or_default(),
            )
        })
        .collect();
    if params.iter().any(|(_, v)| v.contains('\0')) {
        return Err(invalid());
    }
    Ok(Arc::new(Head {
        id,
        handler: route.id,
        kind: route.kind,
        tls,
        deadline,
        content_length,
        method: request.method().to_string(),
        scheme,
        authority,
        path: request.uri().path().to_owned(),
        query: if raw.is_empty() {
            String::new()
        } else {
            format!("?{raw}")
        },
        headers: config::encode_headers(request.headers())?,
        routes: config::encode(params.iter().map(|(a, b)| (a.as_str(), b.as_str()))),
        queries: config::encode(pairs.iter().map(|(a, b)| (a.as_str(), b.as_str()))),
        trace: Vec::new(),
        trace_state: Vec::new(),
        protocol: if request.version() == Version::HTTP_2 {
            2
        } else {
            1
        },
        peer,
        cancellation: Arc::new(AtomicU32::new(0)),
    }))
}
pub fn validate_percent(value: &str) -> Result<()> {
    let bytes = value.as_bytes();
    let mut i = 0;
    while i < bytes.len() {
        if bytes[i] == b'%' {
            if i + 2 >= bytes.len()
                || !bytes[i + 1].is_ascii_hexdigit()
                || !bytes[i + 2].is_ascii_hexdigit()
            {
                return Err(invalid());
            }
            i += 3;
        } else {
            i += 1;
        }
    }
    if percent_encoding::percent_decode(bytes)
        .decode_utf8()
        .is_err()
    {
        return Err(invalid());
    }
    Ok(())
}
fn authority(value: &str) -> Result<()> {
    if value.is_empty()
        || !value.is_ascii()
        || value
            .bytes()
            .any(|x| x.is_ascii_whitespace() || b"@/\\#?%".contains(&x))
    {
        return Err(invalid());
    }
    let a: value::Authority = value.parse().map_err(|_| invalid())?;
    if a.host().is_empty() || a.port().is_some_and(|p| p.as_u16() == 0) {
        return Err(invalid());
    }
    Ok(())
}
mod value {
    pub type Authority = http::uri::Authority;
}
fn forwarded(
    headers: &HeaderMap,
    config: &Config,
    peer: IpAddr,
    scheme: &str,
    host: &str,
) -> Result<(String, String)> {
    if !config.trusted_proxies.contains(&config::normalized(peer)) {
        return Ok((scheme.to_owned(), host.to_owned()));
    }
    let names = ["x-forwarded-for", "x-forwarded-proto", "x-forwarded-host"];
    let mut fields = Vec::new();
    for name in names {
        let values: Vec<_> = headers.get_all(name).iter().collect();
        if values.len() > 1 {
            return Err(invalid());
        }
        fields.push(values.first().copied());
    }
    if fields.iter().all(Option::is_none) {
        return Ok((scheme.to_owned(), host.to_owned()));
    }
    if fields.iter().any(Option::is_none) {
        return Err(invalid());
    }
    let mut lists = Vec::new();
    for field in fields {
        lists.push(
            field
                .unwrap()
                .to_str()
                .map_err(|_| invalid())?
                .split(',')
                .map(|x| x.trim_matches([' ', '\t']))
                .collect::<Vec<_>>(),
        );
    }
    let count = lists[0].len();
    if !(1..=16).contains(&count) || lists.iter().any(|x| x.len() != count) {
        return Err(invalid());
    }
    let mut ips = Vec::with_capacity(count);
    for i in 0..count {
        let text = lists[0][i];
        if text.contains('%') {
            return Err(invalid());
        }
        let text = text
            .strip_prefix('[')
            .and_then(|x| x.strip_suffix(']'))
            .unwrap_or(text);
        ips.push(config::normalized(
            text.parse::<IpAddr>().map_err(|_| invalid())?,
        ));
        if !["http", "https"].contains(&lists[1][i]) {
            return Err(invalid());
        }
        authority(lists[2][i])?;
    }
    let mut current = config::normalized(peer);
    let mut scheme = scheme.to_owned();
    let mut host = host.to_owned();
    for i in (0..count).rev().take(config.forward_limit) {
        if !config.trusted_proxies.contains(&current) {
            break;
        }
        current = ips[i];
        scheme = lists[1][i].to_owned();
        host = lists[2][i].to_owned();
    }
    Ok((scheme, host))
}
