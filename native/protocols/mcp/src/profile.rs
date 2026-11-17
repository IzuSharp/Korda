//! Exact runtime registration metadata. No native symbols or executable code occur here.
use crate::preflight::Limits;
use http::{HeaderMap, Method, StatusCode, Uri};
use rmcp::model::{Prompt, PromptArgument, Resource, Tool};
use serde::Deserialize;
use serde_json::{Map, Value};
use std::{collections::HashSet, sync::Arc};

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Descriptor {
    version: u32,
    kind: String,
    pub handler_id: u64,
    protocol_version: String,
    legacy_sessions: bool,
    event_store: bool,
    strict_metadata: bool,
    pub allowed_hosts: Vec<String>,
    pub allowed_origins: Vec<String>,
    max_request_bytes: usize,
    max_json_depth: usize,
    max_json_nodes: usize,
    max_json_string_bytes: usize,
    pub tools: Vec<ToolEntry>,
    pub resources: Vec<ResourceEntry>,
    pub prompts: Vec<PromptEntry>,
}
#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
pub struct ToolEntry {
    pub name: String,
    pub description: String,
    pub handler_id: u64,
    input_codec_id: u64,
    output_codec_id: u64,
    pub input_schema: Map<String, Value>,
    pub output_schema: Map<String, Value>,
}
#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
pub struct ResourceEntry {
    pub uri: String,
    pub name: String,
    pub mime_type: String,
    pub description: Option<String>,
    pub handler_id: u64,
}
#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
pub struct PromptEntry {
    pub name: String,
    pub description: String,
    pub handler_id: u64,
    pub arguments: Vec<Argument>,
}
#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Argument {
    pub name: String,
    pub description: Option<String>,
    pub required: bool,
}
pub struct Profile {
    pub descriptor: Descriptor,
    pub limits: Limits,
    pub tool_models: Vec<Tool>,
    pub resource_models: Vec<Resource>,
    pub prompt_models: Vec<Prompt>,
    pub metadata_bytes: usize,
}
fn text(value: &str, maximum: usize, empty: bool) -> bool {
    (empty || !value.is_empty()) && value.len() <= maximum && !value.contains('\0')
}
fn name(value: &str) -> bool {
    text(value, 128, false)
        && value
            .bytes()
            .all(|b| b.is_ascii_alphanumeric() || matches!(b, b'_' | b'-' | b'.'))
}
pub fn authority(value: &str) -> bool {
    let Ok(uri) = format!("http://{value}/").parse::<Uri>() else {
        return false;
    };
    uri.authority().is_some_and(|a| {
        a.as_str() == value
            && a.port_u16().is_some()
            && !a.host().contains('*')
            && !a.host().contains('@')
    })
}
pub fn origin(value: &str) -> bool {
    let Ok(uri) = value.parse::<Uri>() else {
        return false;
    };
    matches!(uri.scheme_str(), Some("http" | "https"))
        && uri.authority().is_some_and(|a| authority(a.as_str()))
        && uri.path_and_query().is_none_or(|p| p.as_str() == "/")
        && !value.ends_with('/')
}
impl Profile {
    pub fn parse(bytes: &[u8], route: u64) -> Result<Arc<Self>, &'static str> {
        if bytes.is_empty() || bytes.len() > 4 * 1024 * 1024 {
            return Err("registry metadata limit");
        }
        let d: Descriptor =
            serde_json::from_slice(bytes).map_err(|_| "invalid exact MCP descriptor")?;
        let limits = Limits {
            wire: d.max_request_bytes,
            depth: d.max_json_depth,
            nodes: d.max_json_nodes,
            strings: d.max_json_string_bytes,
        };
        if d.version != 1
            || d.kind != "mcp"
            || d.handler_id != route
            || route == 0
            || d.protocol_version != "2026-07-28"
            || d.legacy_sessions
            || d.event_store
            || !d.strict_metadata
            || !limits.validate()
            || d.tools.len() > 1024
            || d.resources.len() > 1024
            || d.prompts.len() > 1024
            || d.allowed_hosts.len() > 256
            || d.allowed_origins.len() > 256
            || !d.allowed_hosts.iter().all(|s| authority(s))
            || !d.allowed_origins.iter().all(|s| origin(s))
        {
            return Err("unsupported MCP profile");
        }
        let mut ids = HashSet::new();
        ids.insert(route);
        let mut names = HashSet::new();
        let mut codecs = HashSet::new();
        let mut tool_models = Vec::new();
        for t in &d.tools {
            if !name(&t.name)
                || !text(&t.description, 4096, true)
                || !names.insert(&t.name)
                || t.handler_id == 0
                || !ids.insert(t.handler_id)
                || t.input_codec_id == 0
                || t.output_codec_id == 0
                || !codecs.insert(t.input_codec_id)
                || !codecs.insert(t.output_codec_id)
            {
                return Err("invalid/duplicate tool identity");
            }
            check_schema(&t.input_schema)?;
            check_schema(&t.output_schema)?;
            let mut model = Tool::new(
                t.name.clone(),
                t.description.clone(),
                Arc::new(t.input_schema.clone()),
            );
            model.output_schema = Some(Arc::new(t.output_schema.clone()));
            tool_models.push(model);
        }
        let mut uris = HashSet::new();
        let mut resource_models = Vec::new();
        for r in &d.resources {
            if !text(&r.uri, 4096, false)
                || !r.uri.contains(':')
                || !name(&r.name)
                || !text(&r.mime_type, 256, false)
                || r.description.as_ref().is_some_and(|v| !text(v, 4096, true))
                || !uris.insert(&r.uri)
                || r.handler_id == 0
                || !ids.insert(r.handler_id)
            {
                return Err("invalid/duplicate resource identity");
            }
            let mut model =
                Resource::new(r.uri.clone(), r.name.clone()).with_mime_type(r.mime_type.clone());
            model.description = r.description.clone();
            resource_models.push(model);
        }
        let mut names = HashSet::new();
        let mut prompt_models = Vec::new();
        for p in &d.prompts {
            if !name(&p.name)
                || !text(&p.description, 4096, true)
                || !names.insert(&p.name)
                || p.handler_id == 0
                || !ids.insert(p.handler_id)
                || p.arguments.len() > 256
            {
                return Err("invalid/duplicate prompt identity");
            }
            let mut args = HashSet::new();
            let mut models = Vec::new();
            for a in &p.arguments {
                if !name(&a.name)
                    || !args.insert(&a.name)
                    || a.description.as_ref().is_some_and(|v| !text(v, 4096, true))
                {
                    return Err("invalid prompt argument");
                }
                let mut model = PromptArgument::new(a.name.clone()).with_required(a.required);
                model.description = a.description.clone();
                models.push(model);
            }
            prompt_models.push(Prompt::new(
                p.name.clone(),
                Some(p.description.clone()),
                Some(models),
            ));
        }
        Ok(Arc::new(Self {
            descriptor: d,
            limits,
            tool_models,
            resource_models,
            prompt_models,
            metadata_bytes: bytes.len(),
        }))
    }
    /// Runs before a body is polled or managed authorization is dispatched.
    /// The original wire authority is supplied, never a forwarded host value.
    pub fn headers(
        &self,
        method: &Method,
        headers: &HeaderMap,
        original_authority: Option<&str>,
        bound_hosts: &[String],
    ) -> Result<(), StatusCode> {
        if *method != Method::POST {
            return Err(StatusCode::METHOD_NOT_ALLOWED);
        }
        let singleton = |key: &str| {
            let mut all = headers.get_all(key).iter();
            let value = all.next()?.to_str().ok()?;
            if all.next().is_some() {
                None
            } else {
                Some(value)
            }
        };
        let host = singleton("host")
            .or(original_authority)
            .ok_or(StatusCode::BAD_REQUEST)?;
        if headers.get_all("host").iter().count() > 1 {
            return Err(StatusCode::BAD_REQUEST);
        }
        let hosts = if self.descriptor.allowed_hosts.is_empty() {
            bound_hosts
        } else {
            &self.descriptor.allowed_hosts
        };
        if !hosts.iter().any(|value| value.eq_ignore_ascii_case(host)) {
            return Err(StatusCode::FORBIDDEN);
        }
        if headers.contains_key("origin")
            && !singleton("origin").is_some_and(|v| {
                self.descriptor
                    .allowed_origins
                    .iter()
                    .any(|allowed| allowed == v)
            })
        {
            return Err(StatusCode::FORBIDDEN);
        }
        if !singleton("content-type").is_some_and(|v| {
            v.split(';')
                .next()
                .is_some_and(|v| v.trim().eq_ignore_ascii_case("application/json"))
        }) {
            return Err(StatusCode::UNSUPPORTED_MEDIA_TYPE);
        }
        for required in ["application/json", "text/event-stream"] {
            if !headers
                .get_all("accept")
                .iter()
                .filter_map(|v| v.to_str().ok())
                .flat_map(|v| v.split(','))
                .any(|v| {
                    v.split(';')
                        .next()
                        .is_some_and(|v| v.trim().eq_ignore_ascii_case(required))
                })
            {
                return Err(StatusCode::NOT_ACCEPTABLE);
            }
        }
        if headers.get_all("content-length").iter().count() > 1
            || singleton("content-length")
                .is_some_and(|v| v.parse::<usize>().map_or(true, |n| n > self.limits.wire))
        {
            return Err(StatusCode::PAYLOAD_TOO_LARGE);
        }
        Ok(())
    }
}

