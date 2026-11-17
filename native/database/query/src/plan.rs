use crate::*;
use nativeweave_data_contracts::wire::{Batch, Descriptor, Expectation};
use nativeweave_query_binding::{CteReference, PlanReference, ProjectionBinding, SourceIdentity};
use sea_query::{
    Alias as SqlAlias, ExprTrait, Func, PostgresQueryBuilder, Query, SqliteQueryBuilder, Value,
    Values,
};
use sha2::{Digest, Sha256};
use std::sync::OnceLock;

/// Immutable native shape; executions receive separate parameter snapshots.
pub struct NativePlan {
    pub binding: Arc<PlanReference>,
    pub(crate) data: NativeState,
    pub(crate) limits: Limits,
    compiled: OnceLock<Result<CompiledPlan>>,
}
pub(crate) struct CompiledPlan {
    pub(crate) shape: Arc<CompiledShape>,
    arguments: Vec<Arc<Slot>>,
}
/// Immutable native CTE snapshot; ordinary changes to its body query cannot change it.
pub struct NativeCte {
    pub binding: Arc<CteReference>,
    pub(crate) body: Arc<NativePlan>,
}
/// An owned provider command. B04 passes it to B02/B03, which call B07 exactly once.
pub struct RenderedQuery {
    pub sql: String,
    pub parameter_bytes: Vec<u8>,
    pub parameter_expectation: Expectation,
    pub projection: Option<ProjectionBinding>,
    pub terminal: u32,
    pub timeout_ns: u64,
}
impl NativeQuery {
    pub fn seal(&mut self, terminal: u32) -> Result<Arc<NativePlan>> {
        if self.data.grouped {
            for (node, aggregate) in &self.data.selected_nodes {
                if !aggregate {
                    let expression = &self
                        .data
                        .expressions
                        .get(node)
                        .ok_or_else(|| state("selected native node"))?
                        .expr;
                    if !self.data.group_nodes.iter().any(|group| {
                        self.data
                            .expressions
                            .get(group)
                            .is_some_and(|value| &value.expr == expression)
                    }) {
                        return Err(invalid("nonaggregated projection must be a grouping key"));
                    }
                }
            }
        }
        // All native state is already built. B09 atomically freezes the validated shape.
        let binding = self.arena.seal(terminal)?;
        Ok(Arc::new(NativePlan {
            binding,
            data: self.data.clone(),
            limits: self.limits,
            compiled: OnceLock::new(),
        }))
    }
}
impl NativeCte {
    pub fn create(
        declared: ModelBinding,
        table_id: u32,
        body: &NativeQuery,
        recursive: u32,
    ) -> Result<Arc<Self>> {
        // B09 owns shape/type/source validation; B11 owns actual recursive AST
        // structure. Do not let an arbitrary query become recursive by a flag.
        if recursive == 1 {
            let is_declared = |model: &ModelBinding| model.model.hash() == declared.model.hash();
            let refs = |plan: &NativePlan| {
                usize::from(is_declared(&plan.binding.root))
                    + plan
                        .data
                        .aliases
                        .values()
                        .filter(|a| is_declared(&a.model))
                        .count()
            };
            let nested_refs = |data: &NativeState| {
                data.subqueries
                    .iter()
                    .map(|plan| {
                        recursive_references(&plan.binding.root, &plan.data, declared.model.hash())
                    })
                    .sum::<usize>()
            };
            let anchor_refs = nested_refs(&body.data)
                + usize::from(is_declared(&body.root))
                + body
                    .data
                    .aliases
                    .values()
                    .filter(|a| is_declared(&a.model))
                    .count();
            let Some((operation, member)) = body.data.set_operations.first() else {
                return Err(invalid("recursive CTE requires anchor UNION member"));
            };
            if anchor_refs != 0
                || body.data.set_operations.len() != 1
                || *operation > 1
                || refs(member) != 1
                || nested_refs(&member.data) != 0
                || !member.data.set_operations.is_empty()
                || !member.data.ctes.is_empty()
                || member.data.grouped
                || member.data.aggregate_nodes != 0
                || !member.data.windows.is_empty()
            {
                return Err(invalid(
                    "recursive CTE requires one supported self-reference",
                ));
            }
        }
        let binding = CteReference::create(declared, table_id, &body.arena, recursive)?;
        let mut clone = body.clone_selected()?;
        let snapshot = clone.seal(8)?;
        Ok(Arc::new(Self {
            binding,
            body: snapshot,
        }))
    }
}
impl NativePlan {
    /// B04 must reject row-lock execution without its live explicit transaction,
    /// before admission/render or dispatch. The requirement follows nested plans.
    pub fn requires_transaction(&self) -> bool {
        self.data.requires_transaction
    }

