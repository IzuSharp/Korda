//! A bounded statement-prefix guard, not a SQL parser or dialect renderer.
//! The provider parses trusted scripts. This guard prohibits escaping the tracked
//! transaction with explicit transaction control outside quoted/comment regions.
use crate::{Error, Result};
pub fn validate(sql: &str) -> Result<()> {
    let bytes = sql.as_bytes();
    let mut at = 0;
    let mut prefix = true;
    while at < bytes.len() {
        if bytes[at].is_ascii_whitespace() {
            at += 1;
            continue;
        }
        if bytes[at..].starts_with(b"--") {
            at += 2;
            while at < bytes.len() && bytes[at] != b'\n' {
                at += 1;
            }
            continue;
        }
        if bytes[at..].starts_with(b"/*") {
            at += 2;
            let mut depth = 1;
            while at < bytes.len() && depth > 0 {
                if bytes[at..].starts_with(b"/*") {
                    depth += 1;
                    at += 2;
                } else if bytes[at..].starts_with(b"*/") {
                    depth -= 1;
                    at += 2;
                } else {
                    at += 1;
                }
            }
            if depth != 0 {
                return Err(Error::invalid("unterminated migration comment"));
            }
            continue;
        }
        if matches!(bytes[at], b'\'' | b'"' | b'`' | b'[') {
            let quote = if bytes[at] == b'[' { b']' } else { bytes[at] };
            at += 1;
            let mut closed = false;
            while at < bytes.len() {
                if bytes[at] == quote {
                    at += 1;
                    if at < bytes.len() && bytes[at] == quote {
                        at += 1;
                    } else {
                        closed = true;
                        break;
                    }
                } else {
                    at += 1;
                }
            }
            if !closed {
                return Err(Error::invalid("unterminated migration quote"));
            }
            prefix = false;
            continue;
        }
        if bytes[at] == b'$' {
            let start = at;
            let mut end = at + 1;
            while end < bytes.len() && (bytes[end].is_ascii_alphanumeric() || bytes[end] == b'_') {
                end += 1;
            }
            if end < bytes.len() && bytes[end] == b'$' {
                let delimiter = &sql[start..=end];
                at = end + 1;
                let next = sql[at..]
                    .find(delimiter)
                    .ok_or_else(|| Error::invalid("unterminated migration dollar quote"))?;
                at += next + delimiter.len();
                prefix = false;
                continue;
            }
        }
        if bytes[at] == b';' {
            prefix = true;
            at += 1;
            continue;
        }
        if bytes[at].is_ascii_alphabetic() || bytes[at] == b'_' {
            let start = at;
            while at < bytes.len() && (bytes[at].is_ascii_alphanumeric() || bytes[at] == b'_') {
                at += 1;
            }
            if prefix {
                let word = sql[start..at].to_ascii_uppercase();
                if matches!(
                    word.as_str(),
                    "BEGIN"
                        | "COMMIT"
                        | "END"
                        | "ROLLBACK"
                        | "SAVEPOINT"
                        | "RELEASE"
                        | "START"
                        | "PREPARE"
                ) {
                    return Err(Error::invalid(
                        "migration scripts cannot issue transaction control",
                    ));
                }
            }
            prefix = false;
            continue;
        }
        prefix = false;
        at += 1;
    }
    Ok(())
}
