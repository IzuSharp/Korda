use nativeweave_data_contracts::{
    Codec, Model, Profile, stable_id,
    wire::{Batch, Descriptor, Expectation},
};
use nativeweave_query_binding::{
    Capabilities, CodecBindings, Limits, ModelBinding, ParameterIdentity, ParameterRegistry,
    SourceIdentity,
};
use nativeweave_query_native::{NativeCte, NativePlan, NativeQuery, RenderedQuery};
use sea_query::{Alias, Expr, ExprTrait, PostgresQueryBuilder, Query, SqliteQueryBuilder};
use sha2::{Digest, Sha256};
use sqlx::Connection;
use std::sync::Arc;

type ExpectedRows = Vec<Vec<Option<Vec<u8>>>>;
type QueryCase = (&'static str, RenderedQuery, ExpectedRows);
type Field = (u16, u16, Vec<u8>);
fn field(id: u16, ty: u16, v: Vec<u8>) -> Field {
    (id, ty, v)
}
fn num(id: u16, v: u32) -> Field {
    field(id, 1, v.to_le_bytes().to_vec())
}
fn token(id: u16, v: u64) -> Field {
    field(id, 2, v.to_le_bytes().to_vec())
}
fn record(kind: u16, fields: Vec<Field>) -> Vec<u8> {
    let start = 16 + fields.len() * 16;
    let size = start + fields.iter().map(|(_, _, v)| v.len()).sum::<usize>();
    let mut b = vec![0; size];
    b[..2].copy_from_slice(&1u16.to_le_bytes());
    b[2..4].copy_from_slice(&kind.to_le_bytes());
    b[4..8].copy_from_slice(&(size as u32).to_le_bytes());
    b[8..12].copy_from_slice(&(fields.len() as u32).to_le_bytes());
    let mut offset = start;
    for (i, (id, ty, v)) in fields.into_iter().enumerate() {
        let at = 16 + i * 16;
        b[at..at + 2].copy_from_slice(&id.to_le_bytes());
        b[at + 2..at + 4].copy_from_slice(&ty.to_le_bytes());
        b[at + 4..at + 8].copy_from_slice(&(v.len() as u32).to_le_bytes());
        b[at + 8..at + 16].copy_from_slice(&(offset as u64).to_le_bytes());
        b[offset..offset + v.len()].copy_from_slice(&v);
        offset += v.len();
    }
    b
}
fn fixture(name: &str) -> Vec<u8> {
    std::fs::read(
        std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("../golden")
            .join(name),
    )
    .unwrap()
}
fn scalar(id: u32, nullable: bool) -> Codec {
    Codec {
        id,
        configuration: None,
        nullable,
    }
}
fn caps(profile: Profile) -> Capabilities {
    Capabilities::from_mask((1u64 << 25) - 1, profile).unwrap()
}
struct Context {
    root: ModelBinding,
    child: ModelBinding,
    registry: Arc<ParameterRegistry>,
}
impl Context {
    fn new(profile: Profile) -> Self {
        let source = SourceIdentity {
            engine: 91,
            source: 1,
            generation: 1,
            profile,
        };
        let codecs = Arc::new(CodecBindings::new(profile, &[]).unwrap());
        Self {
            root: ModelBinding::new(
                source,
                Arc::new(Model::parse(&fixture("parent-model.json"), profile).unwrap()),
                Arc::clone(&codecs),
            )
            .unwrap(),
            child: ModelBinding::new(
                source,
                Arc::new(Model::parse(&fixture("child-model.json"), profile).unwrap()),
                codecs,
            )
            .unwrap(),
            registry: Arc::new(ParameterRegistry::new(source, 4096).unwrap()),
        }
    }
    fn query(&self) -> NativeQuery {
        NativeQuery::new(
            self.root.clone(),
            Arc::clone(&self.registry),
            self.root.model.table_id(),
            Limits::default(),
            caps(self.root.source.profile),
            10_000_000_000,
        )
        .unwrap()
    }
    fn col(&self, name: &str) -> u32 {
        column(&self.root, name)
    }
    fn value(&self, q: &mut NativeQuery, v: i64) -> u64 {
        q.value(&cells(
            self.root.source.profile,
            scalar(3, false),
            &[Some(v.to_le_bytes().to_vec())],
        ))
        .unwrap()
    }
    fn select_all(&self, q: &mut NativeQuery) {
        let a = q.column(0, self.col("Id")).unwrap();
        let b = q.column(0, self.col("Name")).unwrap();
        select(
            q,
            &[(a, "Id", scalar(3, false)), (b, "Name", scalar(5, false))],
        );
    }
    fn render(&self, q: &mut NativeQuery, terminal: u32) -> RenderedQuery {
        let plan = q.seal(terminal).unwrap();
        execute(&plan, &[])
    }
}
fn column(model: &ModelBinding, name: &str) -> u32 {
    model
        .model
        .columns()
        .iter()
        .find(|c| c.name == name)
        .unwrap()
        .id
}
fn cells(profile: Profile, codec: Codec, values: &[Option<Vec<u8>>]) -> Vec<u8> {
    let expected = Expectation {
        kind: 3,
        profile,
        hash: [0; 32],
        entity_id: 0,
        projection_id: 0,
        descriptors: vec![Descriptor {
            id: 1,
            bound_codec: codec.id,
            codec,
        }],
        exact_rows: None,
    };
    Batch::encode(
        &expected,
        values.len() as u32,
        &values.iter().map(|v| v.as_deref()).collect::<Vec<_>>(),
    )
    .unwrap()
}
fn select(q: &mut NativeQuery, values: &[(u64, &str, Codec)]) {
    let canonical=format!("{{\"version\":1,\"fields\":[{}]}}",values.iter().enumerate().map(|(i,(_,n,c))|format!("{{\"ordinal\":{i},\"name\":{},\"codec\":{},\"codec_config\":null,\"nullable\":{}}}",serde_json::to_string(n).unwrap(),c.id,c.nullable)).collect::<Vec<_>>().join(","));
    let records = values
        .iter()
        .enumerate()
        .map(|(i, (_, name, c))| {
            record(
                22,
                vec![
                    num(1, i as u32),
                    field(2, 4, name.as_bytes().to_vec()),
                    num(3, c.id),
                    num(4, u32::from(c.nullable)),
                ],
            )
        })
        .collect::<Vec<_>>();
    let mut sequence = (records.len() as u32).to_le_bytes().to_vec();
    for b in records {
        sequence.extend((b.len() as u32).to_le_bytes());
        sequence.extend(b);
    }
    let projection = record(
        13,
        vec![
            field(1, 5, sequence),
            field(2, 5, Sha256::digest(canonical).to_vec()),
        ],
    );
    q.clause(
        6,
        &record(
            6,
            vec![
                field(
                    1,
                    8,
                    values
                        .iter()
                        .flat_map(|(t, _, _)| t.to_le_bytes())
                        .collect(),
                ),
                field(2, 5, projection),
            ],
        ),
    )
    .unwrap();
}
fn execute(plan: &Arc<NativePlan>, values: &[Option<Vec<u8>>]) -> RenderedQuery {
    let expected = plan.binding.parameter_expectation();
    let b = Batch::encode(
        &expected,
        1,
        &values.iter().map(|v| v.as_deref()).collect::<Vec<_>>(),
    )
    .unwrap();
    plan.render(plan.binding.source, &b).unwrap()
}
fn where_expr(q: &mut NativeQuery, value: u64) {
    q.clause(1, &record(6, vec![token(1, value)])).unwrap();
}
fn order(q: &mut NativeQuery, value: u64) {
    q.clause(
        3,
        &record(6, vec![token(1, value), num(2, 0), num(3, 1), num(4, 1)]),
    )
    .unwrap();
}

#[test]
fn direct_seaquery_sql_and_parameter_order_agree() {
    for profile in [Profile::Sqlite, Profile::PostgreSql] {
        let c = Context::new(profile);
        let mut q = c.query();
        let id = q.column(0, c.col("Id")).unwrap();
        let literal = c.value(&mut q, 7);
        let condition = q.binary(4, id, literal).unwrap();
        where_expr(&mut q, condition);
        order(&mut q, id);
        q.clause(5, &record(6, vec![token(1, 5), token(2, 2)]))
            .unwrap();
        c.select_all(&mut q);
        let r = c.render(&mut q, 0);
        let direct = Query::select()
            .expr_as(
                Expr::col((Alias::new("Parents"), Alias::new("Id"))),
                Alias::new("Id"),
            )
            .expr_as(
                Expr::col((Alias::new("Parents"), Alias::new("Name"))),
                Alias::new("Name"),
            )
            .from(Alias::new("Parents"))
            .and_where(Expr::col((Alias::new("Parents"), Alias::new("Id"))).gte(7i64))
            .order_by_expr_with_nulls(
                Expr::col((Alias::new("Parents"), Alias::new("Id"))),
                sea_query::Order::Asc,
                sea_query::NullOrdering::Last,
            )
            .limit(5)
            .offset(2)
            .to_owned();
        let (sql, values) = match profile {
            Profile::Sqlite => direct.build(SqliteQueryBuilder),
            Profile::PostgreSql => direct.build(PostgresQueryBuilder),
        };
        assert_eq!(r.sql, sql);
        assert_eq!(values.0.len(), r.parameter_expectation.descriptors.len());
        let batch = Batch::parse(&r.parameter_bytes, r.parameter_expectation).unwrap();
        for (i, v) in [7i64, 5, 2].into_iter().enumerate() {
            assert_eq!(batch.cell(0, i).unwrap(), Some(v.to_le_bytes().as_slice()));
        }
    }
}

#[test]
fn failures_are_atomic_and_capabilities_precede_render() {
    let c = Context::new(Profile::Sqlite);
    let mut q = c.query();
    c.select_all(&mut q);
    let before = q
        .clone_selected()
        .unwrap()
        .seal(8)
        .unwrap()
        .render_shape()
        .unwrap()
        .0;
    for opcode in [0, 23, 999] {
        assert!(q.clause(opcode, &record(6, vec![])).is_err());
    }
    assert!(
        q.clause(12, &record(6, vec![num(1, 0), num(2, 0)]))
            .is_err()
    );
    let id = q.column(0, c.col("Id")).unwrap();
    let count = q.counts();
    assert!(
        q.clause(
            3,
            &record(6, vec![token(1, id), num(2, 7), num(3, 1), num(4, 1)])
        )
        .is_err()
    );
    assert_eq!(q.counts(), count);
    assert_eq!(
        before,
        q.clone_selected()
            .unwrap()
            .seal(8)
            .unwrap()
            .render_shape()
            .unwrap()
            .0
    );
    let lifted = q.unary(4, id).unwrap();
    assert!(q.unary(4, lifted).is_err());
    assert!(q.binary(1, id, 0).is_err());
    let other = c.query().column(0, c.col("Id")).unwrap();
    assert!(q.binary(1, id, other).is_err());
    let plan = q.seal(0).unwrap();
    execute(&plan, &[]);
    assert!(
        plan.render(
            c.root.source,
            &Batch::encode(&plan.binding.parameter_expectation(), 1, &[]).unwrap()
        )
        .is_err()
    );
    assert!(q.column(0, c.col("Id")).is_err());
    q.close();
    assert_eq!(q.counts(), (0, 0, 0));
}

#[test]
fn cardinality_bounds_preserve_take_and_grouping_rejects_arbitrary_columns() {
    for profile in [Profile::Sqlite, Profile::PostgreSql] {
        let c = Context::new(profile);
        for terminal in [2, 3] {
            for take in [0, 1, 2, 5] {
                let mut q = c.query();
                c.select_all(&mut q);
                q.clause(5, &record(6, vec![token(1, take)])).unwrap();
                let rendered = c.render(&mut q, terminal);
                let expected = std::cmp::min(take, if terminal == 2 { 1 } else { 2 });
                let mut oracle = Query::select();
                oracle
                    .columns([
                        (Alias::new("Parents"), Alias::new("Id")),
                        (Alias::new("Parents"), Alias::new("Name")),
                    ])
                    .from(Alias::new("Parents"))
                    .limit(expected);
                let (_, values) = match profile {
                    Profile::Sqlite => oracle.build(SqliteQueryBuilder),
                    Profile::PostgreSql => oracle.build(PostgresQueryBuilder),
                };
                let batch = Batch::parse(&rendered.parameter_bytes, rendered.parameter_expectation)
                    .unwrap();
                assert_eq!(values.0.len(), 1);
                assert_eq!(
                    batch.cell(0, 0).unwrap(),
                    Some((expected as i64).to_le_bytes().as_slice())
                );
            }
        }
        let mut q = c.query();
        let key = q.column(0, c.col("Id")).unwrap();
        q.clause(4, &record(6, vec![token(1, key)])).unwrap();
        c.select_all(&mut q);
        assert!(q.seal(8).is_err());
        assert_eq!(q.state(), nativeweave_query_binding::QueryState::Mutable);
        // Reacquiring an equivalent native key has a distinct token, and remains legal.
        let second_key = q.column(0, c.col("Id")).unwrap();
        select(&mut q, &[(second_key, "Id", scalar(3, false))]);
        // A render/CTE/prepared snapshot has a new arena generation but the same
        // native node indices. Group validation must survive that ordinary clone.
        assert!(q.clone_selected().unwrap().seal(8).is_ok());
        assert!(q.seal(8).is_ok());
    }
}

#[test]
fn parameters_are_isolated_and_values_are_snapshotted() {
    let c = Context::new(Profile::Sqlite);
    let mut q = c.query();
    let id = q.column(0, c.col("Id")).unwrap();
    let parameter = q
        .parameter(ParameterIdentity {
            id: 17,
            name: "minimum".into(),
            codec: scalar(3, false),
            bound_codec: 3,
        })
        .unwrap();
    let p = q.binary(4, id, parameter).unwrap();
    where_expr(&mut q, p);
    c.select_all(&mut q);
    let plan = q.seal(8).unwrap();
    let first = execute(&plan, &[Some(1i64.to_le_bytes().to_vec())]);
    let second = execute(&plan, &[Some(9i64.to_le_bytes().to_vec())]);
    assert_eq!(first.sql, second.sql);
    assert_ne!(first.parameter_bytes, second.parameter_bytes);
    let mut q = c.query();
    let mut bytes = cells(
        Profile::Sqlite,
        scalar(5, false),
        &[Some("O'Reilly 🦀; DROP TABLE x".as_bytes().to_vec())],
    );
    let value = q.value(&bytes).unwrap();
    bytes.fill(0);
    select(&mut q, &[(value, "literal", scalar(5, false))]);
    let r = c.render(&mut q, 0);
    assert!(!r.sql.contains("O'Reilly"));
    let b = Batch::parse(&r.parameter_bytes, r.parameter_expectation).unwrap();
    assert_eq!(
        b.cell(0, 0).unwrap().unwrap(),
        "O'Reilly 🦀; DROP TABLE x".as_bytes()
    );
}

#[test]
fn join_commit_and_abort_invalidate_provisional_expressions() {
    let c = Context::new(Profile::Sqlite);
    let mut q = c.query();
    let model = q.import_model(c.child.clone()).unwrap();
    let reserve = record(
        6,
        vec![
            token(1, model),
            num(2, c.child.model.table_id()),
            field(3, 4, b"child".to_vec()),
            num(4, 1),
        ],
    );
    let alias = q.clause(18, &reserve).unwrap();
    let right = q.column(alias, column(&c.child, "ParentId")).unwrap();
    q.clause(19, &record(6, vec![token(1, alias)])).unwrap();
    assert!(q.unary(1, right).is_err());
    let alias = q.clause(18, &reserve).unwrap();
    let right = q.column(alias, column(&c.child, "ParentId")).unwrap();
    let left = q.column(0, c.col("Id")).unwrap();
    let nullable = q.unary(4, left).unwrap();
    let condition = q.binary(1, nullable, right).unwrap();
    q.clause(7, &record(6, vec![token(1, alias), token(2, condition)]))
        .unwrap();
    assert!(q.unary(1, right).is_err());
    let matched = q.clause(20, &record(6, vec![token(1, alias)])).unwrap();
    select(
        &mut q,
        &[
            (left, "Id", scalar(3, false)),
            (matched, "matched", scalar(4, false)),
        ],
    );
    let r = c.render(&mut q, 0);
    assert!(r.sql.contains("LEFT JOIN (SELECT"));
    assert!(r.sql.contains("_nw_match"));
    assert!(!r.sql.contains("COALESCE"));
}

#[test]
fn expanded_native_expression_budget_prevents_exponential_cloning() {
    let c = Context::new(Profile::Sqlite);
    let mut q = NativeQuery::new(
        c.root.clone(),
        Arc::clone(&c.registry),
        c.root.model.table_id(),
        Limits {
            nodes: 128,
            ..Limits::default()
        },
        caps(Profile::Sqlite),
        10,
    )
    .unwrap();
    let value = c.value(&mut q, 1);
    let mut expression = q.binary(1, value, value).unwrap();
    let mut rejected = false;
    for _ in 0..20 {
        let before = q.counts();
        match q.binary(11, expression, expression) {
            Ok(v) => expression = v,
            Err(_) => {
                assert_eq!(q.counts(), before);
                rejected = true;
                break;
            }
        }
    }
    assert!(rejected);
    assert!(q.counts().2 <= 128);
}

fn case_queries(c: &Context) -> Vec<QueryCase> {
    let mut cases = Vec::new();
    let mut q = c.query();
    let id = q.column(0, c.col("Id")).unwrap();
    let one = c.value(&mut q, 1);
    let p = q.binary(4, id, one).unwrap();
    where_expr(&mut q, p);
    order(&mut q, id);
    c.select_all(&mut q);
    cases.push((
        "filter-order",
        c.render(&mut q, 0),
        vec![
            vec![Some(1i64.to_le_bytes().to_vec()), Some(b"Alice".to_vec())],
            vec![
                Some(2i64.to_le_bytes().to_vec()),
                Some("O'Reilly 🦀".as_bytes().to_vec()),
            ],
        ],
    ));
    for (op, values, wanted) in [
        (
            1,
            vec![
                Some(1i64.to_le_bytes().to_vec()),
                Some(2i64.to_le_bytes().to_vec()),
            ],
            vec![1i64, 2],
        ),
        (2, Vec::new(), vec![0, 1, 2]),
        (
            3,
            vec![
                Some(1i64.to_le_bytes().to_vec()),
                Some(2i64.to_le_bytes().to_vec()),
            ],
            vec![1, 2],
        ),
    ] {
        let mut q = c.query();
        let id = q.column(0, c.col("Id")).unwrap();
        let p = q
            .list(
                op,
                id,
                &cells(c.root.source.profile, scalar(3, false), &values),
            )
            .unwrap();
        where_expr(&mut q, p);
        order(&mut q, id);
        select(&mut q, &[(id, "Id", scalar(3, false))]);
        cases.push((
            "membership",
            c.render(&mut q, 0),
            wanted
                .into_iter()
                .map(|v| vec![Some(v.to_le_bytes().to_vec())])
                .collect(),
        ));
    }
    let mut q = c.query();
    let root = q.column(0, c.col("Id")).unwrap();
    let model = q.import_model(c.child.clone()).unwrap();
    let alias = q
        .clause(
            18,
            &record(
                6,
                vec![
                    token(1, model),
                    num(2, c.child.model.table_id()),
                    field(3, 4, b"c".to_vec()),
                    num(4, 1),
                ],
            ),
        )
        .unwrap();
    let child = q.column(alias, column(&c.child, "ParentId")).unwrap();
    let lifted = q.unary(4, root).unwrap();
    let on = q.binary(1, lifted, child).unwrap();
    q.clause(7, &record(6, vec![token(1, alias), token(2, on)]))
        .unwrap();
    let child_id = q.column(alias, column(&c.child, "Id")).unwrap();
    let matched = q.clause(20, &record(6, vec![token(1, alias)])).unwrap();
    order(&mut q, root);
    select(
        &mut q,
        &[
            (root, "Id", scalar(3, false)),
            (child_id, "child", scalar(3, true)),
            (matched, "matched", scalar(4, false)),
        ],
    );
    cases.push((
        "nullable-left-join",
        c.render(&mut q, 0),
        vec![
            vec![Some(0i64.to_le_bytes().to_vec()), None, Some(vec![0])],
            vec![
                Some(1i64.to_le_bytes().to_vec()),
                Some(10i64.to_le_bytes().to_vec()),
                Some(vec![1]),
            ],
            vec![Some(2i64.to_le_bytes().to_vec()), None, Some(vec![0])],
        ],
    ));
    let mut q = c.query();
    let count = q
        .clause(13, &record(6, vec![num(1, 0), num(3, 0), num(5, 3)]))
        .unwrap();
    let id = q.column(0, c.col("Id")).unwrap();
    let sum = q
        .clause(
            13,
            &record(6, vec![num(1, 2), token(2, id), num(3, 0), num(5, 3)]),
        )
        .unwrap();
    select(
        &mut q,
        &[
            (count, "count", scalar(3, false)),
            (sum, "sum", scalar(3, true)),
        ],
    );
    cases.push((
        "aggregate",
        c.render(&mut q, 0),
        vec![vec![
            Some(3i64.to_le_bytes().to_vec()),
            Some(3i64.to_le_bytes().to_vec()),
        ]],
    ));
    let mut q = c.query();
    let id = q.column(0, c.col("Id")).unwrap();
    let window = q.clause(11, &record(6, vec![num(1, 0)])).unwrap();
    q.clause(
        11,
        &record(
            6,
            vec![
                num(1, 2),
                token(2, window),
                token(3, id),
                num(4, 0),
                num(5, 1),
            ],
        ),
    )
    .unwrap();
    let rank = q
        .clause(13, &record(6, vec![num(1, 6), num(3, 0), num(5, 3)]))
        .unwrap();
    let rank = q
        .clause(21, &record(6, vec![token(1, rank), token(2, window)]))
        .unwrap();
    q.clause(
        11,
        &record(6, vec![num(1, 1), token(2, window), token(3, id)]),
    )
    .unwrap();
    order(&mut q, id);
    select(
        &mut q,
        &[
            (id, "Id", scalar(3, false)),
            (rank, "rank", scalar(3, false)),
        ],
    );
    cases.push((
        "window-snapshot",
        c.render(&mut q, 0),
        (0..3i64)
            .map(|v| {
                vec![
                    Some(v.to_le_bytes().to_vec()),
                    Some((v + 1).to_le_bytes().to_vec()),
                ]
            })
            .collect(),
    ));
    cases.extend(advanced_cases(c));
    cases
}

#[tokio::test]
async fn sqlite_actual_seaquery_and_sole_binder_cases() {
    let mut db = sqlx::SqliteConnection::connect("sqlite::memory:")
        .await
        .unwrap();
    setup_sqlite(&mut db).await;
    let c = Context::new(Profile::Sqlite);
    let mut oracles = Vec::new();
    for (label, command, expected) in case_queries(&c) {
        oracles.push(oracle(label, &command, &expected));
        let params = nativeweave_data_codecs::Parameters::decode(
            &command.parameter_bytes,
            command.parameter_expectation,
            nativeweave_data_codecs::Limits::default(),
        )
        .unwrap();
        let rows = sqlx::query_with(
            sqlx::AssertSqlSafe(command.sql.as_str()),
            params.sqlite().unwrap(),
        )
        .fetch_all(&mut db)
        .await
        .unwrap_or_else(|e| panic!("{label}: {e}; {}", command.sql));
        let fields = command.projection.unwrap().projection;
        let actual = rows
            .iter()
            .map(|row| {
                fields
                    .fields()
                    .iter()
                    .enumerate()
                    .map(|(i, f)| {
                        nativeweave_data_codecs::read_sqlite(row, i, &f.codec, 4096).unwrap()
                    })
                    .collect::<Vec<_>>()
            })
            .collect::<Vec<_>>();
        let (mut actual, mut expected) = (actual, expected);
        if label == "set-operation" {
            actual.sort();
            expected.sort();
        }
        assert_eq!(actual, expected, "{label}");
    }
    write_oracles(c.root.source.profile, &oracles);
}
async fn setup_sqlite(db: &mut sqlx::SqliteConnection) {
    for sql in [
        "CREATE TABLE \"Parents\" (\"Id\" INTEGER PRIMARY KEY, \"Name\" TEXT NOT NULL)",
        "CREATE TABLE \"Children\" (\"Id\" INTEGER PRIMARY KEY, \"ParentId\" INTEGER NULL)",
        "INSERT INTO \"Parents\" VALUES (0,'Zero'),(1,'Alice'),(2,'O''Reilly 🦀')",
        "INSERT INTO \"Children\" VALUES (10,1),(11,NULL)",
    ] {
        sqlx::query(sql).execute(&mut *db).await.unwrap();
    }
}

#[tokio::test]
async fn postgres_actual_seaquery_and_sole_binder_cases() {
    let url = std::env::var("NATIVEWEAVE_TEST_POSTGRES_URL")
        .expect("PostgreSQL execution requires the explicit disposable test endpoint");
    let mut db = sqlx::PgConnection::connect(&url).await.unwrap();
    for sql in [
        "CREATE TEMP TABLE \"Parents\" (\"Id\" BIGINT PRIMARY KEY, \"Name\" TEXT NOT NULL)",
        "CREATE TEMP TABLE \"Children\" (\"Id\" BIGINT PRIMARY KEY, \"ParentId\" BIGINT NULL)",
        "INSERT INTO \"Parents\" VALUES (0,'Zero'),(1,'Alice'),(2,'O''Reilly 🦀')",
        "INSERT INTO \"Children\" VALUES (10,1),(11,NULL)",
    ] {
        sqlx::query(sql).execute(&mut db).await.unwrap();
    }
    let c = Context::new(Profile::PostgreSql);
    let mut oracles = Vec::new();
    for (label, command, expected) in case_queries(&c) {
        oracles.push(oracle(label, &command, &expected));
        let params = nativeweave_data_codecs::Parameters::decode(
            &command.parameter_bytes,
            command.parameter_expectation,
            nativeweave_data_codecs::Limits::default(),
        )
        .unwrap();
        let rows = sqlx::query_with(
            sqlx::AssertSqlSafe(command.sql.as_str()),
            params.postgres().unwrap(),
        )
        .fetch_all(&mut db)
        .await
        .unwrap_or_else(|e| panic!("{label}: {e}; {}", command.sql));
        let fields = command.projection.unwrap().projection;
        let actual = rows
            .iter()
            .map(|row| {
                fields
                    .fields()
                    .iter()
                    .enumerate()
                    .map(|(i, f)| {
                        nativeweave_data_codecs::read_postgres(row, i, &f.codec, 4096)
                            .unwrap_or_else(|e| panic!("{label} {i}: {e}; {}", command.sql))
                    })
                    .collect::<Vec<_>>()
            })
            .collect::<Vec<_>>();
        let (mut actual, mut expected) = (actual, expected);
        if label == "set-operation" {
            actual.sort();
            expected.sort();
        }
        assert_eq!(actual, expected, "{label}");
    }
    write_oracles(c.root.source.profile, &oracles);
}

fn single_column_model(c: &Context, name: &str) -> ModelBinding {
    let logical = format!("NativeWeave.Query.Native.Tests:{name}");
    let table = stable_id(&[Some("NWDB-TABLE-ID-v1"), Some(&logical), None, Some(name)]);
    let column = stable_id(&[
        Some("NWDB-COLUMN-ID-v1"),
        Some(&logical),
        None,
        Some(name),
        Some("Id"),
    ]);
    let text = format!(
        r#"{{"version":1,"logical_id":"{logical}","table_id":{table},"schema":null,"table":"{name}","columns":[{{"id":{column},"name":"Id","codec":3,"codec_config":null,"nullable":false,"key_ordinal":null,"generated":false,"concurrency":false,"collation":null,"max_length":null}}],"projection":[{column}],"relations":[]}}"#
    );
    ModelBinding::new(
        c.root.source,
        Arc::new(Model::parse(text.as_bytes(), c.root.source.profile).unwrap()),
        Arc::clone(&c.root.codecs),
    )
    .unwrap()
}
fn from_model(c: &Context, model: ModelBinding) -> NativeQuery {
    let table = model.model.table_id();
    NativeQuery::new(
        model,
        Arc::clone(&c.registry),
        table,
        Limits::default(),
        caps(c.root.source.profile),
        10_000_000_000,
    )
    .unwrap()
}
fn ints(values: &[i64]) -> Vec<Vec<Option<Vec<u8>>>> {
    values
        .iter()
        .map(|v| vec![Some(v.to_le_bytes().to_vec())])
        .collect()
}
fn selected_ids(c: &Context, op: u32, threshold: i64) -> NativeQuery {
    let mut q = c.query();
    let id = q.column(0, c.col("Id")).unwrap();
    let v = c.value(&mut q, threshold);
    let predicate = q.binary(op, id, v).unwrap();
    where_expr(&mut q, predicate);
    select(&mut q, &[(id, "Id", scalar(3, false))]);
    q
}
fn advanced_cases(c: &Context) -> Vec<QueryCase> {
    let mut cases = Vec::new();
    for (operation, wanted) in [
        (0, vec![0, 1, 2]),
        (1, vec![1, 2, 0]),
        (2, vec![]),
        (3, vec![1, 2]),
    ] {
        let mut a = selected_ids(c, 4, 1);
        let mut b = selected_ids(c, 1, 0);
        let imported = a.import_plan(b.seal(8).unwrap()).unwrap();
        a.clause(9, &record(6, vec![token(1, imported), num(2, operation)]))
            .unwrap();
        // UNION/INTERSECT/EXCEPT result ordering is unspecified. Verification sorts these cases.
        cases.push(("set-operation", c.render(&mut a, 0), ints(&wanted)));
    }
    for operation in [0, 1] {
        let mut source = selected_ids(c, 1, 1);
        let plan = source.clone_selected().unwrap().seal(8).unwrap();
        // Original source may still be used and modified independently.
        let two = c.value(&mut source, 2);
        assert!(two != 0);
        let mut q = c.query();
        let id = q.column(0, c.col("Id")).unwrap();
        let imported = q.import_plan(plan).unwrap();
        let mut fields = vec![token(1, imported), num(2, operation)];
        if operation == 1 {
            fields.push(token(3, id));
        }
        let p = q.clause(14, &record(6, fields)).unwrap();
        where_expr(&mut q, p);
        order(&mut q, id);
        select(&mut q, &[(id, "Id", scalar(3, false))]);
        cases.push((
            "subquery",
            c.render(&mut q, 0),
            ints(if operation == 0 { &[0, 1, 2] } else { &[1] }),
        ));
    }
    let mut body = selected_ids(c, 4, 1);
    let declared = single_column_model(c, "SelectedParents");
    let cte = NativeCte::create(declared.clone(), declared.model.table_id(), &body, 0).unwrap();
    let zero = c.value(&mut body, 0);
    let body_id = body.column(0, c.col("Id")).unwrap();
    let changed = body.binary(1, body_id, zero).unwrap();
    where_expr(&mut body, changed);
    let mut q = from_model(c, declared.clone());
    let imported = q.import_cte(cte).unwrap();
    q.clause(10, &record(6, vec![token(1, imported)])).unwrap();
    let id = q.column(0, column(&declared, "Id")).unwrap();
    order(&mut q, id);
    select(&mut q, &[(id, "Id", scalar(3, false))]);
    cases.push(("cte-snapshot", c.render(&mut q, 0), ints(&[1, 2])));

    let declared = single_column_model(c, "Numbers");
    let mut anchor = selected_ids(c, 1, 0);
    assert!(NativeCte::create(declared.clone(), declared.model.table_id(), &anchor, 1).is_err());
    let mut member = from_model(c, declared.clone());
    let id = member.column(0, column(&declared, "Id")).unwrap();
    let one = c.value(&mut member, 1);
    let three = c.value(&mut member, 3);
    let next = member.binary(7, id, one).unwrap();
    let stop = member.binary(5, id, three).unwrap();
    where_expr(&mut member, stop);
    select(&mut member, &[(next, "Id", scalar(3, false))]);
    let imported = anchor.import_plan(member.seal(8).unwrap()).unwrap();
    anchor
        .clause(9, &record(6, vec![token(1, imported), num(2, 1)]))
        .unwrap();
    let recursive =
        NativeCte::create(declared.clone(), declared.model.table_id(), &anchor, 1).unwrap();
    let mut q = from_model(c, declared.clone());
    let imported = q.import_cte(recursive).unwrap();
    q.clause(10, &record(6, vec![token(1, imported)])).unwrap();
    let id = q.column(0, column(&declared, "Id")).unwrap();
    order(&mut q, id);
    select(&mut q, &[(id, "Id", scalar(3, false))]);
    cases.push(("recursive-cte", c.render(&mut q, 0), ints(&[0, 1, 2, 3])));

    for direct in [false, true] {
        let mut q = c.query();
        let id = q.column(0, c.col("Id")).unwrap();
        let window = q.clause(11, &record(6, vec![num(1, 0)])).unwrap();
        q.clause(
            11,
            &record(
                6,
                vec![
                    num(1, 2),
                    token(2, window),
                    token(3, id),
                    num(4, 0),
                    num(5, 1),
                ],
            ),
        )
        .unwrap();
        q.clause(
            11,
            &record(6, vec![num(1, 3), token(2, window), num(6, 0), num(7, 1)]),
        )
        .unwrap();
        let mut fields = vec![num(1, 2), token(2, id), num(3, 0)];
        if direct {
            fields.push(token(4, window));
        }
        fields.push(num(5, 3));
        let mut sum = q.clause(13, &record(6, fields)).unwrap();
        if !direct {
            sum = q
                .clause(21, &record(6, vec![token(1, sum), token(2, window)]))
                .unwrap();
        }
        order(&mut q, id);
        select(&mut q, &[(sum, "sum", scalar(3, true))]);
        cases.push((
            "window-aggregate-cast",
            c.render(&mut q, 0),
            ints(&[0, 1, 3]),
        ));
    }
    for (operation, text, wanted) in [
        (0, "Reilly", vec![2]),
        (1, "Ali", vec![1]),
        (2, "🦀", vec![2]),
        (3, "O%", vec![2]),
        (0, "%", vec![]),
        (0, "_", vec![]),
        (0, "';DROP", vec![]),
    ] {
        let mut q = c.query();
        let id = q.column(0, c.col("Id")).unwrap();
        let name = q.column(0, c.col("Name")).unwrap();
        let bytes = cells(
            c.root.source.profile,
            scalar(5, false),
            &[Some(text.as_bytes().to_vec())],
        );
        let p = q
            .clause(
                16,
                &record(
                    6,
                    vec![
                        num(1, operation),
                        token(2, name),
                        field(3, 6, bytes),
                        num(4, 92),
                    ],
                ),
            )
            .unwrap();
        where_expr(&mut q, p);
        order(&mut q, id);
        select(&mut q, &[(id, "Id", scalar(3, false))]);
        cases.push(("text-escaped-values", c.render(&mut q, 0), ints(&wanted)));
    }
    let mut q = c.query();
    let id = q.column(0, c.col("Id")).unwrap();
    let one = c.value(&mut q, 1);
    let zero = c.value(&mut q, 0);
    let cond = q.binary(3, id, one).unwrap();
    let case = q
        .clause(
            15,
            &record(6, vec![token(1, cond), token(2, one), token(3, zero)]),
        )
        .unwrap();
    order(&mut q, id);
    select(&mut q, &[(case, "case", scalar(3, false))]);
    cases.push(("case", c.render(&mut q, 0), ints(&[0, 0, 1])));
    let mut q = c.query();
    let id = q.column(0, c.col("Id")).unwrap();
    q.clause(4, &record(6, vec![token(1, id)])).unwrap();
    let count = q
        .clause(13, &record(6, vec![num(1, 0), num(3, 0), num(5, 3)]))
        .unwrap();
    let zero = c.value(&mut q, 0);
    let h = q.binary(3, count, zero).unwrap();
    q.clause(2, &record(6, vec![token(1, h)])).unwrap();
    order(&mut q, id);
    select(&mut q, &[(id, "Id", scalar(3, false))]);
    cases.push(("group-having", c.render(&mut q, 0), ints(&[0, 1, 2])));
    cases
}

#[test]
fn immutable_cache_excludes_values_and_has_finite_eviction() {
    use nativeweave_query_native::ShapeCache;
    let c = Context::new(Profile::Sqlite);
    let first = selected_ids(&c, 4, 1).seal(8).unwrap();
    let second = selected_ids(&c, 4, 2).seal(8).unwrap();
    let different = selected_ids(&c, 5, 2).seal(8).unwrap();
    let mut cache = ShapeCache::new(1, 8192, [11; 32]).unwrap();
    let retained = cache.acquire(&first).unwrap();
    let reused = cache.acquire(&second).unwrap();
    assert!(Arc::ptr_eq(&retained, &reused));
    assert_eq!(cache.counts().0, 1);
    let shape = cache.acquire(&different).unwrap();
    assert!(!Arc::ptr_eq(&retained, &shape));
    assert_eq!(cache.counts().0, 1);
    assert!(!retained.sql().is_empty());
    let empty = Batch::encode(&first.binding.parameter_expectation(), 1, &[]).unwrap();
    let a = first
        .render_cached(c.root.source, &empty, &mut cache)
        .unwrap();
    let b = second
        .render_cached(c.root.source, &empty, &mut cache)
        .unwrap();
    assert_eq!(a.sql, b.sql);
    assert_ne!(a.parameter_bytes, b.parameter_bytes);
    cache.clear();
    assert_eq!(cache.counts(), (0, 0));
    assert!(!retained.sql().is_empty());
    let mut small = ShapeCache::new(1, 1, [11; 32]).unwrap();
    small.acquire(&first).unwrap();
    assert_eq!(small.counts(), (0, 0));
    assert!(ShapeCache::new(0, 100, [11; 32]).is_err());
    assert!(ShapeCache::new(1, 100, [0; 32]).is_err());
}

#[test]
fn malformed_operations_and_nonfinite_values_fail_without_mutation() {
    let c = Context::new(Profile::Sqlite);
    let mut q = c.query();
    let before = q.counts();
    for n in 0..128 {
        assert!(q.clause(1, &vec![0; n]).is_err());
    }
    let mut nonfinite = cells(
        Profile::Sqlite,
        scalar(11, false),
        &[Some(1f64.to_le_bytes().to_vec())],
    );
    let at = nonfinite.len() - 8;
    nonfinite[at..].copy_from_slice(&f64::NAN.to_le_bytes());
    assert!(q.value(&nonfinite).is_err());
    let mut invalid_utf8 = cells(Profile::Sqlite, scalar(5, false), &[Some(b"x".to_vec())]);
    *invalid_utf8.last_mut().unwrap() = 0xff;
    assert!(q.value(&invalid_utf8).is_err());
    assert!(q.clause(5, &record(6, vec![token(1, u64::MAX)])).is_err());
    assert_eq!(q.counts(), before);
}

fn oracle(label: &str, command: &RenderedQuery, expected: &ExpectedRows) -> serde_json::Value {
    let batch = Batch::parse(
        &command.parameter_bytes,
        command.parameter_expectation.clone(),
    )
    .unwrap();
    let args = (0..command.parameter_expectation.descriptors.len())
        .map(|i| batch.cell(0, i).unwrap().map(hex))
        .collect::<Vec<_>>();
    serde_json::json!({"label":label,"sql":command.sql,"ordered_arguments":args,
        "rows": expected.iter().map(|row| row.iter().map(|cell|cell.as_deref().map_or_else(||"null".to_owned(),hex)).collect::<Vec<_>>().join("|")).collect::<Vec<_>>()})
}
fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!("{b:02x}")).collect()
}
fn write_oracles(profile: Profile, values: &[serde_json::Value]) {
    if let Ok(dir) = std::env::var("NATIVEWEAVE_QUERY_ORACLE_OUTPUT") {
        let path = std::path::Path::new(&dir).join(if profile == Profile::Sqlite {
            "sqlite.json"
        } else {
            "postgres.json"
        });
        std::fs::write(path, serde_json::to_vec_pretty(values).unwrap()).unwrap();
    }
}