    /// Validates and snapshots one execution before provider dispatch. Ordinary plans submit once;
    /// prepared shapes use a fresh owned snapshot every time. Native compilation is cached per plan.
    pub fn render(
        self: &Arc<Self>,
        session: SourceIdentity,
        values: &[u8],
    ) -> Result<RenderedQuery> {
        let compiled = self.compiled()?;
        self.render_compiled(session, values, &compiled.shape, &compiled.arguments)
    }
    /// Uses the source-owned bounded shape cache; no execution values enter that cache.
    pub fn render_cached(
        self: &Arc<Self>,
        session: SourceIdentity,
        values: &[u8],
        cache: &mut ShapeCache,
    ) -> Result<RenderedQuery> {
        let compiled = self.compiled()?;
        let shape = cache.acquire(self)?;
        self.render_compiled(session, values, &shape, &compiled.arguments)
    }
    fn render_compiled(
        self: &Arc<Self>,
        session: SourceIdentity,
        values: &[u8],
        shape: &CompiledShape,
        arguments: &[Arc<Slot>],
    ) -> Result<RenderedQuery> {
        let admitted = self
            .binding
            .execution_arguments(session, values, self.limits)?;
        let batch = Batch::parse(&admitted.parameters, self.binding.parameter_expectation())?;
        let parameters = self
            .binding
            .parameters
            .values()
            .enumerate()
            .map(|(i, p)| batch.cell(0, i).map(|b| (p.id, b.map(Arc::<[u8]>::from))))
            .collect::<std::result::Result<BTreeMap<_, _>, _>>()?;
        let cells = arguments
            .iter()
            .map(|slot| match &slot.value {
                SlotValue::Literal(v) => Ok(v.clone()),
                SlotValue::Parameter(id) => parameters
                    .get(id)
                    .cloned()
                    .ok_or_else(|| invalid("missing execution parameter")),
            })
            .collect::<Result<Vec<_>>>()?;
        let refs = cells.iter().map(|v| v.as_deref()).collect::<Vec<_>>();
        let bytes = Batch::encode(&shape.expectation, 1, &refs)?;
        if bytes.len() > self.limits.bytes {
            return Err(capacity("rendered command byte bound"));
        }
        Ok(RenderedQuery {
            sql: shape.sql.to_string(),
            parameter_bytes: bytes,
            parameter_expectation: shape.expectation.clone(),
            projection: self.binding.projection.clone(),
            terminal: self.binding.terminal,
            timeout_ns: self.binding.timeout_ns,
        })
    }
    pub(crate) fn compiled(&self) -> Result<&CompiledPlan> {
        self.compiled
            .get_or_init(|| self.compile())
            .as_ref()
            .map_err(Clone::clone)
    }
    fn compile(&self) -> Result<CompiledPlan> {
        let (sql, values) = self.render_shape()?;
        if sql.len() > self.limits.bytes || values.0.len() > self.limits.parameters {
            return Err(capacity("compiled query shape bound"));
        }
        let mut arguments = Vec::with_capacity(values.0.len());
        let mut descriptors = Vec::with_capacity(values.0.len());
        for (index, value) in values.0.into_iter().enumerate() {
            let slot = match value {
                Value::BigUnsigned(Some(id)) if id & expressions::SLOT_TAG != 0 => Arc::clone(
                    self.data
                        .slots
                        .get(&id)
                        .ok_or_else(|| state("unknown native placeholder"))?,
                ),
                // SeaQuery owns LIMIT/OFFSET placeholders; the binding already
                // rejected unsigned values which cannot be represented by Int64.
                Value::BigUnsigned(Some(v)) if v <= i64::MAX as u64 => Arc::new(Slot {
                    codec: scalar(3, false),
                    bound_codec: 3,
                    value: SlotValue::Literal(Some(Arc::from((v as i64).to_le_bytes()))),
                }),
                _ => return Err(state("unexpected untyped SeaQuery placeholder")),
            };
            descriptors.push(Descriptor {
                id: (index + 1)
                    .try_into()
                    .map_err(|_| capacity("parameter ordinal"))?,
                codec: slot.codec.clone(),
                bound_codec: slot.bound_codec,
            });
            arguments.push(slot);
        }
        // Length-prefixed exact identity includes profile/source generation,
        // every retained model hash, projection, operators emitted by SeaQuery,
        // and ordered codecs plus named-parameter identities. Values never enter it.
        let mut identity = b"NWDB-COMPILED-SHAPE-v1".to_vec();
        identity.extend_from_slice(&self.binding.source.engine.to_le_bytes());
        identity.extend_from_slice(&self.binding.source.source.to_le_bytes());
        identity.extend_from_slice(&self.binding.source.generation.to_le_bytes());
        identity.extend_from_slice(&(self.binding.source.profile as u32).to_le_bytes());
        identity.extend_from_slice(&self.binding.terminal.to_le_bytes());
        let mut hashes = std::collections::BTreeSet::new();
        self.metadata_hashes(&mut hashes);
        identity.extend_from_slice(&(hashes.len() as u32).to_le_bytes());
        for hash in hashes {
            identity.extend_from_slice(&hash);
        }
        identity.extend_from_slice(
            &self
                .binding
                .projection
                .as_ref()
                .map_or([0; 32], |p| p.projection.hash()),
        );
        append(&mut identity, sql.as_bytes());
        append(&mut identity, &self.binding.parameter_schema());
        for (d, slot) in descriptors.iter().zip(&arguments) {
            identity.extend_from_slice(&d.id.to_le_bytes());
            identity.extend_from_slice(&d.bound_codec.to_le_bytes());
            identity.extend_from_slice(&d.codec.id.to_le_bytes());
            identity.push(u8::from(d.codec.nullable));
            append(
                &mut identity,
                d.codec.configuration.as_deref().unwrap_or("").as_bytes(),
            );
            let parameter = match slot.value {
                SlotValue::Parameter(id) => id,
                _ => 0,
            };
            identity.extend_from_slice(&parameter.to_le_bytes());
        }
        let expected = Expectation {
            kind: 1,
            profile: self.binding.source.profile,
            hash: Sha256::digest(&identity).into(),
            entity_id: 0,
            projection_id: 0,
            descriptors,
            exact_rows: Some(1),
        };
        let charge = identity
            .len()
            .checked_add(sql.len())
            .and_then(|v| v.checked_add(std::mem::size_of::<CompiledShape>()))
            .and_then(|v| {
                v.checked_add(
                    expected
                        .descriptors
                        .iter()
                        .map(|d| {
                            std::mem::size_of::<Descriptor>()
                                + d.codec.configuration.as_ref().map_or(0, String::len)
                        })
                        .sum::<usize>(),
                )
            })
            .ok_or_else(|| capacity("compiled shape charge"))?;
        Ok(CompiledPlan {
            shape: Arc::new(CompiledShape {
                sql: sql.into(),
                expectation: expected,
                identity: identity.into(),
                charge,
            }),
            arguments,
        })
    }
    fn metadata_hashes(&self, hashes: &mut std::collections::BTreeSet<[u8; 32]>) {
        hashes.insert(self.binding.root.model.hash());
        for model in self.data.models.values() {
            hashes.insert(model.model.hash());
        }
        for alias in self.data.aliases.values() {
            hashes.insert(alias.model.model.hash());
        }
        for plan in self.data.plans.values() {
            plan.metadata_hashes(hashes);
        }
        for cte in self.data.ctes.values() {
            hashes.insert(cte.binding.declared.model.hash());
            cte.body.metadata_hashes(hashes);
        }
    }
    /// Exact immutable shape rendering; only SeaQuery emits SQL/dialect syntax.
    pub fn render_shape(&self) -> Result<(String, Values)> {
        let profile = self.binding.source.profile;
        let mut statement = self.data.statement.clone();
        if self.data.has_cte {
            statement.with_cte(self.data.with_clause.clone());
        }
        let render_select = |s: &SelectStatement| match profile {
            Profile::Sqlite => s.build(SqliteQueryBuilder),
            Profile::PostgreSql => s.build(PostgresQueryBuilder),
        };
        match self.binding.terminal {
            0 | 1 | 8 => Ok(render_select(&statement)),
            2 => {
                statement.limit(self.data.take.map_or(1, |limit| std::cmp::min(limit, 1)));
                Ok(render_select(&statement))
            }
            3 => {
                statement.limit(self.data.take.map_or(2, |limit| std::cmp::min(limit, 2)));
                Ok(render_select(&statement))
            }
            4 => {
                // Count the current native query result including grouping/distinct/paging.
                if self.binding.projection.is_none() {
                    statement.expr(Expr::Constant(Value::Int(Some(1))));
                }
                let mut outer = Query::select();
                outer
                    .expr(Func::count(Expr::col(sea_query::Asterisk)))
                    .from_subquery(statement, SqlAlias::new("_nw_count"));
                Ok(render_select(&outer))
            }
            5 => {
                if self.binding.projection.is_none() {
                    statement.expr(Expr::Constant(Value::Int(Some(1))));
                }
                let mut outer = Query::select();
                let value: Expr = Expr::case(
                    Expr::exists(statement),
                    Expr::Constant(Value::BigInt(Some(1))),
                )
                .finally(Expr::Constant(Value::BigInt(Some(0))))
                .into();
                outer.expr(if profile == Profile::PostgreSql {
                    // PostgreSQL otherwise infers integer for the fixed CASE literals;
                    // B06/B07 correctly require the declared Int64 scalar metadata.
                    value.cast_as(SqlAlias::new("bigint"))
                } else {
                    value
                });
                Ok(render_select(&outer))
            }
            6 => {
                let mut update = Query::update();
                update.table(table_ref(&self.binding.root)?);
                for (name, value) in &self.data.assignments {
                    update.value(SqlAlias::new(name), value.clone());
                }
                for value in &self.data.predicates {
                    update.and_where(value.clone());
                }
                Ok(match profile {
                    Profile::Sqlite => update.build(SqliteQueryBuilder),
                    Profile::PostgreSql => update.build(PostgresQueryBuilder),
                })
            }
            7 => {
                let mut delete = Query::delete();
                delete.from_table(table_ref(&self.binding.root)?);
                for value in &self.data.predicates {
                    delete.and_where(value.clone());
                }
                Ok(match profile {
                    Profile::Sqlite => delete.build(SqliteQueryBuilder),
                    Profile::PostgreSql => delete.build(PostgresQueryBuilder),
                })
            }
            _ => Err(unsupported("terminal")),
        }
    }
    /// Snapshot for native set/subquery/CTE composition; requires a selected shape.
    pub(crate) fn selection(&self) -> Result<SelectStatement> {
        if self.binding.projection.is_none() {
            return Err(invalid("selected plan required"));
        }
        let mut value = self.data.statement.clone();
        if self.data.has_cte {
            value.with_cte(self.data.with_clause.clone());
        }
        Ok(value)
    }
}
pub(crate) fn scalar(id: u32, nullable: bool) -> Codec {
    Codec {
        id,
        configuration: None,
        nullable,
    }
}

fn append(target: &mut Vec<u8>, bytes: &[u8]) {
    target.extend_from_slice(&(bytes.len() as u64).to_le_bytes());
    target.extend_from_slice(bytes);
}

fn recursive_references(root: &ModelBinding, data: &NativeState, hash: [u8; 32]) -> usize {
    usize::from(root.model.hash() == hash)
        + data
            .aliases
            .values()
            .filter(|a| a.model.model.hash() == hash)
            .count()
        + data
            .subqueries
            .iter()
            .map(|p| recursive_references(&p.binding.root, &p.data, hash))
            .sum::<usize>()
        + data
            .set_operations
            .iter()
            .map(|(_, p)| recursive_references(&p.binding.root, &p.data, hash))
            .sum::<usize>()
        + data
            .ctes
            .values()
            .map(|c| recursive_references(&c.body.binding.root, &c.body.data, hash))
            .sum::<usize>()
}
