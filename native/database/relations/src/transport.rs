use crate::*;
use nativeweave_data_contracts::wire::{Batch, Descriptor, Expectation, control_fields};
use sea_query::{Expr, SimpleExpr, Value, Values};
use sha2::{Digest, Sha256};
use std::collections::BTreeMap;

pub(crate) struct Fields<'a>(BTreeMap<u16, &'a [u8]>);
impl<'a> Fields<'a> {
    pub fn parse(bytes: &'a [u8], kind: u16, schema: &[(u16, u16)]) -> Result<Self> {
        let values = control_fields(bytes, kind, schema)?;
        Ok(Self(
            schema
                .iter()
                .zip(values)
                .map(|((id, _), value)| (*id, value))
                .collect(),
        ))
    }
    pub fn bytes(&self, id: u16) -> Result<&'a [u8]> {
        self.0
            .get(&id)
            .copied()
            .ok_or_else(|| invalid("required field"))
    }
    pub fn u32(&self, id: u16) -> Result<u32> {
        Ok(u32::from_le_bytes(
            self.bytes(id)?
                .try_into()
                .map_err(|_| invalid("u32 field"))?,
        ))
    }
}

pub(crate) struct RowValues {
    pub columns: Vec<Column>,
    pub descriptors: Vec<Descriptor>,
    pub rows: Vec<Vec<Option<Vec<u8>>>>,
}
#[derive(Clone, Copy)]
pub(crate) enum Role {
    Insert,
    Assignments,
    Key,
    Checks,
}
pub(crate) fn row_values(
    model: &ModelBinding,
    bytes: &[u8],
    role: Role,
    multiple: bool,
    limits: Limits,
) -> Result<RowValues> {
    if bytes.len() < 96 || bytes.len() > limits.bytes {
        return Err(capacity("mutation value size"));
    }
    let count = u32::from_le_bytes(bytes[60..64].try_into().unwrap()) as usize;
    if count > limits.parameters || count > limits.nodes || count > (bytes.len() - 96) / 16 {
        return Err(capacity("mutation descriptors"));
    }
    let mut columns = Vec::with_capacity(count);
    for index in 0..count {
        let at = 96 + index * 16;
        let id = u32::from_le_bytes(bytes[at..at + 4].try_into().unwrap());
        let column = model.model.column(id)?.clone();
        if columns.iter().any(|previous: &Column| previous.id == id) {
            return Err(invalid("duplicate mutation column"));
        }
        match role {
            Role::Insert if column.generated => return Err(invalid("generated insert column")),
            Role::Assignments if column.generated || column.key_ordinal.is_some() => {
                return Err(invalid("key/generated assignment"));
            }
            Role::Key if column.key_ordinal != Some(index as u32) || column.codec.nullable => {
                return Err(invalid("complete ordered non-null key required"));
            }
            Role::Checks if !column.concurrency => {
                return Err(invalid("undeclared concurrency column"));
            }
            _ => {}
        }
        columns.push(column);
    }
    let ordered = ordered(&model.model)?;
    let required: Vec<u32> = match role {
        Role::Insert => ordered
            .iter()
            .filter(|c| !c.generated)
            .map(|c| c.id)
            .collect(),
        Role::Key => model.model.keys().iter().map(|c| c.id).collect(),
        Role::Checks => ordered
            .iter()
            .filter(|c| c.concurrency)
            .map(|c| c.id)
            .collect(),
        Role::Assignments => ordered
            .iter()
            .filter(|c| columns.iter().any(|value| value.id == c.id))
            .map(|c| c.id)
            .collect(),
    };
    if columns.iter().map(|c| c.id).collect::<Vec<_>>() != required
        || !matches!(role, Role::Insert) && columns.is_empty()
    {
        return Err(invalid("mutation role shape/order"));
    }
    let descriptors: Vec<_> = columns
        .iter()
        .map(|column| {
            Ok(Descriptor {
                id: column.id,
                codec: column.codec.clone(),
                bound_codec: model.codecs.bound(&column.codec)?,
            })
        })
        .collect::<Result<_>>()?;
    let expectation = Expectation {
        kind: 4,
        profile: model.source.profile,
        hash: model.model.hash(),
        entity_id: model.model.table_id(),
        projection_id: 0,
        descriptors: descriptors.clone(),
        exact_rows: if multiple { None } else { Some(1) },
    };
    let batch = Batch::parse(bytes, expectation)?;
    let cells = (batch.rows() as usize)
        .checked_mul(count)
        .ok_or_else(|| capacity("mutation cell count"))?;
    check_limits(
        limits,
        bytes.len(),
        cells,
        cells.saturating_mul(2).saturating_add(1),
    )?;
    let rows = (0..batch.rows())
        .map(|row| {
            (0..count)
                .map(|column| {
                    let cell = batch.cell(row, column)?;
                    if cell.is_some_and(|value| {
                        columns[column]
                            .max_length
                            .is_some_and(|bound| value.len() > bound as usize)
                    }) {
                        return Err(invalid("column byte capacity"));
                    }
                    Ok(cell.map(Vec::from))
                })
                .collect::<Result<Vec<_>>>()
        })
        .collect::<Result<Vec<_>>>()?;
    Ok(RowValues {
        columns,
        descriptors,
        rows,
    })
}

