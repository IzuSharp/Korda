use crate::{transport::*, *};
use nativeweave_query_binding::ProjectionBinding;
use nativeweave_query_native::RenderedQuery;
use sea_query::{Expr, ExprTrait, OnConflict, PostgresQueryBuilder, Query, SqliteQueryBuilder};

/// One already validated command and its native affected-row policy.
pub struct Mutation {
    pub command: RenderedQuery,
    pub checked: bool,
    pub key_bound: bool,
    pub input_rows: usize,
}

/// Copies/validates the exact mutation field inventory before any statement can execute.
pub fn compile_mutation(
    model: &ModelBinding,
    kind: u32,
    payload: &[u8],
    projection: Option<ProjectionBinding>,
    limits: Limits,
    timeout_ns: u64,
) -> Result<Mutation> {
    check_limits(limits, payload.len(), 0, 1)?;
    let mut schema = vec![(1, 1)];
    match kind {
        0 => schema.push((2, 5)),
        1 => schema.extend([(2, 5), (7, 1)]),
        2 => schema.extend([(2, 5), (3, 5)]),
        3 => schema.extend([(2, 5), (3, 5), (4, 5)]),
        4 => schema.push((3, 5)),
        5 => schema.extend([(3, 5), (4, 5)]),
        6 => {
            // The record parser still validates canonical IDs, offsets and exactly
            // one target. Only the selected target's required inventory is admitted.
            if payload.len() < 16 {
                return Err(invalid("mutation envelope"));
            }
            let count = u32::from_le_bytes(payload[8..12].try_into().unwrap()) as usize;
            if count != 4 || payload.len() < 16 + count * 16 {
                return Err(invalid("upsert field inventory"));
            }
            let target = u16::from_le_bytes(payload[48..50].try_into().unwrap());
            if target != 5 && target != 6 {
                return Err(invalid("upsert conflict target"));
            }
            schema.extend([(2, 5), (target, if target == 5 { 7 } else { 4 }), (8, 5)]);
        }
        _ => return Err(invalid("mutation kind")),
    }
    let fields = Fields::parse(payload, 4, &schema)?;
    if fields.u32(1)? != model.model.table_id() {
        return Err(invalid("mutation table identity"));
    }
    if (kind == 1) != projection.is_some() {
        return Err(invalid("mutation projection inventory"));
    }
    let mut parameters = Parameters::default();
    let mut input_rows = 1;
    let (sql, values) = match kind {
        0 | 1 | 6 => {
            let insert = row_values(model, fields.bytes(2)?, Role::Insert, kind == 0, limits)?;
            input_rows = insert.rows.len();
            let mut statement = Query::insert();
            statement.into_table(table(&model.model)?);
            if insert.columns.is_empty() {
                if insert.rows.len() != 1 {
                    return Err(unsupported("multirow generated-only insert"));
                }
                statement.or_default_values();
            } else {
                statement.columns(insert.columns.iter().map(|c| Alias::new(&c.name)));
                for row in &insert.rows {
                    statement
                        .values(
                            row.iter()
                                .zip(&insert.descriptors)
                                .map(|(v, d)| parameters.value(d, v)),
                        )
                        .map_err(|_| invalid("insert shape"))?;
                }
            }
            if kind == 1 {
                let returned = projection.as_ref().unwrap();
                if fields.u32(7)? != returned.id || returned.source != model.source {
                    return Err(invalid("returning source/projection"));
                }
                let columns = returned
                    .projection
                    .fields()
                    .iter()
                    .map(|field| {
                        let column = model
                            .model
                            .columns()
                            .iter()
                            .find(|c| c.name == field.name)
                            .ok_or_else(|| invalid("RETURNING column"))?;
                        if column.codec != field.codec {
                            return Err(invalid("RETURNING codec/nullability"));
                        }
                        Ok(Alias::new(&column.name))
                    })
                    .collect::<Result<Vec<_>>>()?;
                statement.returning(Query::returning().columns(columns));
            }
            if kind == 6 {
                let update = row_values(model, fields.bytes(8)?, Role::Assignments, false, limits)?;
                let mut conflict = if schema[2].0 == 5 {
                    let bytes = fields.bytes(5)?;
                    if bytes.is_empty()
                        || !bytes.len().is_multiple_of(4)
                        || bytes.len() / 4 > limits.parameters
                    {
                        return Err(invalid("conflict columns"));
                    }
                    let mut ids = std::collections::HashSet::new();
                    let columns = bytes
                        .as_chunks::<4>()
                        .0
                        .iter()
                        .map(|id| {
                            let id = u32::from_le_bytes(*id);
                            if !ids.insert(id) {
                                return Err(invalid("duplicate conflict column"));
                            }
                            Ok(Alias::new(&model.model.column(id)?.name))
                        })
                        .collect::<Result<Vec<_>>>()?;
                    OnConflict::columns(columns)
                } else {
                    if model.source.profile != Profile::PostgreSql {
                        return Err(unsupported("named constraint conflict target"));
                    }
                    let name = std::str::from_utf8(fields.bytes(6)?)
                        .map_err(|_| invalid("constraint UTF8"))?;
                    if name.is_empty() || name.len() > 256 || name.chars().any(char::is_control) {
                        return Err(invalid("constraint name"));
                    }
                    OnConflict::constraint(name)
                };
                for ((column, descriptor), value) in update
                    .columns
                    .iter()
                    .zip(&update.descriptors)
                    .zip(&update.rows[0])
                {
                    conflict.value(
                        Alias::new(&column.name),
                        parameters.value(descriptor, value),
                    );
                }
                statement.on_conflict(conflict);
            }
            match model.source.profile {
                Profile::Sqlite => statement.build(SqliteQueryBuilder),
                Profile::PostgreSql => statement.build(PostgresQueryBuilder),
            }
        }
        2 | 3 => {
            let assignments =
                row_values(model, fields.bytes(2)?, Role::Assignments, false, limits)?;
            let key = row_values(model, fields.bytes(3)?, Role::Key, false, limits)?;
            let mut statement = Query::update();
            statement.table(table(&model.model)?);
            for ((column, descriptor), value) in assignments
                .columns
                .iter()
                .zip(&assignments.descriptors)
                .zip(&assignments.rows[0])
            {
                statement.value(
                    Alias::new(&column.name),
                    parameters.value(descriptor, value),
                );
            }
            for condition in conditions(&key, &mut parameters) {
                statement.and_where(condition);
            }
            if kind == 3 {
                for condition in conditions(
                    &row_values(model, fields.bytes(4)?, Role::Checks, false, limits)?,
                    &mut parameters,
                ) {
                    statement.and_where(condition);
                }
            }
            match model.source.profile {
                Profile::Sqlite => statement.build(SqliteQueryBuilder),
                Profile::PostgreSql => statement.build(PostgresQueryBuilder),
            }
        }
        4 | 5 => {
            let key = row_values(model, fields.bytes(3)?, Role::Key, false, limits)?;
            let mut statement = Query::delete();
            statement.from_table(table(&model.model)?);
            for condition in conditions(&key, &mut parameters) {
                statement.and_where(condition);
            }
            if kind == 5 {
                for condition in conditions(
                    &row_values(model, fields.bytes(4)?, Role::Checks, false, limits)?,
                    &mut parameters,
                ) {
                    statement.and_where(condition);
                }
            }
            match model.source.profile {
                Profile::Sqlite => statement.build(SqliteQueryBuilder),
                Profile::PostgreSql => statement.build(PostgresQueryBuilder),
            }
        }
        _ => unreachable!(),
    };
    let command = parameters.bind(
        model,
        (sql, values),
        projection,
        if kind == 1 { 0 } else { 6 },
        timeout_ns,
        limits,
    )?;
    Ok(Mutation {
        command,
        checked: kind == 3 || kind == 5,
        key_bound: matches!(kind, 2..=5),
        input_rows,
    })
}
fn conditions(values: &RowValues, parameters: &mut Parameters) -> Vec<sea_query::SimpleExpr> {
    values
        .columns
        .iter()
        .zip(&values.descriptors)
        .zip(&values.rows[0])
        .map(|((column, descriptor), value)| {
            if value.is_none() {
                Expr::col(Alias::new(&column.name)).is_null()
            } else {
                Expr::col(Alias::new(&column.name)).eq(parameters.value(descriptor, value))
            }
        })
        .collect()
}
