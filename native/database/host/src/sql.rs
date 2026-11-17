//! A bounded parameter lexer for the explicitly trusted SQL boundary; no SQL AST.
use crate::{
    Error, Result,
    control::{Fields, quote, sequence},
    runtime::Source,
};
use nativeweave_data_contracts::{
    Profile,
    wire::{Batch, Descriptor, Expectation},
};
use nativeweave_query_binding::{ProjectionBinding, RelationBinding};
use sha2::{Digest, Sha256};
use std::{
    collections::{HashMap, HashSet},
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
    },
    time::Duration,
};

pub struct SqlPlan {
    pub source: Arc<Source>,
    pub sql: String,
    pub parameters: Vec<u8>,
    pub expectation: Expectation,
    pub projection: Option<ProjectionBinding>,
    // A relation load retains both exact immutable model/codec roots, even
    // after the creating source removes its registry/cache references.
    pub relation: Option<Arc<RelationBinding>>,
    pub kind: u32,
    pub result_kind: u32,
    pub scalar_count: bool,
    pub timeout: Duration,
    submitted: AtomicBool,
}
impl SqlPlan {
    pub fn parse(
        source: Arc<Source>,
        template: &[u8],
        parameters: &[u8],
        options: &[u8],
    ) -> Result<Self> {
        source.admit()?;
        let limits = &source.config.limits;
        if template.len() > limits.parameter_bytes || parameters.len() > limits.parameter_bytes {
            return Err(Error::capacity());
        }
        let f = Fields::parse(
            options,
            20,
            &[
                (1, 1, true),
                (2, 1, true),
                (3, 3, true),
                (4, 1, true),
                (5, 5, true),
                (6, 1, true),
            ],
        )?;
        let kind = f.u32(1)?;
        let result_kind = f.u32(2)?;
        let projection_id = f.u32(4)?;
        if kind > 2
            || result_kind > 3
            || result_kind == 0 && kind == 0
            || result_kind != 0 && kind != 0
            || result_kind == 0 && projection_id != 0
            || result_kind != 0 && projection_id == 0
        {
            return Err(Error::invalid("command/result kind mismatch"));
        }
        let timeout = Duration::from_nanos(f.u64(3)?);
        if timeout > Duration::from_secs(3600) {
            return Err(Error::invalid("command deadline"));
        }
        let definitions = sequence(f.bytes(5)?)?;
        if definitions.len() > limits.parameters {
            return Err(Error::capacity());
        }
        let codecs = source.codecs()?;
        let mut descriptors = Vec::new();
        let mut names = HashMap::new();
        let mut schema = String::from("{\"version\":1,\"parameters\":[");
        for (index, definition) in definitions.iter().enumerate() {
            let d = Fields::parse(
                definition,
                21,
                &[(1, 1, true), (2, 4, true), (3, 1, true), (4, 1, true)],
            )?;
            let id = d.u32(1)?;
            let name = d.text(2)?;
            if id != index as u32 + 1
                || name.is_empty()
                || name.len() > 128
                || !name.bytes().enumerate().all(|(i, b)| {
                    b == b'_' || b.is_ascii_alphabetic() || i > 0 && b.is_ascii_digit()
                })
                || names.insert(name.to_owned(), id).is_some()
            {
                return Err(Error::invalid("parameter identity"));
            }
            let bound = d.u32(3)?;
            let codec = codecs.get(bound, d.boolean(4)?)?;
            if index > 0 {
                schema.push(',');
            }
            schema.push_str(&format!(
                "{{\"id\":{id},\"name\":{},\"codec\":{},\"codec_config\":{},\"nullable\":{}}}",
                quote(name),
                codec.id,
                codec.configuration.as_deref().unwrap_or("null"),
                codec.nullable
            ));
            descriptors.push(Descriptor {
                id,
                codec,
                bound_codec: bound,
            });
        }
        schema.push_str("]}");
        let expectation = Expectation {
            kind: 1,
            profile: source.config.profile,
            hash: Sha256::digest(schema.as_bytes()).into(),
            entity_id: 0,
            projection_id: 0,
            descriptors,
            exact_rows: Some(1),
        };
        Batch::parse(parameters, expectation.clone())?;
        let sql = match f.u32(6)? {
            0 => {
                if template.contains(&1) || template.contains(&2) {
                    return Err(Error::invalid("reserved SQL template marker"));
                }
                std::str::from_utf8(template)
                    .map_err(|_| Error::invalid("SQL UTF8"))?
                    .to_owned()
            }
            1 => interpolated(template)?,
            _ => return Err(Error::invalid("SQL template kind")),
        };
        let sql = render(&sql, &names, source.config.profile, kind)?;
        let projection = if result_kind == 0 {
            None
        } else {
            Some(source.projection(projection_id)?)
        };
        Ok(Self {
            source,
            sql,
            parameters: parameters.to_vec(),
            expectation,
            projection,
            relation: None,
            kind,
            result_kind,
            scalar_count: false,
            timeout,
            submitted: AtomicBool::new(false),
        })
    }
    pub fn from_rendered(
        source: Arc<Source>,
        rendered: nativeweave_query_native::RenderedQuery,
    ) -> Result<Self> {
        let scalar_count = matches!(rendered.terminal, 4 | 5);
        let result_kind = if matches!(rendered.terminal, 6 | 7) {
            0
        } else if scalar_count {
            1
        } else {
            3
        };
        let projection = if scalar_count {
            let projection = Arc::new(nativeweave_data_contracts::Projection::parse_scalar(br#"{"version":1,"fields":[{"ordinal":0,"name":"","codec":3,"codec_config":null,"nullable":false}]}"#,source.config.profile)?);
            Some(source.resolve_projection(ProjectionBinding::new(
                source.identity,
                1,
                projection,
                source.codecs()?,
            )?)?)
        } else {
            rendered
                .projection
                .map(|p| source.resolve_projection(p))
                .transpose()?
        };
        Ok(Self {
            source,
            sql: rendered.sql,
            parameters: rendered.parameter_bytes,
            expectation: rendered.parameter_expectation,
            projection,
            relation: None,
            kind: if result_kind == 0 { 1 } else { 0 },
            result_kind,
            scalar_count,
            timeout: Duration::from_nanos(rendered.timeout_ns),
            submitted: AtomicBool::new(true),
        })
    }
    pub fn submit(&self, source: &Source) -> Result<()> {
        if self.source.identity != source.identity {
            return Err(Error::state("plan belongs to another source"));
        }
        if self.submitted.swap(true, Ordering::AcqRel) {
            return Err(Error::state("raw plan already submitted"));
        }
        Ok(())
    }
}
fn interpolated(bytes: &[u8]) -> Result<String> {
    if bytes.len() < 4 {
        return Err(Error::invalid("interpolation header"));
    }
    let count = u32::from_le_bytes(bytes[..4].try_into().unwrap()) as usize;
    if count > 8193 {
        return Err(Error::capacity());
    }
    let mut at = 4usize;
    let mut output = String::new();
    for _ in 0..count {
        let h = bytes
            .get(at..at + 8)
            .ok_or_else(|| Error::invalid("interpolation segment"))?;
        let kind = u32::from_le_bytes(h[..4].try_into().unwrap());
        let len = u32::from_le_bytes(h[4..].try_into().unwrap()) as usize;
        at += 8;
        let end = at.checked_add(len).ok_or_else(Error::capacity)?;
        let data = bytes
            .get(at..end)
            .ok_or_else(|| Error::invalid("interpolation range"))?;
        at = end;
        match kind {
            0 => {
                if data.contains(&1) || data.contains(&2) {
                    return Err(Error::invalid("reserved interpolation marker"));
                }
                output.push_str(
                    std::str::from_utf8(data).map_err(|_| Error::invalid("interpolation UTF8"))?,
                );
            }
            1 if len == 4 => {
                let id = u32::from_le_bytes(data.try_into().unwrap());
                if id == 0 {
                    return Err(Error::invalid("interpolation ID"));
                }
                output.push_str(&format!("\u{1}@p{}\u{2}", id - 1));
            }
            _ => return Err(Error::invalid("interpolation segment kind")),
        }
    }
    if at != bytes.len() {
        return Err(Error::invalid("interpolation trailing bytes"));
    }
    Ok(output)
}
fn render(
    input: &str,
    names: &HashMap<String, u32>,
    profile: Profile,
    kind: u32,
) -> Result<String> {
    if input.is_empty() || input.contains('\0') {
        return Err(Error::invalid("SQL template"));
    }
    let bytes = input.as_bytes();
    let mut i = 0;
    let mut output = Vec::with_capacity(bytes.len());
    let mut used = HashSet::new();
    let mut first = None;
    let mut ended = false;
    while i < bytes.len() {
        let b = bytes[i];
        if b.is_ascii_whitespace() {
            output.push(b);
            i += 1;
            continue;
        }
        if i + 1 < bytes.len() && bytes[i..i + 2] == *b"--" {
            let start = i;
            i += 2;
            while i < bytes.len() && bytes[i] != b'\n' {
                i += 1;
            }
            if bytes[start..i].contains(&1) || bytes[start..i].contains(&2) {
                return Err(Error::invalid(
                    "interpolation inside SQL quotation or comment",
                ));
            }
            output.extend_from_slice(&bytes[start..i]);
            continue;
        }
        if i + 1 < bytes.len() && bytes[i..i + 2] == *b"/*" {
            let start = i;
            let mut depth = 1;
            i += 2;
            while i < bytes.len() && depth > 0 {
                if i + 1 < bytes.len() && bytes[i..i + 2] == *b"/*" {
                    depth += 1;
                    i += 2;
                } else if i + 1 < bytes.len() && bytes[i..i + 2] == *b"*/" {
                    depth -= 1;
                    i += 2;
                } else {
                    i += 1;
                }
                if depth > 64 {
                    return Err(Error::capacity());
                }
            }
            if depth != 0 {
                return Err(Error::invalid("unterminated SQL comment"));
            }
            if bytes[start..i].contains(&1) || bytes[start..i].contains(&2) {
                return Err(Error::invalid(
                    "interpolation inside SQL quotation or comment",
                ));
            }
            output.extend_from_slice(&bytes[start..i]);
            continue;
        }
        if ended {
            return Err(Error::invalid("multiple SQL statements"));
        }
        if b == b';' {
            ended = true;
            output.push(b);
            i += 1;
            continue;
        }
        if matches!(b, b'\'' | b'"' | b'`' | b'[') {
            let start = i;
            let end = if b == b'[' { b']' } else { b };
            i += 1;
            let mut closed = false;
            while i < bytes.len() {
                if bytes[i] == end {
                    if i + 1 < bytes.len() && bytes[i + 1] == end {
                        i += 2;
                    } else {
                        i += 1;
                        closed = true;
                        break;
                    }
                } else if bytes[i] == b'\\' && b == b'\'' {
                    return Err(Error::invalid(
                        "ambiguous backslash string requires standard SQL quoting",
                    ));
                } else {
                    i += 1;
                }
            }
            if !closed {
                return Err(Error::invalid("unterminated SQL quotation"));
            }
            if bytes[start..i].contains(&1) || bytes[start..i].contains(&2) {
                return Err(Error::invalid(
                    "interpolation inside SQL quotation or comment",
                ));
            }
            output.extend_from_slice(&bytes[start..i]);
            continue;
        }
        if b == b'$' {
            let start = i;
            let mut end = i + 1;
            while end < bytes.len() && (bytes[end].is_ascii_alphanumeric() || bytes[end] == b'_') {
                end += 1;
            }
            if end < bytes.len() && bytes[end] == b'$' {
                let tag = &bytes[start..=end];
                i = end + 1;
                let mut found = false;
                while i + tag.len() <= bytes.len() {
                    if &bytes[i..i + tag.len()] == tag {
                        i += tag.len();
                        found = true;
                        break;
                    }
                    i += 1;
                }
                if !found {
                    return Err(Error::invalid("unterminated dollar quote"));
                }
                if bytes[start..i].contains(&1) || bytes[start..i].contains(&2) {
                    return Err(Error::invalid(
                        "interpolation inside SQL quotation or comment",
                    ));
                }
                output.extend_from_slice(&bytes[start..i]);
                continue;
            }
            return Err(Error::invalid(
                "provider positional parameter is not a named template",
            ));
        }
        if b == 1 {
            let end = bytes[i + 1..]
                .iter()
                .position(|b| *b == 2)
                .map(|n| i + 1 + n)
                .ok_or_else(|| Error::invalid("interpolation marker"))?;
            if bytes.get(i + 1) != Some(&b'@') {
                return Err(Error::invalid("interpolation parameter"));
            }
            let name = std::str::from_utf8(&bytes[i + 2..end])
                .map_err(|_| Error::invalid("interpolation name"))?;
            let id = *names
                .get(name)
                .ok_or_else(|| Error::invalid("missing interpolation parameter"))?;
            used.insert(id);
            output.extend_from_slice(
                format!(
                    "{}{id}",
                    if profile == Profile::PostgreSql {
                        '$'
                    } else {
                        '?'
                    }
                )
                .as_bytes(),
            );
            i = end + 1;
            continue;
        }
        if b == 2 {
            return Err(Error::invalid("unmatched interpolation marker"));
        }
        if b == b'@' {
            let start = i + 1;
            let mut end = start;
            while end < bytes.len() && (bytes[end].is_ascii_alphanumeric() || bytes[end] == b'_') {
                end += 1;
            }
            let name = std::str::from_utf8(&bytes[start..end])
                .map_err(|_| Error::invalid("parameter name"))?;
            let id = *names
                .get(name)
                .ok_or_else(|| Error::invalid("missing named parameter"))?;
            used.insert(id);
            output.extend_from_slice(
                format!(
                    "{}{id}",
                    if profile == Profile::PostgreSql {
                        '$'
                    } else {
                        '?'
                    }
                )
                .as_bytes(),
            );
            i = end;
            continue;
        }
        if b == b'?' {
            return Err(Error::invalid("untyped positional placeholder"));
        }
        if first.is_none() && (b.is_ascii_alphabetic() || b == b'_') {
            let start = i;
            while i < bytes.len() && (bytes[i].is_ascii_alphanumeric() || bytes[i] == b'_') {
                i += 1;
            }
            first = Some(
                String::from_utf8(bytes[start..i].to_vec())
                    .unwrap()
                    .to_ascii_uppercase(),
            );
            if bytes[start..i].contains(&1) || bytes[start..i].contains(&2) {
                return Err(Error::invalid(
                    "interpolation inside SQL quotation or comment",
                ));
            }
            output.extend_from_slice(&bytes[start..i]);
            continue;
        }
        output.push(b);
        i += 1;
    }
    if used.len() != names.len() {
        return Err(Error::invalid(
            "unused named parameter or interpolation in quoted text",
        ));
    }
    let first = first.ok_or_else(|| Error::invalid("empty SQL statement"))?;
    if matches!(
        first.as_str(),
        "BEGIN" | "COMMIT" | "END" | "ROLLBACK" | "SAVEPOINT" | "RELEASE"
    ) {
        return Err(Error::invalid("use the tracked transaction API"));
    }
    if kind != 2
        && matches!(
            first.as_str(),
            "SET"
                | "RESET"
                | "PRAGMA"
                | "ATTACH"
                | "DETACH"
                | "VACUUM"
                | "DISCARD"
                | "LISTEN"
                | "UNLISTEN"
                | "PREPARE"
                | "DEALLOCATE"
        )
    {
        return Err(Error::invalid(
            "session mutation requires explicit classification",
        ));
    }
    if kind != 0 && matches!(first.as_str(), "SELECT" | "VALUES" | "SHOW" | "EXPLAIN") {
        return Err(Error::invalid("affected-count command has rows"));
    }
    String::from_utf8(output).map_err(|_| Error::invalid("SQL UTF8"))
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn native_placeholder_lexing() {
        let map = HashMap::from([("value".into(), 1)]);
        assert_eq!(
            render(
                "select @value, '@value', @value -- @ignored",
                &map,
                Profile::PostgreSql,
                0
            )
            .unwrap(),
            "select $1, '@value', $1 -- @ignored"
        );
        assert!(render("select '@value'", &map, Profile::Sqlite, 0).is_err());
        assert!(render("select @value; delete from t", &map, Profile::Sqlite, 0).is_err());
        assert!(render("BEGIN", &HashMap::new(), Profile::Sqlite, 2).is_err());
    }
    #[test]
    fn interpolation_holes_cannot_be_hidden_by_quotes_or_comments() {
        let map = HashMap::from([("p0".into(), 1)]);
        let marker = "\u{1}@p0\u{2}";
        assert_eq!(
            render(
                &format!("SELECT {marker}, {marker}"),
                &map,
                Profile::PostgreSql,
                0
            )
            .unwrap(),
            "SELECT $1, $1"
        );
        for statement in [
            format!("SELECT {marker}, '{marker}'"),
            format!("SELECT {marker} -- {marker}"),
            format!("SELECT {marker} /* {marker} */"),
            format!("SELECT {marker}, \"{marker}\""),
            format!("SELECT {marker}, $tag${marker}$tag$"),
        ] {
            assert!(
                render(&statement, &map, Profile::PostgreSql, 0).is_err(),
                "{statement:?}"
            );
        }
    }
}
