use crate::*;
use nativeweave_query_binding::projection_record;
use sea_query::{
    Alias as SqlAlias, Asterisk, BinOper, CommonTableExpression, ExprTrait, Frame, FrameType, Func,
    JoinType, LockBehavior, LockType, NullOrdering, Order, OverStatement, PostgresQueryBuilder,
    Query, QueryBuilder, SelectExpr, SqlWriterValues, SqliteQueryBuilder, UnionType, Value,
    WindowSelectType,
};
use wire::Fields;

enum Pending {
    None,
    Expression(NativeExpression),
    Alias(Alias),
    Window(WindowStatement),
}
impl NativeQuery {
    /// Builds a private SeaQuery candidate, validates the exact B09 operation, then
    /// publishes once. An invalid clause leaves both native SQL and binding state unchanged.
    pub fn clause(&mut self, opcode: u32, bytes: &[u8]) -> Result<u64> {
        let fields = Fields::new(bytes)?;
        if !self.capabilities.windows
            && (matches!(opcode, 11 | 21)
                || opcode == 13 && (fields.get(4).is_some() || matches!(fields.u32(1)?, 6 | 7)))
        {
            return Err(unsupported("window capability"));
        }
        let mut candidate = self.data.clone();
        let pending = self.prepare_clause(opcode, &fields, &mut candidate)?;
        let extra = match &pending {
            Pending::Expression(v) => v.nodes,
            Pending::Window(_) => 1,
            _ => 0,
        };
        candidate.check_extra(extra, 0, self.limits)?;
        let token = self.arena.clause(opcode, bytes)?;
        match pending {
            Pending::None => {}
            Pending::Expression(value) => {
                candidate.expressions.insert(token as u32, value);
            }
            Pending::Alias(value) => {
                candidate.aliases.insert(token as u32, value);
            }
            Pending::Window(value) => {
                candidate.windows.insert(token as u32, (value, 1));
            }
        }
        self.data = candidate;
        Ok(token)
    }
    fn prepare_clause(
        &self,
        opcode: u32,
        f: &Fields<'_>,
        data: &mut NativeState,
    ) -> Result<Pending> {
        match opcode {
            1 | 2 => {
                let value = self.expression(f.u64(1)?)?;
                data.statement_nodes += value.nodes;
                if opcode == 1 {
                    data.statement.and_where(value.expr.clone());
                    data.predicates.push(value.expr);
                } else {
                    data.statement.and_having(value.expr);
                }
            }
            3 => {
                let value = self.expression(f.u64(1)?)?;
                let (direction, nulls) = ordering(f.u32(2)?, f.u32(3)?)?;
                if f.u32(4)? == 1 {
                    data.statement.clear_order_by();
                }
                data.statement
                    .order_by_expr_with_nulls(value.expr, direction, nulls);
                data.statement_nodes += value.nodes;
            }
            4 => {
                let value = self.expression(f.u64(1)?)?;
                data.statement.add_group_by([value.expr]);
                data.grouped = true;
                data.statement_nodes += value.nodes;
            }
            5 => {
                if f.get(1).is_some() {
                    data.statement.limit(f.u64(1)?);
                }
                if f.get(2).is_some() {
                    data.statement.offset(f.u64(2)?);
                }
            }
            6 => {
                let tokens = f.tokens(1)?;
                let projection = projection_record(
                    f.bytes(2)?,
                    self.root.source,
                    1,
                    Arc::clone(&self.root.codecs),
                    Some(&tokens),
                )?;
                if tokens.len() != projection.projection.fields().len() {
                    return Err(invalid("projection count"));
                }
                data.statement.clear_selects();
                for (token, field) in tokens.iter().zip(projection.projection.fields()) {
                    let value = self.expression(*token)?;
                    data.statement
                        .expr_as(value.expr, SqlAlias::new(&field.name));
                    data.statement_nodes += value.nodes;
                }
            }
            7 => {
                let token = f.u64(1)?;
                let on = self.expression(f.u64(2)?)?;
                let alias = data
                    .aliases
                    .get(&(token as u32))
                    .ok_or_else(|| invalid("native JOIN reservation"))?;
                if alias.left {
                    let mut table = Query::select();
                    for column in alias.model.model.columns() {
                        table.column(SqlAlias::new(&column.name));
                    }
                    table
                        .expr_as(
                            Expr::Constant(Value::Int(Some(1))),
                            SqlAlias::new(&alias.marker),
                        )
                        .from(table_ref(&alias.model)?);
                    data.statement.join_subquery(
                        JoinType::LeftJoin,
                        table,
                        SqlAlias::new(&alias.name),
                        on.expr,
                    );
                    data.statement_nodes += alias.model.model.columns().len() + 2;
                } else {
                    data.statement.join_as(
                        JoinType::InnerJoin,
                        table_ref(&alias.model)?,
                        SqlAlias::new(&alias.name),
                        on.expr,
                    );
                }
                data.statement_nodes += on.nodes + 1;
            }
            8 => {
                data.statement.distinct();
            }
            9 => {
                let plan = data
                    .plans
                    .get(&(f.u64(1)? as u32))
                    .ok_or_else(|| invalid("native imported plan"))?;
                let operation = match f.u32(2)? {
                    0 => UnionType::Distinct,
                    1 => UnionType::All,
                    2 => UnionType::Intersect,
                    3 => UnionType::Except,
                    _ => return Err(unsupported("set operation")),
                };
                data.statement.union(operation, plan.selection()?);
                data.set_operations.push((f.u32(2)?, Arc::clone(plan)));
                data.statement_nodes += plan.data.statement_nodes;
            }
            10 => {
                let cte = data
                    .ctes
                    .get(&(f.u64(1)? as u32))
                    .ok_or_else(|| invalid("native imported CTE"))?;
                let mut table = CommonTableExpression::new();
                table
                    .table_name(SqlAlias::new(&cte.binding.name))
                    .query(cte.body.selection()?);
                let model: serde_json::Value =
                    serde_json::from_slice(cte.binding.declared.model.bytes())
                        .map_err(|_| invalid("CTE model"))?;
                for id in model["projection"]
                    .as_array()
                    .ok_or_else(|| invalid("CTE model projection"))?
                {
                    let column = cte
                        .binding
                        .declared
                        .model
                        .column(id.as_u64().ok_or_else(|| invalid("CTE column"))? as u32)?;
                    table.column(SqlAlias::new(&column.name));
                }
                data.recursive_cte |= cte.binding.recursive;
                data.with_clause.recursive(data.recursive_cte).cte(table);
                data.has_cte = true;
                data.statement_nodes += cte.body.data.statement_nodes;
            }
            11 => {
                let action = f.u32(1)?;
                if action == 0 {
                    return Ok(Pending::Window(WindowStatement::new()));
                }
                let id = f.u64(2)? as u32;
                let value = if matches!(action, 1 | 2) {
                    Some(self.expression(f.u64(3)?)?)
                } else {
                    None
                };
                let (window, nodes) = data
                    .windows
                    .get_mut(&id)
                    .ok_or_else(|| invalid("native window token"))?;
                match action {
                    1 => {
                        let value = value.ok_or_else(|| invalid("window partition"))?;
                        window.add_partition_by(value.expr);
                        *nodes += value.nodes;
                    }
                    2 => {
                        let value = value.ok_or_else(|| invalid("window order"))?;
                        let (direction, nulls) = ordering(f.u32(4)?, f.u32(5)?)?;
                        window.order_by_expr_with_nulls(value.expr, direction, nulls);
                        *nodes += value.nodes;
                    }
                    3 => {
                        window.frame_between(FrameType::Rows, frame(f.u32(6)?)?, frame(f.u32(7)?)?);
                        *nodes += 1;
                    }
                    _ => return Err(unsupported("window action")),
                }
            }
            12 => {
                if self.root.source.profile != Profile::PostgreSql {
                    return Err(unsupported("row locks require PostgreSQL"));
                }
                let mode = match f.u32(1)? {
                    0 => LockType::Update,
                    1 => LockType::Share,
                    2 => LockType::NoKeyUpdate,
                    3 => LockType::KeyShare,
                    _ => return Err(unsupported("row lock mode")),
                };
                match f.u32(2)? {
                    0 => {
                        data.statement.lock(mode);
                    }
                    1 => {
                        data.statement
                            .lock_with_behavior(mode, LockBehavior::Nowait);
                    }
                    2 => {
                        data.statement
                            .lock_with_behavior(mode, LockBehavior::SkipLocked);
                    }
                    _ => return Err(unsupported("row lock wait")),
                }
            }
            13 => {
                data.aggregate_nodes += 1;
                let function = f.u32(1)?;
                let distinct = f.u32(3)? == 1;
                let input_codec = if f.get(2).is_some() {
                    Some(self.arena.expression_type(f.u64(2)?)?.codec)
                } else {
                    None
                };
                let input = if f.get(2).is_some() {
                    Some(self.expression(f.u64(2)?)?)
                } else {
                    None
                };
                let mut nodes = 1 + input.as_ref().map_or(0, |v| v.nodes);
                let arg = input.map(|v| v.expr);
                let distinct_arg = |value: Expr| {
                    if distinct {
                        Expr::cust_with_expr(
                            if self.root.source.profile == Profile::Sqlite {
                                "DISTINCT ?"
                            } else {
                                "DISTINCT $1"
                            },
                            value,
                        )
                    } else {
                        value
                    }
                };
                let mut expr: Expr = match function {
                    0 => Func::count(Expr::col(Asterisk)).into(),
                    1 => {
                        let arg = arg.ok_or_else(|| invalid("aggregate input"))?;
                        if distinct {
                            Func::count_distinct(arg).into()
                        } else {
                            Func::count(arg).into()
                        }
                    }
                    2 => Func::sum(distinct_arg(arg.ok_or_else(|| invalid("aggregate input"))?))
                        .into(),
                    3 => Func::avg(distinct_arg(arg.ok_or_else(|| invalid("aggregate input"))?))
                        .into(),
                    4 => Func::min(distinct_arg(arg.ok_or_else(|| invalid("aggregate input"))?))
                        .into(),
                    5 => Func::max(distinct_arg(arg.ok_or_else(|| invalid("aggregate input"))?))
                        .into(),
                    6 => Func::cust(SqlAlias::new("ROW_NUMBER")).into(),
                    7 => Func::cust(SqlAlias::new("RANK")).into(),
                    _ => return Err(unsupported("aggregate function")),
                };
                if f.get(4).is_some() {
                    let (window, count) = data
                        .windows
                        .get(&(f.u64(4)? as u32))
                        .ok_or_else(|| invalid("aggregate window"))?;
                    expr = window_expr(expr, window.clone(), self.root.source.profile);
                    nodes += count;
                }
                // PostgreSQL widens SUM/AVG. Cast the complete window expression,
                // or retain its raw aggregate so a later explicit OVER does the same.
                let cast = if self.root.source.profile == Profile::PostgreSql {
                    input_codec.as_ref().and_then(|codec| {
                        if function == 2 && matches!(codec.id, 1..=3 | 15) {
                            Some("bigint")
                        } else if (function == 3 && matches!(codec.id, 1..=3 | 11 | 12))
                            || (function == 2 && matches!(codec.id, 11 | 12))
                        {
                            Some("double precision")
                        } else {
                            None
                        }
                    })
                } else {
                    None
                };
                let window_cast = cast
                    .filter(|_| f.get(4).is_none())
                    .map(|ty| (expr.clone(), ty));
                if window_cast.is_some() {
                    nodes += nodes;
                }
                if let Some(ty) = cast {
                    expr = expr.cast_as(SqlAlias::new(ty));
                    nodes += 1;
                }
                return Ok(Pending::Expression(NativeExpression {
                    expr,
                    nodes,
                    window_cast,
                }));
            }
            14 => {
                let plan = data
                    .plans
                    .get(&(f.u64(1)? as u32))
                    .ok_or_else(|| invalid("native subquery plan"))?;
                let mut nodes = plan.data.statement_nodes + 1;
                let expr = match f.u32(2)? {
                    0 => Expr::exists(plan.selection()?),
                    1 => {
                        let lhs = self.expression(f.u64(3)?)?;
                        nodes += lhs.nodes;
                        lhs.expr.in_subquery(plan.selection()?)
                    }
                    _ => return Err(unsupported("subquery operation")),
                };
                return Ok(Pending::Expression(NativeExpression {
                    expr,
                    nodes,
                    window_cast: None,
                }));
            }
            15 => {
                let condition = self.expression(f.u64(1)?)?;
                let yes = self.expression(f.u64(2)?)?;
                let no = self.expression(f.u64(3)?)?;
                let nodes = condition.nodes + yes.nodes + no.nodes + 1;
                return Ok(Pending::Expression(NativeExpression {
                    window_cast: None,
                    expr: Expr::case(condition.expr, yes.expr).finally(no.expr).into(),
                    nodes,
                }));
            }
            16 => return self.text_clause(f, data),
            17 => {
                let column = self.root.model.column(f.u32(1)?)?;
                let expression = self.expression(f.u64(2)?)?;
                data.assignments
                    .push((column.name.clone(), expression.expr));
                data.statement_nodes += expression.nodes;
            }
            18 => {
                let model = data
                    .models
                    .get(&(f.u64(1)? as u32))
                    .ok_or_else(|| invalid("native imported model"))?;
                let mut marker = String::from("_nw_match");
                while model.model.columns().iter().any(|c| c.name == marker) {
                    marker.push('_');
                }
                return Ok(Pending::Alias(Alias {
                    model: model.clone(),
                    name: f.text(3)?.to_owned(),
                    left: f.u32(4)? == 1,
                    marker,
                }));
            }
            19 => {
                data.aliases.remove(&(f.u64(1)? as u32));
            }
            20 => {
                let alias = data
                    .aliases
                    .get(&(f.u64(1)? as u32))
                    .ok_or_else(|| invalid("native left JOIN alias"))?;
                return Ok(Pending::Expression(NativeExpression {
                    window_cast: None,
                    expr: Expr::col((SqlAlias::new(&alias.name), SqlAlias::new(&alias.marker)))
                        .is_not_null(),
                    nodes: 2,
                }));
            }
            21 => {
                let value = self.expression(f.u64(1)?)?;
                let (window, nodes) = data
                    .windows
                    .get(&(f.u64(2)? as u32))
                    .ok_or_else(|| invalid("native window"))?;
                let expr = match value.window_cast {
                    Some((inner, ty)) => {
                        window_expr(inner, window.clone(), self.root.source.profile)
                            .cast_as(SqlAlias::new(ty))
                    }
                    None => window_expr(value.expr, window.clone(), self.root.source.profile),
                };
                return Ok(Pending::Expression(NativeExpression {
                    window_cast: None,
                    expr,
                    nodes: value.nodes + nodes + 1,
                }));
            }
            22 => {} // B09 owns the immutable timeout snapshot; no SQL clause is needed.
            _ => return Err(unsupported("clause opcode")),
        }
        Ok(Pending::None)
    }
    fn text_clause(&self, f: &Fields<'_>, data: &mut NativeState) -> Result<Pending> {
        let operation = f.u32(1)?;
        let input = self.expression(f.u64(2)?)?;
        if operation == 4 {
            // B09 verifies a fixed provider allowlist before publication. SeaQuery quotes the identifier.
            let name = f.text(3)?;
            return Ok(Pending::Expression(NativeExpression {
                window_cast: None,
                expr: input
                    .expr
                    .binary(BinOper::Custom("COLLATE"), Expr::col(SqlAlias::new(name))),
                nodes: input.nodes + 2,
            }));
        }
        if operation > 3 {
            return Err(unsupported("text operation"));
        }
        let escape = char::from_u32(f.u32(4)?)
            .filter(|c| *c != '\0')
            .ok_or_else(|| invalid("LIKE escape scalar"))?;
        let (codec, bound, cells) = wire::values(&self.root, f.bytes(3)?, Some(1), self.limits)?;
        if codec.id != 5 {
            return Err(invalid("text argument codec"));
        }
        let cell = match &cells[0] {
            None => None,
            Some(bytes) => {
                let text = std::str::from_utf8(bytes).map_err(|_| invalid("LIKE text UTF8"))?;
                let mut pattern = String::new();
                if operation == 0 || operation == 2 {
                    pattern.push('%');
                }
                for c in text.chars() {
                    if operation != 3 && (c == escape || c == '%' || c == '_') {
                        pattern.push(escape);
                    }
                    pattern.push(c);
                }
                if operation == 0 || operation == 1 {
                    pattern.push('%');
                }
                if pattern.len() > self.limits.bytes {
                    return Err(capacity("escaped LIKE pattern bound"));
                }
                Some(Arc::<[u8]>::from(pattern.into_bytes()))
            }
        };
        let bytes = cell.as_ref().map_or(0, |v| v.len());
        data.check_extra(input.nodes + 3, bytes, self.limits)?;
        let (id, value, expr) = expressions::slot(codec, bound, SlotValue::Literal(cell))?;
        data.slots.insert(id, value);
        let rhs = expr.binary(
            BinOper::Escape,
            Expr::Constant(Value::String(Some(escape.to_string()))),
        );
        Ok(Pending::Expression(NativeExpression {
            window_cast: None,
            expr: input.expr.binary(BinOper::Like, rhs),
            nodes: input.nodes + 3,
        }))
    }
}
fn ordering(direction: u32, nulls: u32) -> Result<(Order, NullOrdering)> {
    Ok((
        match direction {
            0 => Order::Asc,
            1 => Order::Desc,
            _ => return Err(invalid("order direction")),
        },
        match nulls {
            0 => NullOrdering::First,
            1 => NullOrdering::Last,
            _ => return Err(invalid("NULL ordering")),
        },
    ))
}
fn frame(code: u32) -> Result<Frame> {
    match code {
        0 => Ok(Frame::UnboundedPreceding),
        1 => Ok(Frame::CurrentRow),
        2 => Ok(Frame::UnboundedFollowing),
        _ => Err(invalid("window frame")),
    }
}

/// SeaQuery exposes OVER on SelectExpr, while the public API permits composing a
/// window expression. Snapshot it through SeaQuery's public expression renderer,
/// then retain its engine-produced template and native placeholder values. Neither
/// user SQL nor an independently implemented window/dialect emitter enters this path.
fn window_expr(expr: Expr, window: WindowStatement, profile: Profile) -> Expr {
    let selected = SelectExpr {
        expr,
        alias: None,
        window: Some(WindowSelectType::Query(window)),
    };
    let mut writer = SqlWriterValues::new(
        if profile == Profile::Sqlite { "?" } else { "$" },
        profile == Profile::PostgreSql,
    );
    match profile {
        Profile::Sqlite => SqliteQueryBuilder.prepare_select_expr(&selected, &mut writer),
        Profile::PostgreSql => PostgresQueryBuilder.prepare_select_expr(&selected, &mut writer),
    }
    let (text, values) = writer.into_parts();
    Expr::cust_with_values(format!("({text})"), values.0)
}
