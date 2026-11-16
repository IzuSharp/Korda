use crate::{
    arena::{NEXT_ARENA, Node, boolean, merge_parameter, numeric},
    control::Fields,
    *,
};
use nativeweave_data_contracts::Profile;
use std::{
    collections::BTreeSet,
    sync::{Arc, atomic::Ordering},
};

/// Immutable CTE binding. B11 retains its own SeaQuery clone alongside this metadata.
#[derive(Clone, Debug)]
pub struct CteReference {
    pub source: SourceIdentity,
    pub declared: ModelBinding,
    pub body: Arc<PlanReference>,
    pub recursive: bool,
    pub name: String,
}
impl CteReference {
    pub fn create(
        declared: ModelBinding,
        table_id: u32,
        body: &QueryArena,
        recursive: u32,
    ) -> Result<Arc<Self>> {
        if !body.capabilities.cte {
            return Err(unsupported("CTE capability"));
        }
        declared.ensure_source(body.source())?;
        if declared.model.table_id() != table_id || recursive > 1 {
            return Err(invalid("CTE declaration/recursive flag"));
        }
        let mut snapshot = body.clone_selected()?;
        let plan = snapshot.seal(8)?;
        let projection = plan
            .projection
            .as_ref()
            .ok_or_else(|| invalid("CTE selection"))?;
        if !model_row_codecs_match(&declared, &projection.projection)? {
            return Err(invalid("CTE declared/body shape"));
        }
        let value: serde_json::Value = serde_json::from_slice(declared.model.bytes())
            .map_err(|_| invalid("canonical model"))?;
        let name = value["table"]
            .as_str()
            .ok_or_else(|| invalid("CTE table name"))?
            .to_owned();
        Ok(Arc::new(Self {
            source: declared.source,
            declared,
            body: plan,
            recursive: recursive == 1,
            name,
        }))
    }
}
impl QueryArena {
    /// Clone selected metadata under the host's read admission. No source mutation;
    /// clone tokens have a fresh generation and cannot be used by the source.
    pub fn clone_selected(&self) -> Result<Self> {
        self.mutable(false)?;
        if self.projection.is_none() {
            return Err(invalid("selected source required for clone"));
        }
        let generation = NEXT_ARENA
            .try_update(Ordering::Relaxed, Ordering::Relaxed, |n| n.checked_add(1))
            .map_err(|_| capacity("arena generation exhausted"))?;
        Ok(Self {
            generation,
            root: self.root.clone(),
            registry: Arc::clone(&self.registry),
            limits: self.limits,
            capabilities: self.capabilities,
            state: QueryState::Mutable,
            nodes: self.nodes.clone(),
            values: self.values.clone(),
            bytes: self.bytes,
            parameters: self.parameters.clone(),
            projection: self.projection.clone(),
            reserved: None,
            timeout_ns: self.timeout_ns,
            assignments: self.assignments.clone(),
            cte_names: self.cte_names.clone(),
            has_join: self.has_join,
            has_group: self.has_group,
            has_paging: self.has_paging,
        })
    }
    pub fn import_cte(&mut self, cte: Arc<CteReference>) -> Result<u64> {
        self.mutable(false)?;
        if !self.capabilities.cte {
            return Err(unsupported("CTE capability"));
        }
        if cte.source != self.source() {
            return Err(state("CTE source generation"));
        }
        self.quota(cte.body.owned_bytes, 0)?;
        let mut parameters = self.parameters.clone();
        for parameter in cte.body.parameters.values() {
            merge_parameter(&mut parameters, parameter, self.limits.parameters)?;
        }
        let bytes = cte.body.owned_bytes;
        let token = self.append(Node::Cte(cte), bytes)?;
        self.parameters = parameters;
        Ok(token)
    }
    /// Validates a direct kind6 operation and publishes binding metadata once.
    /// Each accepted operation consumes finite native budget even without output.
    /// Callers issue the matching SeaQuery operation only after successful validation;
    /// a backend failure that cannot be rolled back must poison this arena.
    pub fn clause(&mut self, opcode: u32, borrowed: &[u8]) -> Result<u64> {
        if !(1..=22).contains(&opcode) {
            return Err(unsupported("unknown required clause opcode"));
        }
        self.mutable(matches!(opcode, 7 | 19))?;
        if opcode != 19 {
            self.quota(borrowed.len(), 0)?;
        }
        let parse = |allowed: &[(u16, u16, bool)]| Fields::parse(borrowed, 6, allowed);
        match opcode {
            1 | 2 => {
                let f = parse(&[(1, 2, true)])?;
                let info = self.expression_type(f.u64(1)?)?;
                if info.codec.id != 4 {
                    return Err(invalid("predicate must be boolean"));
                }
                if opcode == 1 && info.aggregate {
                    return Err(invalid("aggregate not allowed in WHERE"));
                }
                self.marker(borrowed.len())
            }
            3 => {
                let f = parse(&[(1, 2, true), (2, 1, true), (3, 1, true), (4, 1, true)])?;
                self.expression_type(f.u64(1)?)?;
                direction(&f, 2, 3)?;
                f.flag(4)?;
                self.marker(borrowed.len())
            }
            4 => {
                let f = parse(&[(1, 2, true)])?;
                let info = self.expression_type(f.u64(1)?)?;
                if info.aggregate {
                    return Err(invalid("aggregate cannot be a GROUP key"));
                }
                self.marker(borrowed.len())?;
                self.has_group = true;
                Ok(0)
            }
            5 => {
                let f = parse(&[(1, 2, false), (2, 2, false)])?;
                if f.get(1).is_none() && f.get(2).is_none() {
                    return Err(invalid("empty paging operation"));
                }
                for id in 1..=2 {
                    if f.get(id).is_some() && f.u64(id)? > i64::MAX as u64 {
                        return Err(invalid("paging exceeds signed database range"));
                    }
                }
                self.marker(borrowed.len())?;
                self.has_paging = true;
                Ok(0)
            }
            6 => {
                let f = parse(&[(1, 8, true), (2, 5, true)])?;
                let tokens = token_vector(f.bytes(1)?)?;
                let projection = projection_record(
                    f.bytes(2)?,
                    self.source(),
                    1,
                    Arc::clone(&self.root.codecs),
                    Some(&tokens),
                )?;
                if tokens.len() != projection.projection.fields().len() {
                    return Err(invalid("projection count"));
                }
                for (token, field) in tokens.iter().zip(projection.projection.fields()) {
                    let info = self.expression_type(*token)?;
                    if info.codec != field.codec
                        || info.bound_codec != projection.codecs.bound(&field.codec)?
                    {
                        return Err(invalid("projection expression codec/nullability"));
                    }
                }
                self.marker(borrowed.len())?;
                self.projection = Some(projection);
                Ok(0)
            }
            7 => {
                let f = parse(&[(1, 2, true), (2, 2, true)])?;
                let alias = f.u64(1)?;
                let index = self.provisional(alias)?;
                let on = self.expression_type(f.u64(2)?)?;
                if on.codec.id != 4 || on.aggregate {
                    return Err(invalid("JOIN ON requires scalar boolean"));
                }
                let candidate = match self.node(alias)?.clone() {
                    Node::Alias {
                        model, name, left, ..
                    } => Node::Alias {
                        model,
                        name,
                        left,
                        provisional: false,
                    },
                    _ => unreachable!(),
                };
                self.marker(borrowed.len())?;
                self.invalidate_alias_expressions(index as u32 + 1);
                self.nodes[index] = Some(candidate);
                self.reserved = None;
                self.has_join = true;
                Ok(alias)
            }
            8 => {
                parse(&[])?;
                self.marker(borrowed.len())
            }
            9 => {
                let f = parse(&[(1, 2, true), (2, 1, true)])?;
                if f.u32(2)? > 3 {
                    return Err(unsupported("set operation"));
                }
                let plan = self.plan(f.u64(1)?)?;
                let own = self
                    .projection
                    .as_ref()
                    .ok_or_else(|| invalid("set selection missing"))?;
                let incoming = plan
                    .projection
                    .as_ref()
                    .ok_or_else(|| invalid("imported selection missing"))?;
                if own.projection.fields().len() != incoming.projection.fields().len()
                    || own
                        .projection
                        .fields()
                        .iter()
                        .zip(incoming.projection.fields())
                        .any(|(a, b)| a.codec != b.codec)
                {
                    return Err(invalid("set result shape"));
                }
                self.marker(borrowed.len())
            }
            10 => {
                let f = parse(&[(1, 2, true)])?;
                let Node::Cte(cte) = self.node(f.u64(1)?)? else {
                    return Err(invalid("expected imported CTE"));
                };
                if self.cte_names.contains(&cte.name) {
                    return Err(invalid("duplicate CTE name"));
                }
                let name = cte.name.clone();
                self.marker(borrowed.len())?;
                self.cte_names.insert(name);
                Ok(0)
            }
            11 => self.window_clause(borrowed),
            12 => {
                let f = parse(&[(1, 1, true), (2, 1, true)])?;
                if self.source().profile != Profile::PostgreSql || !self.capabilities.row_lock {
                    return Err(unsupported("PostgreSQL row-lock capability"));
                }
                if f.u32(1)? > 3 || f.u32(2)? > 2 {
                    return Err(unsupported("row-lock mode/wait"));
                }
                self.marker(borrowed.len())
            }
            13 => self.aggregate_clause(borrowed),
            14 => {
                let f = parse(&[(1, 2, true), (2, 1, true), (3, 2, false)])?;
                let plan = self.plan(f.u64(1)?)?;
                let projection = plan
                    .projection
                    .as_ref()
                    .ok_or_else(|| invalid("subquery projection"))?;
                let (nullable, dependencies, depth) = match f.u32(2)? {
                    0 => {
                        f.require_set(&[1, 2])?;
                        (false, BTreeSet::new(), 1)
                    }
                    1 => {
                        f.require_set(&[1, 2, 3])?;
                        let lhs = self.expression_type(f.u64(3)?)?;
                        if projection.projection.fields().len() != 1
                            || !projection.projection.fields()[0]
                                .codec
                                .same_scalar(&lhs.codec)
                        {
                            return Err(invalid("IN subquery scalar shape"));
                        }
                        (
                            lhs.codec.nullable || projection.projection.fields()[0].codec.nullable,
                            lhs.dependencies,
                            lhs.depth + 1,
                        )
                    }
                    _ => return Err(unsupported("subquery operation")),
                };
                self.expression(
                    ExpressionType {
                        codec: boolean(nullable),
                        bound_codec: 4,
                        depth,
                        aggregate: false,
                        dependencies,
                    },
                    borrowed.len(),
                )
            }
            15 => {
                let f = parse(&[(1, 2, true), (2, 2, true), (3, 2, true)])?;
                let condition = self.expression_type(f.u64(1)?)?;
                let a = self.expression_type(f.u64(2)?)?;
                let b = self.expression_type(f.u64(3)?)?;
                if condition.codec.id != 4
                    || !a.codec.same_scalar(&b.codec)
                    || a.bound_codec != b.bound_codec
                {
                    return Err(invalid("CASE operand types"));
                }
                let mut dependencies = a.dependencies;
                dependencies.extend(b.dependencies);
                dependencies.extend(condition.dependencies);
                let mut codec = a.codec;
                codec.nullable |= b.codec.nullable;
                self.expression(
                    ExpressionType {
                        codec,
                        bound_codec: a.bound_codec,
                        depth: condition.depth.max(a.depth).max(b.depth) + 1,
                        aggregate: condition.aggregate || a.aggregate || b.aggregate,
                        dependencies,
                    },
                    borrowed.len(),
                )
            }
            16 => self.text_clause(borrowed),
            17 => {
                let f = parse(&[(1, 1, true), (2, 2, true)])?;
                let id = f.u32(1)?;
                let column = self.root.model.column(id)?;
                let value = self.expression_type(f.u64(2)?)?;
                if column.key_ordinal.is_some()
                    || column.generated
                    || self.assignments.contains(&id)
                    || !column.codec.same_scalar(&value.codec)
                    || !column.codec.nullable && value.codec.nullable
                    || value.aggregate
                    || !value.dependencies.is_empty()
                {
                    return Err(invalid("invalid root assignment"));
                }
                self.marker(borrowed.len())?;
                self.assignments.insert(id);
                Ok(0)
            }
            18 => {
                let f = parse(&[(1, 2, true), (2, 1, true), (3, 4, true), (4, 1, true)])?;
                let Node::Model(model) = self.node(f.u64(1)?)? else {
                    return Err(invalid("JOIN imported model token"));
                };
                let model = model.clone();
                let name = f.text(3)?;
                identifier(name)?;
                if f.u32(2)? != model.model.table_id() || f.u32(4)? > 1 {
                    return Err(invalid("JOIN table/kind"));
                }
                if self
                    .nodes
                    .iter()
                    .flatten()
                    .any(|n| matches!(n,Node::Alias{name:other,..} if other==name))
                {
                    return Err(invalid("duplicate JOIN alias"));
                }
                let token = self.append(
                    Node::Alias {
                        model,
                        name: name.to_owned(),
                        left: f.u32(4)? == 1,
                        provisional: true,
                    },
                    borrowed.len(),
                )?;
                self.reserved = Some(token as u32);
                Ok(token)
            }
            19 => {
                let f = parse(&[(1, 2, true)])?;
                let index = self.provisional(f.u64(1)?)?; // Abort must remain possible at the budget limit; it allocates no new state.
                self.invalidate_alias_expressions(index as u32 + 1);
                self.nodes[index] = None;
                self.reserved = None;
                Ok(0)
            }
            20 => {
                let f = parse(&[(1, 2, true)])?;
                let alias = f.u64(1)?;
                let index = self.index(alias)?;
                if !matches!(
                    self.node(alias)?,
                    Node::Alias {
                        left: true,
                        provisional: false,
                        ..
                    }
                ) {
                    return Err(invalid("JOIN MATCH requires committed left alias"));
                }
                self.expression(
                    ExpressionType {
                        codec: boolean(false),
                        bound_codec: 4,
                        depth: 1,
                        aggregate: false,
                        dependencies: BTreeSet::from([index as u32 + 1]),
                    },
                    borrowed.len(),
                )
            }
            21 => {
                let f = parse(&[(1, 2, true), (2, 2, true)])?;
                let mut expression = self.expression_type(f.u64(1)?)?;
                if !expression.aggregate {
                    return Err(invalid("OVER requires native aggregate/window expression"));
                }
                self.window(f.u64(2)?)?;
                expression.depth += 1; // B11 snapshots the corresponding native window now, not at render.
                self.expression(expression, borrowed.len())
            }
            22 => {
                let f = parse(&[(1, 3, true)])?;
                let timeout = f.u64(1)?;
                if timeout == 0 {
                    return Err(invalid("timeout must be finite and positive"));
                }
                self.marker(borrowed.len())?;
                self.timeout_ns = timeout;
                Ok(0)
            }
            _ => unreachable!(),
        }
    }
    fn marker(&mut self, bytes: usize) -> Result<u64> {
        self.append(Node::Clause, bytes)?;
        Ok(0)
    }
    fn provisional(&self, token: u64) -> Result<usize> {
        let index = self.index(token)?;
        if self.reserved != Some(index as u32 + 1)
            || !matches!(
                self.node(token)?,
                Node::Alias {
                    provisional: true,
                    ..
                }
            )
        {
            return Err(invalid("active provisional JOIN alias required"));
        }
        Ok(index)
    }
    fn invalidate_alias_expressions(&mut self, slot: u32) {
        for node in &mut self.nodes {
            if matches!(node,Some(Node::Expression(info)) if info.dependencies.contains(&slot)) {
                *node = None;
            }
        }
    }
    fn plan(&self, token: u64) -> Result<&Arc<PlanReference>> {
        match self.node(token)? {
            Node::Plan(plan) => Ok(plan),
            _ => Err(invalid("imported plan token required")),
        }
    }
    fn window(&self, token: u64) -> Result<Node> {
        if !self.capabilities.windows {
            return Err(unsupported("window capability"));
        }
        match self.node(token)? {
            node @ Node::Window { .. } => Ok(node.clone()),
            _ => Err(invalid("window token required")),
        }
    }
    fn window_clause(&mut self, bytes: &[u8]) -> Result<u64> {
        if !self.capabilities.windows {
            return Err(unsupported("window capability"));
        }
        let f = Fields::parse(
            bytes,
            6,
            &[
                (1, 1, true),
                (2, 2, false),
                (3, 2, false),
                (4, 1, false),
                (5, 1, false),
                (6, 1, false),
                (7, 1, false),
            ],
        )?;
        let action = f.u32(1)?;
        if action == 0 {
            f.require_set(&[1])?;
            return self.append(
                Node::Window {
                    partition_count: 0,
                    order_count: 0,
                    frame: None,
                },
                bytes.len(),
            );
        }
        let index = self.index(f.u64(2)?)?;
        let Node::Window {
            mut partition_count,
            mut order_count,
            mut frame,
        } = self.window(f.u64(2)?)?
        else {
            unreachable!()
        };
        match action {
            1 => {
                f.require_set(&[1, 2, 3])?;
                self.expression_type(f.u64(3)?)?;
                partition_count = partition_count
                    .checked_add(1)
                    .ok_or_else(|| capacity("window partition count"))?;
            }
            2 => {
                f.require_set(&[1, 2, 3, 4, 5])?;
                self.expression_type(f.u64(3)?)?;
                direction(&f, 4, 5)?;
                order_count = order_count
                    .checked_add(1)
                    .ok_or_else(|| capacity("window order count"))?;
            }
            3 => {
                f.require_set(&[1, 2, 6, 7])?;
                let start = f.u32(6)?;
                let end = f.u32(7)?;
                if start > 1 || !(1..=2).contains(&end) || start > end {
                    return Err(invalid("window frame endpoints"));
                }
                frame = Some((start, end));
            }
            _ => return Err(unsupported("window action")),
        }
        self.marker(bytes.len())?;
        self.nodes[index] = Some(Node::Window {
            partition_count,
            order_count,
            frame,
        });
        Ok(0)
    }
    fn aggregate_clause(&mut self, bytes: &[u8]) -> Result<u64> {
        let f = Fields::parse(
            bytes,
            6,
            &[
                (1, 1, true),
                (2, 2, false),
                (3, 1, true),
                (4, 2, false),
                (5, 1, true),
            ],
        )?;
        let function = f.u32(1)?;
        let distinct = f.flag(3)?;
        if function > 7 {
            return Err(unsupported("aggregate function"));
        }
        let input = if f.get(2).is_some() {
            Some(self.expression_type(f.u64(2)?)?)
        } else {
            None
        };
        if matches!(function, 0 | 6 | 7) && input.is_some()
            || matches!(function, 1..=5) && input.is_none()
            || matches!(function, 0 | 6 | 7) && distinct
        {
            return Err(invalid("aggregate operand set"));
        }
        if let Some(token) = f.get(4) {
            self.window(u64::from_le_bytes(token.try_into().unwrap()))?;
        }
        if matches!(function, 6 | 7) && !self.capabilities.windows {
            return Err(unsupported("ranking window capability"));
        }
        let nullable = matches!(function, 2..=5);
        let codec = self.root.codecs.get(f.u32(5)?, nullable)?;
        if matches!(function, 0 | 1 | 6 | 7) && codec.id != 3 {
            return Err(invalid("count/rank result codec must be Int64"));
        }
        if let Some(input) = &input {
            if input.aggregate {
                return Err(invalid("nested aggregate requires a subquery"));
            }
            if matches!(function, 2 | 3) && (!numeric(input.codec.id) || !numeric(codec.id)) {
                return Err(unsupported("numeric aggregate codec"));
            }
            if matches!(function, 4 | 5) && !codec.same_scalar(&input.codec) {
                return Err(invalid("min/max result codec"));
            }
        }
        let depth = input.as_ref().map_or(1, |info| info.depth + 1);
        let dependencies = input.map_or_else(BTreeSet::new, |info| info.dependencies);
        self.expression(
            ExpressionType {
                codec,
                bound_codec: f.u32(5)?,
                depth,
                aggregate: true,
                dependencies,
            },
            bytes.len(),
        )
    }
    fn text_clause(&mut self, bytes: &[u8]) -> Result<u64> {
        // Field3 is db-wire for LIKE operations, UTF8 for COLLATE. No ambiguous fallback.
        if bytes.len() < 16 {
            return Err(invalid("text record"));
        }
        let operation = crate::control::discriminant(bytes)?;
        if operation > 4 {
            return Err(unsupported("text operation"));
        }
        let f = Fields::parse(
            bytes,
            6,
            &[
                (1, 1, true),
                (2, 2, true),
                (3, if operation == 4 { 4 } else { 6 }, true),
                (4, 1, true),
            ],
        )?;
        let mut input = self.expression_type(f.u64(2)?)?;
        if input.codec.id != 5 {
            return Err(invalid("text input codec"));
        }
        let escape = f.u32(4)?;
        if char::from_u32(escape).is_none() || escape == 0 {
            return Err(invalid("LIKE escape Unicode scalar"));
        }
        if operation == 4 {
            let name = std::str::from_utf8(f.bytes(3)?).map_err(|_| invalid("collation UTF8"))?;
            let allowed = match self.source().profile {
                Profile::Sqlite => matches!(name, "BINARY" | "NOCASE" | "RTRIM"),
                Profile::PostgreSql => matches!(name, "C" | "POSIX" | "default"),
            };
            if !allowed {
                return Err(unsupported("collation is not allowlisted"));
            }
        } else {
            let (codec, _, _) = self.decode_values(f.bytes(3)?, Some(1))?;
            if codec.id != 5 {
                return Err(invalid("text argument codec"));
            }
            input.codec = boolean(input.codec.nullable || codec.nullable);
            input.bound_codec = 4;
        }
        input.depth += 1;
        let snapshot = Arc::<[u8]>::from(f.bytes(3)?);
        let token = self.expression(input, bytes.len())?;
        self.values.push(snapshot);
        Ok(token)
    }
}
fn direction(fields: &Fields<'_>, direction: u16, null_order: u16) -> Result<()> {
    if fields.u32(direction)? > 1 || fields.u32(null_order)? > 1 {
        Err(invalid("order direction/null placement"))
    } else {
        Ok(())
    }
}
fn token_vector(bytes: &[u8]) -> Result<Vec<u64>> {
    if bytes.is_empty()
        || !bytes.len().is_multiple_of(8)
        || bytes.len() / 8 > nativeweave_data_contracts::MAX_NODES
    {
        return Err(invalid("token vector count"));
    }
    Ok(bytes
        .as_chunks::<8>()
        .0
        .iter()
        .map(|b| u64::from_le_bytes(*b))
        .collect())
}
fn identifier(value: &str) -> Result<()> {
    if value.is_empty() || value.len() > 128 || value.chars().any(|c| c == '\0' || c.is_control()) {
        Err(invalid("alias/name bounds"))
    } else {
        Ok(())
    }
}
pub(crate) fn model_row_matches(
    model: &ModelBinding,
    projection: &nativeweave_data_contracts::Projection,
) -> Result<bool> {
    let metadata: serde_json::Value =
        serde_json::from_slice(model.model.bytes()).map_err(|_| invalid("canonical model"))?;
    let ids = metadata["projection"]
        .as_array()
        .ok_or_else(|| invalid("model projection"))?;
    if ids.len() != projection.fields().len() {
        return Ok(false);
    }
    for (id, field) in ids.iter().zip(projection.fields()) {
        let id = id.as_u64().ok_or_else(|| invalid("column ID"))? as u32;
        let column = model.model.column(id)?;
        if column.name != field.name || column.codec != field.codec {
            return Ok(false);
        }
    }
    Ok(true)
}

fn model_row_codecs_match(
    model: &ModelBinding,
    projection: &nativeweave_data_contracts::Projection,
) -> Result<bool> {
    let metadata: serde_json::Value =
        serde_json::from_slice(model.model.bytes()).map_err(|_| invalid("canonical model"))?;
    let ids = metadata["projection"]
        .as_array()
        .ok_or_else(|| invalid("model projection"))?;
    if ids.len() != projection.fields().len() {
        return Ok(false);
    }
    for (id, field) in ids.iter().zip(projection.fields()) {
        let id = id.as_u64().ok_or_else(|| invalid("column ID"))? as u32;
        if model.model.column(id)?.codec != field.codec {
            return Ok(false);
        }
    }
    Ok(true)
}
