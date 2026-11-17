use crate::{transport::*, *};
use nativeweave_query_binding::RelationLoad;
use nativeweave_query_native::RenderedQuery;
use sea_query::{Expr, ExprTrait, PostgresQueryBuilder, Query, SqliteQueryBuilder};

/// One ordinary child SELECT. There is no parent existence query, additional
/// tenant predicate, ordering, dynamic loader or managed relationship engine.
pub fn compile_relation(load: &RelationLoad, limits: Limits) -> Result<RenderedQuery> {
    let relation = &load.relation;
    let key = row_values(&relation.parent, &load.key, Role::Key, false, limits)?;
    let canonical: serde_json::Value = serde_json::from_slice(relation.relation.bytes())
        .map_err(|_| invalid("relation identity"))?;
    let ids = canonical["child_columns"]
        .as_array()
        .ok_or_else(|| invalid("child columns"))?;
    if ids.len() != key.columns.len() {
        return Err(invalid("relation arity"));
    }
    let mut statement = Query::select();
    statement.from(table(&relation.child.model)?);
    for column in ordered(&relation.child.model)? {
        statement.column(Alias::new(&column.name));
    }
    let mut parameters = Parameters::default();
    for (index, id) in ids.iter().enumerate() {
        let id = id
            .as_u64()
            .and_then(|id| u32::try_from(id).ok())
            .ok_or_else(|| invalid("child column ID"))?;
        let column = relation.child.model.column(id)?;
        statement.and_where(
            Expr::col(Alias::new(&column.name))
                .eq(parameters.value(&key.descriptors[index], &key.rows[0][index])),
        );
    }
    let (sql, values) = match relation.source.profile {
        Profile::Sqlite => statement.build(SqliteQueryBuilder),
        Profile::PostgreSql => statement.build(PostgresQueryBuilder),
    };
    parameters.bind(
        &relation.child,
        (sql, values),
        Some(load.projection.clone()),
        0,
        load.timeout_ns,
        limits,
    )
}