#[test]
fn unused_imports_charge_retained_expanded_native_trees() {
    let c = Context::new(Profile::Sqlite);
    let mut source = c.query();
    let v = c.value(&mut source, 1);
    let mut e = source.binary(1, v, v).unwrap();
    for _ in 0..7 {
        e = source.binary(11, e, e).unwrap();
    }
    c.select_all(&mut source);
    let expanded = source.counts().2;
    let mut destination = c.query();
    let mut admitted = 0;
    loop {
        let plan = source.clone_selected().unwrap().seal(8).unwrap();
        let before = destination.counts();
        match destination.import_plan(plan) {
            Ok(_) => {
                admitted += 1;
                assert!(destination.counts().2 >= admitted * expanded);
            }
            Err(_) => {
                assert_eq!(before, destination.counts());
                break;
            }
        }
        assert!(
            admitted < 10,
            "unused imported trees must not escape expanded-node charging"
        );
    }
    assert!(admitted > 0);
    assert!(destination.counts().2 <= Limits::default().nodes);
}

#[test]
fn recursive_self_references_cannot_hide_in_nested_subqueries() {
    let c = Context::new(Profile::Sqlite);
    let declared = single_column_model(&c, "Numbers");
    let mut nested = from_model(&c, declared.clone());
    let id = nested.column(0, column(&declared, "Id")).unwrap();
    select(&mut nested, &[(id, "Id", scalar(3, false))]);
    let mut member = from_model(&c, declared.clone());
    let id = member.column(0, column(&declared, "Id")).unwrap();
    let imported = member.import_plan(nested.seal(8).unwrap()).unwrap();
    let exists = member
        .clause(14, &record(6, vec![token(1, imported), num(2, 0)]))
        .unwrap();
    where_expr(&mut member, exists);
    select(&mut member, &[(id, "Id", scalar(3, false))]);
    let mut anchor = selected_ids(&c, 1, 0);
    let imported = anchor.import_plan(member.seal(8).unwrap()).unwrap();
    anchor
        .clause(9, &record(6, vec![token(1, imported), num(2, 1)]))
        .unwrap();
    let before = anchor.counts();
    assert!(NativeCte::create(declared.clone(), declared.model.table_id(), &anchor, 1).is_err());
    assert_eq!(before, anchor.counts());
}