fn check_schema(schema: &Map<String, Value>) -> Result<(), &'static str> {
    if schema.get("type").and_then(Value::as_str) != Some("object")
        || serde_json::to_vec(schema)
            .map_err(|_| "schema encoding")?
            .len()
            > 65536
    {
        return Err("schema root or byte limit");
    }
    let root = Value::Object(schema.clone());
    schema_node(&root, &root, 0, &mut Vec::new())
}
fn schema_node(
    node: &Value,
    root: &Value,
    depth: usize,
    path: &mut Vec<String>,
) -> Result<(), &'static str> {
    if depth > 16 {
        return Err("schema nesting/recursive reference");
    }
    let obj = node.as_object().ok_or("open schema")?;
    if let Some(reference) = obj.get("$ref") {
        let reference = reference.as_str().ok_or("invalid schema reference")?;
        if !reference.starts_with("#/") || path.iter().any(|p| p == reference) {
            return Err("recursive/external schema reference");
        }
        path.push(reference.into());
        let result = schema_node(
            root.pointer(&reference[1..])
                .ok_or("missing schema reference")?,
            root,
            depth + 1,
            path,
        );
        path.pop();
        return result;
    }
    for key in obj.keys() {
        if !matches!(
            key.as_str(),
            "$schema"
                | "type"
                | "properties"
                | "required"
                | "items"
                | "additionalProperties"
                | "description"
                | "title"
                | "default"
                | "format"
                | "enum"
                | "minimum"
                | "maximum"
                | "pattern"
        ) {
            return Err("unsupported static schema keyword");
        }
    }
    let types: Vec<&str> = match obj.get("type") {
        Some(Value::String(v)) => vec![v],
        Some(Value::Array(v)) => v
            .iter()
            .map(|v| v.as_str().ok_or("invalid schema type"))
            .collect::<Result<_, _>>()?,
        _ => return Err("open schema type"),
    };
    if types.is_empty()
        || types.iter().any(|v| {
            !matches!(
                *v,
                "object" | "array" | "string" | "number" | "integer" | "boolean" | "null"
            )
        })
    {
        return Err("unsupported schema type");
    }
    if types.contains(&"object") {
        let props = obj
            .get("properties")
            .and_then(Value::as_object)
            .ok_or("open object schema")?;
        if props.len() > 256
            || obj
                .get("additionalProperties")
                .is_some_and(|v| v != &Value::Bool(false))
        {
            return Err("open object schema");
        }
        for value in props.values() {
            schema_node(value, root, depth + 1, path)?;
        }
        if let Some(required) = obj.get("required") {
            for name in required.as_array().ok_or("invalid required names")? {
                if !name.as_str().is_some_and(|n| props.contains_key(n)) {
                    return Err("unknown required name");
                }
            }
        }
    }
    if types.contains(&"array") {
        schema_node(
            obj.get("items").ok_or("open array schema")?,
            root,
            depth + 1,
            path,
        )?;
    }
    Ok(())
}
/// Finite static DTO-shape validation; this is deliberately not a general JSON
/// Schema engine. Serializer-specific scalar formats are checked by the static
/// managed codec before its typed business delegate is invoked.
pub fn conforms(value: &Value, schema: &Map<String, Value>) -> bool {
    conforms_node(value, schema, schema, 0)
}
fn conforms_node(
    value: &Value,
    s: &Map<String, Value>,
    root: &Map<String, Value>,
    depth: usize,
) -> bool {
    if depth > 32 {
        return false;
    }
    if let Some(reference) = s.get("$ref").and_then(Value::as_str) {
        let Some(path) = reference.strip_prefix("#/") else {
            return false;
        };
        let mut parts = path.split('/');
        let Some(first) = parts.next() else {
            return false;
        };
        let key = first.replace("~1", "/").replace("~0", "~");
        let Some(mut next) = root.get(&key) else {
            return false;
        };
        for part in parts {
            let key = part.replace("~1", "/").replace("~0", "~");
            let Some(found) = next.get(&key) else {
                return false;
            };
            next = found;
        }
        return next
            .as_object()
            .is_some_and(|next| conforms_node(value, next, root, depth + 1));
    }
    let matches = |name: &str| match name {
        "null" => value.is_null(),
        "object" => value.is_object(),
        "array" => value.is_array(),
        "string" => value.is_string(),
        "boolean" => value.is_boolean(),
        "number" => value.is_number(),
        "integer" => value.as_i64().is_some() || value.as_u64().is_some(),
        _ => false,
    };
    if !match s.get("type") {
        Some(Value::String(v)) => matches(v),
        Some(Value::Array(v)) => v.iter().any(|v| v.as_str().is_some_and(matches)),
        _ => false,
    } {
        return false;
    }
    if s.get("enum")
        .and_then(Value::as_array)
        .is_some_and(|values| !values.contains(value))
    {
        return false;
    }
    if let Some(obj) = value.as_object() {
        let Some(props) = s.get("properties").and_then(Value::as_object) else {
            return false;
        };
        if s.get("required")
            .and_then(Value::as_array)
            .is_some_and(|names| {
                names
                    .iter()
                    .any(|name| !name.as_str().is_some_and(|name| obj.contains_key(name)))
            })
        {
            return false;
        }
        for (name, value) in obj {
            if let Some(shape) = props.get(name) {
                if !shape
                    .as_object()
                    .is_some_and(|shape| conforms_node(value, shape, root, depth + 1))
                {
                    return false;
                }
            } else if s.get("additionalProperties") == Some(&Value::Bool(false)) {
                return false;
            }
        }
    }
    if let Some(array) = value.as_array() {
        let Some(shape) = s.get("items").and_then(Value::as_object) else {
            return false;
        };
        if !array
            .iter()
            .all(|v| conforms_node(v, shape, root, depth + 1))
        {
            return false;
        }
    }
    true
}
