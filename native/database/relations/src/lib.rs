//! Explicit immutable relation/mutation compilation. SQL and placeholder order
//! belong to SeaQuery; the existing provider remains the sole B07 scalar binder.
mod mutation;
mod relation;
mod transport;
pub use mutation::{Mutation, compile_mutation};
pub use relation::compile_relation;

use nativeweave_data_contracts::{Column, Model, Profile};
use nativeweave_interop_abi::{NW_DOMAIN_ERROR, NW_INVALID_ARGUMENT, NW_NOT_SUPPORTED};
use nativeweave_query_binding::{BindingError, Limits, ModelBinding, Result};
use sea_query::{Alias, TableRef};

fn invalid(detail: &'static str) -> BindingError {
    BindingError {
        status: NW_INVALID_ARGUMENT,
        detail,
    }
}
fn capacity(detail: &'static str) -> BindingError {
    BindingError {
        status: NW_DOMAIN_ERROR,
        detail,
    }
}
fn unsupported(detail: &'static str) -> BindingError {
    BindingError {
        status: NW_NOT_SUPPORTED,
        detail,
    }
}
fn table(model: &Model) -> Result<TableRef> {
    // B01 already validates and owns canonical UTF8; extract store identifiers,
    // then let SeaQuery quote them. Never concatenate identifiers into SQL.
    let json: serde_json::Value =
        serde_json::from_slice(model.bytes()).map_err(|_| invalid("model JSON"))?;
    let name = json["table"]
        .as_str()
        .ok_or_else(|| invalid("store table"))?;
    use sea_query::IntoTableRef;
    Ok(match json["schema"].as_str() {
        Some(schema) => (Alias::new(schema), Alias::new(name)).into_table_ref(),
        None => Alias::new(name).into_table_ref(),
    })
}
fn ordered(model: &Model) -> Result<Vec<&Column>> {
    let json: serde_json::Value =
        serde_json::from_slice(model.bytes()).map_err(|_| invalid("model JSON"))?;
    json["projection"]
        .as_array()
        .ok_or_else(|| invalid("model projection"))?
        .iter()
        .map(|id| {
            Ok(model.column(
                id.as_u64()
                    .and_then(|v| u32::try_from(v).ok())
                    .ok_or_else(|| invalid("column ID"))?,
            )?)
        })
        .collect()
}
fn check_limits(limits: Limits, bytes: usize, parameters: usize, nodes: usize) -> Result<()> {
    limits.validate()?;
    if bytes > limits.bytes || parameters > limits.parameters || nodes > limits.nodes {
        return Err(capacity("mutation/relation bounded demand"));
    }
    if limits.depth < 2 {
        return Err(capacity("mutation/relation depth"));
    }
    Ok(())
}

#[cfg(test)]
mod tests;
