use crate::*;
use nativeweave_data_contracts::{
    Codec,
    wire::{Batch, Descriptor, Expectation},
};
use std::{
    collections::{BTreeMap, BTreeSet},
    sync::{
        Arc,
        atomic::{AtomicU32, Ordering},
    },
};
pub(crate) static NEXT_ARENA: AtomicU32 = AtomicU32::new(1);
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum QueryState {
    Mutable,
    Sealed,
    Submitted,
    Poisoned,
    Closed,
}
#[derive(Clone, Debug)]
pub struct ExpressionType {
    pub codec: Codec,
    pub bound_codec: u32,
    pub depth: u32,
    pub aggregate: bool,
    pub dependencies: BTreeSet<u32>,
}
#[derive(Clone, Debug)]
pub(crate) enum Node {
    Expression(ExpressionType),
    Model(ModelBinding),
    Alias {
        model: ModelBinding,
        name: String,
        left: bool,
        provisional: bool,
    },
    Window {
        partition_count: u32,
        order_count: u32,
        frame: Option<(u32, u32)>,
    },
    Plan(Arc<PlanReference>),
    Cte(Arc<CteReference>),
    Clause,
}
/// Immutable query shape reference. This is binding metadata, not a SQL executor
/// or substitute SeaQuery tree. B11 associates it with its actual native plan.
#[derive(Clone, Debug)]
pub struct PlanReference {
    pub source: SourceIdentity,
    pub terminal: u32,
    pub parameters: BTreeMap<u32, ParameterIdentity>,
    pub projection: Option<ProjectionBinding>,
    pub timeout_ns: u64,
    pub nodes: usize,
    pub owned_bytes: usize,
    pub(crate) dependencies: Vec<Node>,
    pub(crate) values: Vec<Arc<[u8]>>,
}
/// Query-local generation tokens and semantic metadata only. SeaQuery statements
/// remain B11-owned; each successful validator call authorizes one exact native
/// operation. If native mutation cannot roll back safely, B11 calls poison().
pub struct QueryArena {
    pub(crate) generation: u32,
    pub(crate) root: ModelBinding,
    pub(crate) registry: Arc<ParameterRegistry>,
    pub(crate) limits: Limits,
    pub(crate) capabilities: Capabilities,
    pub(crate) state: QueryState,
    pub(crate) nodes: Vec<Option<Node>>,
    pub(crate) values: Vec<Arc<[u8]>>,
    pub(crate) bytes: usize,
    pub(crate) parameters: BTreeMap<u32, ParameterIdentity>,
    pub(crate) projection: Option<ProjectionBinding>,
    pub(crate) reserved: Option<u32>,
    pub(crate) timeout_ns: u64,
    pub(crate) assignments: BTreeSet<u32>,
    pub(crate) cte_names: BTreeSet<String>,
    pub(crate) has_join: bool,
    pub(crate) has_group: bool,
    pub(crate) has_paging: bool,
}
impl QueryArena {
    pub fn new(
        root: ModelBinding,
        registry: Arc<ParameterRegistry>,
        table_id: u32,
        limits: Limits,
        capabilities: Capabilities,
        timeout_ns: u64,
    ) -> Result<Self> {
        limits.validate()?;
        root.source.validate()?;
        if root.model.table_id() != table_id || registry.source != root.source || timeout_ns == 0 {
            return Err(invalid("query identity/default timeout mismatch"));
        }
        if !capabilities.query {
            return Err(unsupported("db.query.v1 required"));
        }
        let generation = NEXT_ARENA
            .try_update(Ordering::Relaxed, Ordering::Relaxed, |n| n.checked_add(1))
            .map_err(|_| capacity("arena generation exhausted"))?;
        Ok(Self {
            generation,
            root,
            registry,
            limits,
            capabilities,
            state: QueryState::Mutable,
            nodes: Vec::new(),
            values: Vec::new(),
            bytes: 0,
            parameters: BTreeMap::new(),
            projection: None,
            reserved: None,
            timeout_ns,
            assignments: BTreeSet::new(),
            cte_names: BTreeSet::new(),
            has_join: false,
            has_group: false,
            has_paging: false,
        })
    }
    pub fn state(&self) -> QueryState {
        self.state
    }
    pub fn counts(&self) -> (usize, usize) {
        (self.nodes.len(), self.bytes)
    }
    pub fn source(&self) -> SourceIdentity {
        self.root.source
    }
    pub fn poison(&mut self) {
        if self.state != QueryState::Closed {
            self.state = QueryState::Poisoned;
        }
    }
    pub fn close(&mut self) {
        self.state = QueryState::Closed;
        self.nodes.clear();
        self.values.clear();
        self.parameters.clear();
        self.projection = None;
        self.reserved = None;
        self.bytes = 0;
    }
    pub(crate) fn mutable(&self, expression: bool) -> Result<()> {
        if self.state == QueryState::Poisoned {
            return Err(poisoned());
        }
        if self.state != QueryState::Mutable {
            return Err(state("query is not mutable"));
        }
        if self.reserved.is_some() && !expression {
            return Err(state(
                "only expression construction is admitted during JOIN reservation",
            ));
        }
        Ok(())
    }
    pub(crate) fn quota(&self, bytes: usize, depth: u32) -> Result<()> {
        if self.nodes.len() >= self.limits.nodes
            || bytes > self.limits.bytes.saturating_sub(self.bytes)
            || depth > self.limits.depth
        {
            Err(capacity("query node/depth/byte limit"))
        } else {
            Ok(())
        }
    }
    pub(crate) fn append(&mut self, node: Node, bytes: usize) -> Result<u64> {
        self.quota(bytes, 0)?;
        self.nodes.push(Some(node));
        self.bytes += bytes;
        Ok(((self.generation as u64) << 32) | self.nodes.len() as u64)
    }
    pub(crate) fn index(&self, token: u64) -> Result<usize> {
        if token == 0 || (token >> 32) as u32 != self.generation {
            return Err(invalid("token belongs to another query generation"));
        }
        let index = (token as u32)
            .checked_sub(1)
            .ok_or_else(|| invalid("zero arena slot"))? as usize;
        if self.nodes.get(index).is_none_or(Option::is_none) {
            return Err(invalid("stale or wrong arena slot"));
        }
        Ok(index)
    }
    pub(crate) fn node(&self, token: u64) -> Result<&Node> {
        Ok(self.nodes[self.index(token)?].as_ref().unwrap())
    }
    pub fn expression_type(&self, token: u64) -> Result<ExpressionType> {
        if matches!(self.state, QueryState::Closed | QueryState::Poisoned) {
            return Err(state("closed/poisoned arena"));
        }
        match self.node(token)? {
            Node::Expression(info) => Ok(info.clone()),
            _ => Err(invalid("token is not an expression")),
        }
    }
    pub(crate) fn expression(&mut self, info: ExpressionType, bytes: usize) -> Result<u64> {
        self.mutable(true)?;
        self.quota(bytes, info.depth)?;
        self.append(Node::Expression(info), bytes)
    }
    pub fn column(&mut self, alias: u64, column_id: u32) -> Result<u64> {
        self.mutable(true)?;
        let (model, left, dependency) = if alias == 0 {
            (self.root.clone(), false, None)
        } else {
            match self.node(alias)? {
                Node::Alias {
                    model,
                    left,
                    provisional,
                    ..
                } => {
                    let index = self.index(alias)? as u32;
                    if *provisional && self.reserved != Some(index) {
                        return Err(state("inactive provisional alias"));
                    }
                    (model.clone(), *left && !*provisional, Some(index))
                }
                _ => return Err(invalid("column requires alias token")),
            }
        };
        let column = model.model.column(column_id)?;
        let mut codec = column.codec.clone();
        codec.nullable |= left;
        let bound_codec = model.codecs.bound(&codec)?;
        let dependencies = dependency.into_iter().collect();
        self.expression(
            ExpressionType {
                codec,
                bound_codec,
                depth: 1,
                aggregate: false,
                dependencies,
            },
            0,
        )
    }
    pub(crate) fn decode_values(
        &self,
        bytes: &[u8],
        exact: Option<u32>,
    ) -> Result<(Codec, u32, Batch)> {
        if bytes.len() < 112 || bytes.len() > self.limits.bytes {
            return Err(invalid("expression wire size"));
        }
        let bound = u32::from_le_bytes(bytes[104..108].try_into().unwrap());
        let flags = u16::from_le_bytes(bytes[102..104].try_into().unwrap());
        if flags > 1 {
            return Err(invalid("value flags"));
        }
        let codec = self.root.codecs.get(bound, flags == 1)?;
        let expected = Expectation {
            kind: 3,
            profile: self.root.source.profile,
            hash: [0; 32],
            entity_id: 0,
            projection_id: 0,
            descriptors: vec![Descriptor {
                id: 1,
                codec: codec.clone(),
                bound_codec: bound,
            }],
            exact_rows: exact,
        };
        let batch = Batch::parse(bytes, expected)?;
        if batch.rows() as usize > self.limits.parameters {
            return Err(capacity("expression collection size"));
        }
        Ok((codec, bound, batch))
    }
    pub fn value(&mut self, borrowed: &[u8]) -> Result<u64> {
        self.mutable(true)?;
        let (codec, bound_codec, _) = self.decode_values(borrowed, Some(1))?;
        self.quota(borrowed.len(), 1)?;
        let snapshot = Arc::<[u8]>::from(borrowed);
        let token = self.expression(
            ExpressionType {
                codec,
                bound_codec,
                depth: 1,
                aggregate: false,
                dependencies: BTreeSet::new(),
            },
            borrowed.len(),
        )?;
        self.values.push(snapshot);
        Ok(token)
    }
    pub fn parameter(&mut self, identity: ParameterIdentity) -> Result<u64> {
        self.mutable(true)?;
        identity.validate(&self.root.codecs)?;
        self.quota(identity.name.len(), 1)?;
        let mut parameters = self.parameters.clone();
        merge_parameter(&mut parameters, &identity, self.limits.parameters)?;
        self.registry.register(&identity)?;
        let token = self.expression(
            ExpressionType {
                codec: identity.codec.clone(),
                bound_codec: identity.bound_codec,
                depth: 1,
                aggregate: false,
                dependencies: BTreeSet::new(),
            },
            identity.name.len(),
        )?;
        self.parameters = parameters;
        Ok(token)
    }
    pub fn binary(&mut self, opcode: u32, left: u64, right: u64) -> Result<u64> {
        self.mutable(true)?;
        let a = self.expression_type(left)?;
        let b = self.expression_type(right)?;
        if !(1..=14).contains(&opcode) {
            return Err(unsupported("unknown required binary opcode"));
        }
        if !a.codec.same_scalar(&b.codec) || a.bound_codec != b.bound_codec {
            return Err(invalid("binary operand codec mismatch"));
        }
        let mut codec = a.codec.clone();
        match opcode {
            1..=6 => codec = boolean(a.codec.nullable || b.codec.nullable),
            7..=10 if !numeric(codec.id) => return Err(unsupported("arithmetic codec")),
            11 | 12 if codec.id != 4 => return Err(invalid("boolean operand required")),
            13 => {
                if !self.capabilities.null_safe_equal {
                    return Err(unsupported("null-safe equality capability"));
                }
                codec = boolean(false);
            }
            14 => codec.nullable = a.codec.nullable && b.codec.nullable,
            _ => codec.nullable = a.codec.nullable || b.codec.nullable,
        }
        let dependencies = a.dependencies.union(&b.dependencies).copied().collect();
        let depth = a
            .depth
            .max(b.depth)
            .checked_add(1)
            .ok_or_else(|| capacity("query depth overflow"))?;
        let bound_codec = if codec.id == 4 { 4 } else { a.bound_codec };
        self.expression(
            ExpressionType {
                codec,
                bound_codec,
                depth,
                aggregate: a.aggregate || b.aggregate,
                dependencies,
            },
            0,
        )
    }
    pub fn unary(&mut self, opcode: u32, input: u64) -> Result<u64> {
        self.mutable(true)?;
        let mut info = self.expression_type(input)?;
        match opcode {
            1 | 2 => {
                info.codec = boolean(false);
                info.bound_codec = 4;
            }
            3 => {
                if info.codec.id != 4 {
                    return Err(invalid("NOT requires boolean"));
                }
            }
            4 => {
                if info.codec.nullable {
                    return Err(invalid("NullableLift requires non-null input"));
                }
                info.codec.nullable = true;
            }
            _ => return Err(unsupported("unknown required unary opcode")),
        }
        info.depth = info
            .depth
            .checked_add(1)
            .ok_or_else(|| capacity("depth overflow"))?;
        self.expression(info, 0)
    }
    pub fn list(&mut self, opcode: u32, input: u64, borrowed: &[u8]) -> Result<u64> {
        self.mutable(true)?;
        if !(1..=3).contains(&opcode) {
            return Err(unsupported("unknown required list opcode"));
        }
        let input = self.expression_type(input)?;
        let (codec, bound, batch) =
            self.decode_values(borrowed, if opcode == 3 { Some(2) } else { None })?;
        if !codec.same_scalar(&input.codec) || bound != input.bound_codec {
            return Err(invalid("list element codec mismatch"));
        }
        self.quota(borrowed.len(), input.depth + 1)?;
        let nullable = batch.rows() != 0 && (codec.nullable || input.codec.nullable);
        let snapshot = Arc::<[u8]>::from(borrowed);
        let token = self.expression(
            ExpressionType {
                codec: boolean(nullable),
                bound_codec: 4,
                depth: input.depth + 1,
                aggregate: false,
                dependencies: input.dependencies,
            },
            borrowed.len(),
        )?;
        self.values.push(snapshot);
        Ok(token)
    }
    pub fn import_model(&mut self, model: ModelBinding) -> Result<u64> {
        self.mutable(false)?;
        model.ensure_source(self.root.source)?;
        self.append(Node::Model(model), 0)
    }
    pub fn import_plan(&mut self, plan: Arc<PlanReference>) -> Result<u64> {
        self.mutable(false)?;
        if plan.source != self.root.source {
            return Err(state("imported plan from another source"));
        }
        self.quota(plan.owned_bytes, 0)?;
        let mut parameters = self.parameters.clone();
        for parameter in plan.parameters.values() {
            merge_parameter(&mut parameters, parameter, self.limits.parameters)?;
        }
        let bytes = plan.owned_bytes;
        let token = self.append(Node::Plan(plan), bytes)?;
        self.parameters = parameters;
        Ok(token)
    }
    pub fn seal(&mut self, terminal: u32) -> Result<Arc<PlanReference>> {
        self.mutable(false)?;
        if terminal > 8 {
            return Err(unsupported("unknown query terminal"));
        }
        if matches!(terminal, 0..=3 | 8) && self.projection.is_none() {
            return Err(invalid("selected projection required"));
        }
        if matches!(terminal, 6 | 7) && (self.has_join || self.has_group || self.has_paging) {
            return Err(unsupported("set mutation requires root-only filter"));
        }
        if terminal == 6 && self.assignments.is_empty() {
            return Err(invalid("update assignments required"));
        }
        let plan = Arc::new(PlanReference {
            source: self.root.source,
            terminal,
            parameters: self.parameters.clone(),
            projection: self.projection.clone(),
            timeout_ns: self.timeout_ns,
            nodes: self.nodes.len(),
            owned_bytes: self.bytes,
            dependencies: self.nodes.iter().flatten().cloned().collect(),
            values: self.values.clone(),
        });
        self.state = QueryState::Sealed;
        Ok(plan)
    }
    pub fn mark_submitted(&mut self) -> Result<()> {
        if self.state != QueryState::Sealed {
            return Err(state("only sealed query may submit"));
        }
        self.state = QueryState::Submitted;
        Ok(())
    }
}
pub(crate) fn boolean(nullable: bool) -> Codec {
    Codec {
        id: 4,
        configuration: None,
        nullable,
    }
}
pub(crate) fn numeric(id: u32) -> bool {
    matches!(id,1..=3|11..=13|15)
}
pub(crate) fn merge_parameter(
    destination: &mut BTreeMap<u32, ParameterIdentity>,
    value: &ParameterIdentity,
    limit: usize,
) -> Result<()> {
    if let Some(existing) = destination.get(&value.id) {
        return if existing == value {
            Ok(())
        } else {
            Err(invalid("parameter ID metadata collision"))
        };
    }
    if destination.values().any(|p| p.name == value.name) {
        return Err(invalid("different parameter IDs share a composed name"));
    }
    if destination.len() == limit {
        return Err(capacity("parameter count limit"));
    }
    destination.insert(value.id, value.clone());
    Ok(())
}
