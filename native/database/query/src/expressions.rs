use crate::*;
use nativeweave_query_binding::ParameterIdentity;
use sea_query::{Alias as SqlAlias, BinOper, ExprTrait, Func, Value};
use std::sync::atomic::{AtomicU64, Ordering};

/// Only an identity sequence, never an owner/cache/runtime. High-bit markers cannot
/// overlap with admitted LIMIT/OFFSET values (bounded to i64::MAX by B09).
static NEXT_SLOT: AtomicU64 = AtomicU64::new(1);
pub(crate) const SLOT_TAG: u64 = 1 << 63;

pub(crate) fn slot(
    codec: Codec,
    bound_codec: u32,
    value: SlotValue,
) -> Result<(u64, Arc<Slot>, Expr)> {
    let id = NEXT_SLOT
        .try_update(Ordering::Relaxed, Ordering::Relaxed, |v| {
            v.checked_add(1).filter(|v| *v < SLOT_TAG)
        })
        .map_err(|_| capacity("native placeholder identity exhausted"))?
        | SLOT_TAG;
    Ok((
        id,
        Arc::new(Slot {
            codec,
            bound_codec,
            value,
        }),
        Expr::Value(Value::BigUnsigned(Some(id))),
    ))
}
impl NativeQuery {
    pub fn column(&mut self, alias: u64, column_id: u32) -> Result<u64> {
        let (model, name) = if alias == 0 {
            (&self.root, names(&self.root)?.1)
        } else {
            let value = self
                .data
                .aliases
                .get(&(alias as u32))
                .ok_or_else(|| invalid("unknown native alias"))?;
            (&value.model, value.name.clone())
        };
        let column = model.model.column(column_id)?;
        let expression = NativeExpression {
            window_cast: None,
            expr: Expr::col((SqlAlias::new(name), SqlAlias::new(&column.name))),
            nodes: 1,
        };
        self.check_expression(1, 0)?;
        let token = self.arena.column(alias, column_id)?;
        Ok(self.put_expression(token, expression))
    }
    /// Copies a validated single db-wire cell immediately; mutable caller storage is never retained.
    pub fn value(&mut self, bytes: &[u8]) -> Result<u64> {
        let (codec, bound, cells) = wire::values(&self.root, bytes, Some(1), self.limits)?;
        self.check_expression(1, bytes.len())?;
        let (id, value, expr) = slot(codec, bound, SlotValue::Literal(cells[0].clone()))?;
        let token = self.arena.value(bytes)?;
        self.data.slots.insert(id, value);
        Ok(self.put_expression(
            token,
            NativeExpression {
                expr,
                nodes: 1,
                window_cast: None,
            },
        ))
    }
    /// Creates a reference to one source-bound parameter identity, not a mutable value buffer.
    pub fn parameter(&mut self, identity: ParameterIdentity) -> Result<u64> {
        self.check_expression(1, 0)?;
        let (id, value, expr) = slot(
            identity.codec.clone(),
            identity.bound_codec,
            SlotValue::Parameter(identity.id),
        )?;
        let token = self.arena.parameter(identity)?;
        self.data.slots.insert(id, value);
        Ok(self.put_expression(
            token,
            NativeExpression {
                expr,
                nodes: 1,
                window_cast: None,
            },
        ))
    }
    pub fn binary(&mut self, opcode: u32, left: u64, right: u64) -> Result<u64> {
        let a = self.expression(left)?;
        let b = self.expression(right)?;
        let nodes = 1 + a.nodes + b.nodes;
        self.check_expression(nodes, 0)?;
        let expr = match opcode {
            1 => a.expr.eq(b.expr),
            2 => a.expr.ne(b.expr),
            3 => a.expr.gt(b.expr),
            4 => a.expr.gte(b.expr),
            5 => a.expr.lt(b.expr),
            6 => a.expr.lte(b.expr),
            7 => a.expr.add(b.expr),
            8 => a.expr.sub(b.expr),
            9 => a.expr.mul(b.expr),
            10 => a.expr.div(b.expr),
            11 => a.expr.and(b.expr),
            12 => a.expr.or(b.expr),
            13 => a.expr.binary(
                if self.root.source.profile == Profile::Sqlite {
                    BinOper::Is
                } else {
                    BinOper::Custom("IS NOT DISTINCT FROM")
                },
                b.expr,
            ),
            14 => Func::coalesce([a.expr, b.expr]).into(),
            _ => return Err(unsupported("binary opcode")),
        };
        let token = self.arena.binary(opcode, left, right)?;
        Ok(self.put_expression(
            token,
            NativeExpression {
                expr,
                nodes,
                window_cast: None,
            },
        ))
    }
    pub fn unary(&mut self, opcode: u32, input: u64) -> Result<u64> {
        let value = self.expression(input)?;
        let nodes = value.nodes + 1;
        self.check_expression(nodes, 0)?;
        let expr = match opcode {
            1 => value.expr.is_null(),
            2 => value.expr.is_not_null(),
            3 => value.expr.not(),
            4 => value.expr,
            _ => return Err(unsupported("unary opcode")),
        };
        let token = self.arena.unary(opcode, input)?;
        Ok(self.put_expression(
            token,
            NativeExpression {
                expr,
                nodes,
                window_cast: if opcode == 4 { value.window_cast } else { None },
            },
        ))
    }
    pub fn list(&mut self, opcode: u32, input: u64, bytes: &[u8]) -> Result<u64> {
        let input_expr = self.expression(input)?;
        let (codec, bound, cells) = wire::values(
            &self.root,
            bytes,
            if opcode == 3 { Some(2) } else { None },
            self.limits,
        )?;
        let nodes = input_expr.nodes + cells.len() + 1;
        self.check_expression(nodes, bytes.len())?;
        let mut additions = Vec::with_capacity(cells.len());
        let mut exprs = Vec::with_capacity(cells.len());
        for value in cells {
            let (id, slot, expr) = slot(codec.clone(), bound, SlotValue::Literal(value))?;
            additions.push((id, slot));
            exprs.push(expr);
        }
        let expr = match opcode {
            1 | 2 if exprs.is_empty() => Expr::Constant(Value::Bool(Some(opcode == 2))),
            1 => input_expr.expr.binary(BinOper::In, Expr::Tuple(exprs)),
            2 => input_expr.expr.binary(BinOper::NotIn, Expr::Tuple(exprs)),
            3 => input_expr.expr.between(exprs[0].clone(), exprs[1].clone()),
            _ => return Err(unsupported("list opcode")),
        };
        let token = self.arena.list(opcode, input, bytes)?;
        self.data.slots.extend(additions);
        Ok(self.put_expression(
            token,
            NativeExpression {
                expr,
                nodes,
                window_cast: None,
            },
        ))
    }
}
