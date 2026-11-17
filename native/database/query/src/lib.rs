//! Actual SeaQuery state, bound to B09's canonical arena and B01 wire identities.
//! B04 owns admission/runtime/provider operations; B07 remains the sole SQLx binder.
//! No query, parameter, runtime, connection or application entity is process-global.
mod cache;
mod clauses;
mod expressions;
mod plan;
mod wire;

pub use cache::{CompiledShape, ShapeCache};
use nativeweave_data_contracts::{Codec, Profile};
use nativeweave_interop_abi::{
    NW_DOMAIN_ERROR, NW_INVALID_ARGUMENT, NW_INVALID_STATE, NW_NOT_SUPPORTED,
};
pub use nativeweave_query_binding::{BindingError, Result};
use nativeweave_query_binding::{
    Capabilities, Limits, ModelBinding, ParameterRegistry, QueryArena, QueryState,
};
pub use plan::{NativeCte, NativePlan, RenderedQuery};
use sea_query::{Expr, SelectStatement, WindowStatement, WithClause};
use std::{collections::BTreeMap, sync::Arc};

/// B04 puts this owner behind its fail-fast single-writer admission, never a global mutex.
pub struct NativeQuery {
    pub(crate) arena: QueryArena,
    pub(crate) root: ModelBinding,
    pub(crate) limits: Limits,
    pub(crate) capabilities: Capabilities,
    pub(crate) data: NativeState,
}

#[derive(Clone, Debug)]
pub(crate) struct NativeExpression {
    expr: Expr,
    /// PostgreSQL widens aggregate results after OVER, never inside it.
    window_cast: Option<(Expr, &'static str)>,
    /// Expanded SeaQuery expression size, including cloned children.
    nodes: usize,
}

#[derive(Clone, Debug)]
pub(crate) enum SlotValue {
    Literal(Option<Arc<[u8]>>),
    Parameter(u32),
}

#[derive(Clone, Debug)]
pub(crate) struct Slot {
    codec: Codec,
    bound_codec: u32,
    value: SlotValue,
}

#[derive(Clone)]
pub(crate) struct Alias {
    model: ModelBinding,
    name: String,
    left: bool,
    marker: String,
}

#[derive(Clone)]
pub(crate) struct NativeState {
    statement: SelectStatement,
    expressions: BTreeMap<u32, NativeExpression>,
    aliases: BTreeMap<u32, Alias>,
    models: BTreeMap<u32, ModelBinding>,
    plans: BTreeMap<u32, Arc<NativePlan>>,
    ctes: BTreeMap<u32, Arc<NativeCte>>,
    windows: BTreeMap<u32, (WindowStatement, usize)>,
    slots: BTreeMap<u64, Arc<Slot>>,
    predicates: Vec<Expr>,
    assignments: Vec<(String, Expr)>,
    with_clause: WithClause,
    has_cte: bool,
    recursive_cte: bool,
    statement_nodes: usize,
    set_operations: Vec<(u32, Arc<NativePlan>)>,
    aggregate_nodes: usize,
    grouped: bool,
}

impl NativeQuery {
    /// Creates one mutable, source-bound native builder without executing SQL.
    pub fn new(
        root: ModelBinding,
        registry: Arc<ParameterRegistry>,
        table_id: u32,
        limits: Limits,
        capabilities: Capabilities,
        timeout_ns: u64,
    ) -> Result<Self> {
        let arena = QueryArena::new(
            root.clone(),
            registry,
            table_id,
            limits,
            capabilities,
            timeout_ns,
        )?;
        let mut statement = SelectStatement::new();
        statement.from(table_ref(&root)?);
        Ok(Self {
            arena,
            root,
            limits,
            capabilities,
            data: NativeState {
                statement,
                expressions: BTreeMap::new(),
                aliases: BTreeMap::new(),
                models: BTreeMap::new(),
                plans: BTreeMap::new(),
                ctes: BTreeMap::new(),
                windows: BTreeMap::new(),
                slots: BTreeMap::new(),
                predicates: Vec::new(),
                assignments: Vec::new(),
                with_clause: WithClause::new(),
                has_cte: false,
                recursive_cte: false,
                statement_nodes: 1,
                set_operations: Vec::new(),
                aggregate_nodes: 0,
                grouped: false,
            },
        })
    }