#[derive(Default)]
pub(crate) struct Parameters {
    slots: Vec<(Descriptor, Option<Vec<u8>>)>,
}
impl Parameters {
    pub fn value(&mut self, descriptor: &Descriptor, bytes: &Option<Vec<u8>>) -> SimpleExpr {
        let id = self.slots.len();
        self.slots.push((descriptor.clone(), bytes.clone()));
        Expr::val(Value::BigUnsigned(Some(id as u64)))
    }
    pub fn bind(
        self,
        model: &ModelBinding,
        rendered: (String, Values),
        projection: Option<nativeweave_query_binding::ProjectionBinding>,
        terminal: u32,
        timeout_ns: u64,
        limits: Limits,
    ) -> Result<nativeweave_query_native::RenderedQuery> {
        let (sql, values) = rendered;
        let mut descriptors = Vec::with_capacity(values.0.len());
        let mut cells = Vec::with_capacity(values.0.len());
        for (index, value) in values.0.into_iter().enumerate() {
            let Value::BigUnsigned(Some(slot)) = value else {
                return Err(invalid("unexpected native placeholder"));
            };
            let (mut descriptor, bytes) = self
                .slots
                .get(usize::try_from(slot).map_err(|_| invalid("parameter slot"))?)
                .cloned()
                .ok_or_else(|| invalid("parameter slot"))?;
            descriptor.id = u32::try_from(index + 1).map_err(|_| capacity("parameter count"))?;
            descriptors.push(descriptor);
            cells.push(bytes);
        }
        check_limits(
            limits,
            sql.len(),
            descriptors.len(),
            descriptors.len().saturating_mul(2).saturating_add(1),
        )?;
        let mut identity = Vec::new();
        identity.extend_from_slice(&model.model.hash());
        identity.extend_from_slice(sql.as_bytes());
        for d in &descriptors {
            identity.extend_from_slice(&d.id.to_le_bytes());
            identity.extend_from_slice(&d.bound_codec.to_le_bytes());
            identity.push(u8::from(d.codec.nullable));
        }
        let expected = Expectation {
            kind: 1,
            profile: model.source.profile,
            hash: Sha256::digest(identity).into(),
            entity_id: 0,
            projection_id: 0,
            descriptors,
            exact_rows: Some(1),
        };
        let bytes = Batch::encode(
            &expected,
            1,
            &cells.iter().map(|v| v.as_deref()).collect::<Vec<_>>(),
        )?;
        check_limits(
            limits,
            bytes.len(),
            expected.descriptors.len(),
            expected
                .descriptors
                .len()
                .saturating_mul(2)
                .saturating_add(1),
        )?;
        Ok(nativeweave_query_native::RenderedQuery {
            sql,
            parameter_bytes: bytes,
            parameter_expectation: expected,
            projection,
            terminal,
            timeout_ns,
        })
    }
}