    /// Creating source generation; never a native pointer or inferred global runtime.
    pub fn source(&self) -> nativeweave_query_binding::SourceIdentity {
        self.root.source
    }
    /// Canonical state; ordinary plan submission is observed through B09.
    pub fn state(&self) -> QueryState {
        self.arena.state()
    }
    /// Bounded token/encoded-value charges plus actual expanded SeaQuery node charge.
    pub fn counts(&self) -> (usize, usize, usize) {
        let (nodes, bytes) = self.arena.counts();
        (nodes, bytes, self.data.nodes())
    }
    /// Invalidates mutations after a caught engine panic; cleanup remains legal.
    pub fn poison(&mut self) {
        self.arena.poison();
    }
    /// Drops query roots. Imported plans/active executions retain their own immutable owners.
    pub fn close(&mut self) {
        self.arena.close();
        self.data.expressions.clear();
        self.data.aliases.clear();
        self.data.models.clear();
        self.data.plans.clear();
        self.data.ctes.clear();
        self.data.windows.clear();
        self.data.slots.clear();
        self.data.statement = SelectStatement::new();
        self.data.predicates.clear();
        self.data.assignments.clear();
        self.data.with_clause = WithClause::new();
        self.data.statement_nodes = 0;
        self.data.set_operations.clear();
    }
    /// Copies mutable native state with a fresh B09 token generation; source remains mutable.
    pub fn clone_selected(&self) -> Result<Self> {
        Ok(Self {
            arena: self.arena.clone_selected()?,
            root: self.root.clone(),
            limits: self.limits,
            capabilities: self.capabilities,
            data: self.data.clone(),
        })
    }
    /// Explicit immutable model import; no raw pointer is encoded in a clause.
    pub fn import_model(&mut self, model: ModelBinding) -> Result<u64> {
        let token = self.arena.import_model(model.clone())?;
        self.data.models.insert(token as u32, model);
        Ok(token)
    }
    /// Retains an immutable same-source native plan. Parameter identities and literal slots remain distinct.
    pub fn import_plan(&mut self, plan: Arc<NativePlan>) -> Result<u64> {
        self.data.check_import(&plan.data, self.limits)?;
        let token = self.arena.import_plan(Arc::clone(&plan.binding))?;
        self.data
            .slots
            .extend(plan.data.slots.iter().map(|(k, v)| (*k, Arc::clone(v))));
        self.data.plans.insert(token as u32, plan);
        Ok(token)
    }
    /// Retains a validated immutable CTE created from a selected mutable query.
    pub fn import_cte(&mut self, cte: Arc<NativeCte>) -> Result<u64> {
        self.data.check_import(&cte.body.data, self.limits)?;
        let token = self.arena.import_cte(Arc::clone(&cte.binding))?;
        self.data
            .slots
            .extend(cte.body.data.slots.iter().map(|(k, v)| (*k, Arc::clone(v))));
        self.data.ctes.insert(token as u32, cte);
        Ok(token)
    }
    pub(crate) fn expression(&self, token: u64) -> Result<NativeExpression> {
        self.arena.expression_type(token)?;
        self.data
            .expressions
            .get(&(token as u32))
            .cloned()
            .ok_or_else(|| state("missing native expression"))
    }
    pub(crate) fn check_expression(&self, nodes: usize, value_bytes: usize) -> Result<()> {
        self.data.check_extra(nodes, value_bytes, self.limits)
    }
    pub(crate) fn put_expression(&mut self, token: u64, expression: NativeExpression) -> u64 {
        self.data.expressions.insert(token as u32, expression);
        token
    }
}
impl NativeState {
    fn nodes(&self) -> usize {
        self.statement_nodes
            + self.expressions.values().map(|e| e.nodes).sum::<usize>()
            + self.windows.values().map(|(_, n)| *n).sum::<usize>()
    }
    fn literal_bytes(&self) -> usize {
        self.slots
            .values()
            .map(|s| match &s.value {
                SlotValue::Literal(Some(v)) => v.len(),
                _ => 0,
            })
            .sum()
    }
    fn check_extra(&self, nodes: usize, bytes: usize, limits: Limits) -> Result<()> {
        if nodes > limits.nodes.saturating_sub(self.nodes())
            || bytes > limits.bytes.saturating_sub(self.literal_bytes())
        {
            Err(capacity("expanded native query/value budget exhausted"))
        } else {
            Ok(())
        }
    }
    fn check_import(&self, incoming: &Self, limits: Limits) -> Result<()> {
        let extra = incoming
            .slots
            .iter()
            .filter(|(id, _)| !self.slots.contains_key(id))
            .map(|(_, s)| match &s.value {
                SlotValue::Literal(Some(v)) => v.len(),
                _ => 0,
            })
            .sum();
        self.check_extra(incoming.statement_nodes, extra, limits)
    }
}

fn invalid(detail: &'static str) -> BindingError {
    BindingError {
        status: NW_INVALID_ARGUMENT,
        detail,
    }
}
fn state(detail: &'static str) -> BindingError {
    BindingError {
        status: NW_INVALID_STATE,
        detail,
    }
}
fn unsupported(detail: &'static str) -> BindingError {
    BindingError {
        status: NW_NOT_SUPPORTED,
        detail,
    }
}
fn capacity(detail: &'static str) -> BindingError {
    BindingError {
        status: NW_DOMAIN_ERROR,
        detail,
    }
}

fn names(model: &ModelBinding) -> Result<(Option<String>, String)> {
    let value: serde_json::Value =
        serde_json::from_slice(model.model.bytes()).map_err(|_| invalid("canonical model"))?;
    let table = value["table"]
        .as_str()
        .ok_or_else(|| invalid("table name"))?
        .to_owned();
    Ok((value["schema"].as_str().map(str::to_owned), table))
}
fn table_ref(model: &ModelBinding) -> Result<sea_query::TableRef> {
    use sea_query::IntoTableRef;
    let (schema, table) = names(model)?;
    Ok(match schema {
        Some(schema) => {
            (sea_query::Alias::new(schema), sea_query::Alias::new(table)).into_table_ref()
        }
        None => sea_query::Alias::new(table).into_table_ref(),
    })
}
