use crate::{
    blobs::{Blobs, Upload},
    db::{DataRow, Database, Lease, Value},
    protocol::{
        self, ATTACH, AttachedItem, Attachment, CREATE, CreateItem, CreatedItem, Cursor, Error,
        Identity, ItemPage, ItemView, Operation, Replay, Result,
    },
};
use sea_query::{Alias, Expr, ExprTrait, Order, PostgresQueryBuilder, Query, SqliteQueryBuilder};
use std::sync::Arc;

const OPERATION_COLUMNS: &str = "scope,request_key,request_hash,operation_id,tenant_id,principal_id,semantic,response_version,response_status,response_bytes,completed_at";
pub struct Service {
    pub source: Arc<Database>,
    consumer: Arc<Database>,
    pub blobs: Arc<Blobs>,
    cursor: Cursor,
}
impl Service {
    pub fn new(
        source: Arc<Database>,
        consumer: Arc<Database>,
        blobs: Arc<Blobs>,
        cursor: Cursor,
    ) -> Self {
        Self {
            source,
            consumer,
            blobs,
            cursor,
        }
    }
    fn operation(row: &DataRow) -> Result<Operation> {
        Ok(Operation {
            scope: row.text(0)?,
            key: row.text(1)?,
            hash: row.text(2)?,
            operation_id: row.text(3)?,
            tenant: row.text(4)?,
            principal: row.text(5)?,
            semantic: row.text(6)?,
            response_version: row.integer(7)?,
            status: row.integer(8)?,
            bytes: row.bytes(9)?,
            completed_at: row.integer(10)?,
        })
    }
    async fn claim(lease: &mut Lease, result: &Operation) -> Result<Option<Replay>> {
        let rows=lease.query(&format!("INSERT INTO operations({OPERATION_COLUMNS}) VALUES($1,$2,$3,$4,$5,$6,$7,1,$8,$9,$10) ON CONFLICT(scope,request_key) DO NOTHING RETURNING operation_id"),
            vec![result.scope.as_str().into(),result.key.as_str().into(),result.hash.as_str().into(),result.operation_id.as_str().into(),result.tenant.as_str().into(),result.principal.as_str().into(),result.semantic.as_str().into(),result.status.into(),result.bytes.as_slice().into(),result.completed_at.into()]).await?;
        if !rows.is_empty() {
            return Ok(None);
        }
        // Deliberately a separate READ COMMITTED statement after DO NOTHING.
        let rows = lease
            .query(
                &format!(
                    "SELECT {OPERATION_COLUMNS} FROM operations WHERE scope=$1 AND request_key=$2"
                ),
                vec![result.scope.as_str().into(), result.key.as_str().into()],
            )
            .await?;
        if rows.len() != 1 {
            return Err(Error(503, "outcome_unconfirmed"));
        }
        let winner = Self::operation(&rows[0])?;
        if winner.tenant != result.tenant
            || winner.principal != result.principal
            || winner.semantic != result.semantic
        {
            return Err(Error(500, "scope_identity_mismatch"));
        }
        if winner.hash != result.hash {
            return Err(Error(409, "idempotency_conflict"));
        }
        if winner.response_version != 1 {
            return Err(Error(500, "unsupported_response_version"));
        }
        Ok(Some(Replay {
            status: winner.status as u16,
            bytes: winner.bytes,
            operation_id: winner.operation_id,
        }))
    }
    async fn insert_outbox(lease: &mut Lease, result: &Operation) -> Result<()> {
        lease.execute("INSERT INTO outbox(event_id,tenant_id,operation_id,payload_version,payload,delivery_state,attempt_count,available_at,lease_until,completed_at) VALUES($1,$2,$3,1,$4,0,0,$5,0,0)",
            vec![protocol::uuid().into(),result.tenant.as_str().into(),result.operation_id.as_str().into(),result.bytes.as_slice().into(),result.completed_at.into()]).await?;
        Ok(())
    }
    async fn commit(lease: &mut Lease, result: Operation) -> Result<Replay> {
        match lease.commit().await {
            Ok(()) => Ok(Replay {
                status: result.status as u16,
                bytes: result.bytes,
                operation_id: result.operation_id,
            }),
            Err(_) if lease.unknown => Ok(Replay {
                status: 503,
                bytes: protocol::problem("outcome_unknown", Some(&result.operation_id)),
                operation_id: result.operation_id,
            }),
            Err(error) => Err(error),
        }
    }
    pub async fn create(
        &self,
        identity: &Identity,
        key: String,
        input: CreateItem,
    ) -> Result<Replay> {
        protocol::title(&input.title)?;
        let operation = protocol::uuid();
        let item = protocol::uuid();
        let now = protocol::now();
        let response = protocol::json(&CreatedItem {
            response_version: 1,
            operation_id: operation.clone(),
            item_id: item.clone(),
            title: input.title.clone(),
            created_at: now,
            version: 1,
        });
        let result = Operation {
            scope: protocol::scope(identity, CREATE),
            key,
            hash: protocol::create_hash(identity, &input.title),
            operation_id: operation.clone(),
            tenant: identity.tenant.clone(),
            principal: identity.principal.clone(),
            semantic: CREATE.into(),
            response_version: 1,
            status: 201,
            bytes: response,
            completed_at: now,
        };
        let mut lease = self.source.open().await?;
        lease.begin().await?;
        if let Some(replay) = Self::claim(&mut lease, &result).await? {
            return Ok(replay);
        }
        lease.execute("INSERT INTO items(tenant_id,item_id,operation_id,title,created_at,version) VALUES($1,$2,$3,$4,$5,1)",vec![identity.tenant.as_str().into(),item.into(),operation.as_str().into(),input.title.into(),now.into()]).await?;
        Self::insert_outbox(&mut lease, &result).await?;
        let blobs = self.blobs.clone();
        let retained = operation.clone();
        lease.on_rollback(move || blobs.confirm(&retained, false));
        self.blobs.protect(&result)?;
        let replay = Self::commit(&mut lease, result).await?;
        if replay.status != 503 {
            self.blobs.confirm(&operation, false);
        }
        Ok(replay)
    }
    pub async fn recover(&self, identity: &Identity, operation: &str) -> Result<Replay> {
        protocol::id(operation)?;
        let mut lease = self.source.open().await?;
        let rows=lease.query(&format!("SELECT {OPERATION_COLUMNS} FROM operations WHERE operation_id=$1 AND tenant_id=$2 AND principal_id=$3"),vec![operation.into(),identity.tenant.as_str().into(),identity.principal.as_str().into()]).await?;
        if rows.len() != 1 {
            return Err(Error(404, "outcome_unconfirmed"));
        }
        let result = Self::operation(&rows[0])?;
        Ok(Replay {
            status: 200,
            bytes: result.bytes,
            operation_id: result.operation_id,
        })
    }
    pub async fn list(
        &self,
        identity: &Identity,
        limit: u64,
        after: Option<&str>,
    ) -> Result<ItemPage> {
        if !(1..=100).contains(&limit) {
            return Err(Error(400, "invalid_limit"));
        }
        let cursor = after
            .map(|value| self.cursor.decode(identity, value))
            .transpose()?;
        let mut query = Query::select();
        query
            .columns([
                Alias::new("item_id"),
                Alias::new("title"),
                Alias::new("created_at"),
                Alias::new("version"),
            ])
            .from(Alias::new("items"))
            .and_where(Expr::col(Alias::new("tenant_id")).eq(identity.tenant.clone()));
        if let Some((created, item)) = cursor {
            query.and_where(
                Expr::col(Alias::new("created_at"))
                    .gt(created)
                    .or(Expr::col(Alias::new("created_at"))
                        .eq(created)
                        .and(Expr::col(Alias::new("item_id")).gt(item))),
            );
        }
        query
            .order_by(Alias::new("created_at"), Order::Asc)
            .order_by(Alias::new("item_id"), Order::Asc)
            .limit(limit);
        let (sql, values) = if self.source.is_postgres() {
            query.build(PostgresQueryBuilder)
        } else {
            query.build(SqliteQueryBuilder)
        };
        let parameters = values
            .0
            .into_iter()
            .map(|value| match value {
                sea_query::Value::String(Some(value)) => Ok(Value::Text(value)),
                sea_query::Value::BigInt(Some(value)) => Ok(Value::Integer(value)),
                sea_query::Value::BigUnsigned(Some(value)) => i64::try_from(value)
                    .map(Value::Integer)
                    .map_err(|_| Error(500, "query_value_overflow")),
                _ => Err(Error(500, "unexpected_query_value")),
            })
            .collect::<Result<Vec<_>>>()?;
        let mut lease = self.source.open().await?;
        let rows = lease.query(&sql, parameters).await?;
        let items = rows
            .iter()
            .map(|row| {
                Ok(ItemView {
                    item_id: row.text(0)?,
                    title: row.text(1)?,
                    created_at: row.integer(2)?,
                    version: row.integer(3)?,
                })
            })
            .collect::<Result<Vec<_>>>()?;
        let next_cursor = if items.len() == limit as usize {
            let last = items.last().expect("positive page limit");
            Some(self.cursor.encode(identity, last.created_at, &last.item_id))
        } else {
            None
        };
        Ok(ItemPage { items, next_cursor })
    }
    pub async fn authorize_item(&self, identity: &Identity, item: &str) -> Result<()> {
        let mut lease = self.source.open().await?;
        if lease
            .query(
                "SELECT item_id FROM items WHERE tenant_id=$1 AND item_id=$2",
                vec![identity.tenant.as_str().into(), item.into()],
            )
            .await?
            .len()
            != 1
        {
            return Err(Error(404, "not_found"));
        }
        Ok(())
    }
    pub async fn attachment(&self, identity: &Identity, item: &str) -> Result<Attachment> {
        let mut lease = self.source.open().await?;
        let rows=lease.query("SELECT content_hash,content_length,version FROM attachments WHERE tenant_id=$1 AND item_id=$2",vec![identity.tenant.as_str().into(),item.into()]).await?;
        if rows.len() != 1 {
            return Err(Error(404, "not_found"));
        }
        Ok(Attachment {
            hash: rows[0].text(0)?,
            length: rows[0].integer(1)?,
            version: rows[0].integer(2)?,
        })
    }
    pub async fn bind(
        &self,
        identity: &Identity,
        key: String,
        item: &str,
        expected: i64,
        upload: &mut Upload,
    ) -> Result<Replay> {
        let manifest = upload.manifest.clone();
        let version = expected
            .checked_add(1)
            .ok_or(Error(400, "invalid_expected_version"))?;
        let hash = manifest
            .content_hash
            .as_ref()
            .ok_or(Error(500, "upload_not_durable"))?;
        let response = protocol::json(&AttachedItem {
            response_version: 1,
            operation_id: manifest.operation_id.clone(),
            item_id: item.into(),
            content_hash: hash.clone(),
            content_length: manifest.content_length,
            version,
        });
        let result = Operation {
            scope: protocol::scope(identity, ATTACH),
            key,
            hash: protocol::attachment_hash(
                identity,
                item,
                hash,
                manifest.content_length,
                expected,
            ),
            operation_id: manifest.operation_id.clone(),
            tenant: identity.tenant.clone(),
            principal: identity.principal.clone(),
            semantic: ATTACH.into(),
            response_version: 1,
            status: 200,
            bytes: response,
            completed_at: protocol::now(),
        };
        let mut lease = self.source.open().await?;
        lease.begin().await?;
        upload.begin_binding();
        let blobs = self.blobs.clone();
        let retained = manifest.operation_id.clone();
        lease.on_rollback(move || blobs.confirm(&retained, true));
        if let Some(replay) = Self::claim(&mut lease, &result).await? {
            return Ok(replay);
        }
        if lease
            .execute(
                "UPDATE items SET version=$1 WHERE tenant_id=$2 AND item_id=$3 AND version=$4",
                vec![
                    version.into(),
                    identity.tenant.as_str().into(),
                    item.into(),
                    expected.into(),
                ],
            )
            .await?
            != 1
        {
            return Err(Error(412, "version_conflict"));
        }
        lease.execute("INSERT INTO attachments(tenant_id,item_id,content_hash,content_length,operation_id,version) VALUES($1,$2,$3,$4,$5,$6) ON CONFLICT(tenant_id,item_id) DO UPDATE SET content_hash=excluded.content_hash,content_length=excluded.content_length,operation_id=excluded.operation_id,version=excluded.version",
            vec![identity.tenant.as_str().into(),item.into(),hash.as_str().into(),manifest.content_length.into(),manifest.operation_id.as_str().into(),version.into()]).await?;
        Self::insert_outbox(&mut lease, &result).await?;
        Self::commit(&mut lease, result).await
    }
    pub async fn outbox_once(&self) -> Result<usize> {
        let entries = self.claim_outbox().await?;
        let mut count = 0;
        for entry in entries {
            let delivered = self.publish(&entry).await.is_ok();
            self.acknowledge(&entry, delivered).await?;
            if delivered {
                count += 1;
            }
        }
        Ok(count)
    }
    async fn claim_outbox(&self) -> Result<Vec<Outbox>> {
        let mut lease = self.source.open().await?;
        lease.begin().await?;
        let now = protocol::now();
        let rows=lease.query(&format!("SELECT event_id,operation_id,payload,attempt_count FROM outbox WHERE delivery_state<2 AND available_at<=$1 AND lease_until<=$1 ORDER BY available_at,event_id LIMIT 16{}",if self.source.is_postgres(){" FOR UPDATE SKIP LOCKED"}else{""}),vec![now.into()]).await?;
        let mut claimed = Vec::new();
        for row in rows {
            let event = row.text(0)?;
            let attempts = row.integer(3)?;
            if attempts >= 16 {
                lease.execute("UPDATE outbox SET delivery_state=3,lease_until=0 WHERE event_id=$1 AND attempt_count=$2",vec![event.into(),attempts.into()]).await?;
                continue;
            }
            if lease.execute("UPDATE outbox SET delivery_state=1,attempt_count=$1,lease_until=$2 WHERE event_id=$3 AND attempt_count=$4",vec![(attempts+1).into(),(now+30000).into(),event.as_str().into(),attempts.into()]).await?!=1{return Err(Error(500,"outbox_claim_conflict"));}
            claimed.push(Outbox {
                event,
                payload: row.bytes(2)?,
                attempts: attempts + 1,
            });
        }
        lease.commit().await?;
        Ok(claimed)
    }
    async fn publish(&self, entry: &Outbox) -> Result<()> {
        let mut lease = self.consumer.open().await?;
        lease.begin().await?;
        let hash = protocol::hash(&entry.payload);
        if lease.query("INSERT INTO receipts(event_id,payload_hash,delivery_attempts,effects) VALUES($1,$2,1,1) ON CONFLICT(event_id) DO NOTHING RETURNING event_id",vec![entry.event.as_str().into(),hash.as_str().into()]).await?.is_empty(){
            let rows=lease.query("SELECT payload_hash,delivery_attempts,effects FROM receipts WHERE event_id=$1",vec![entry.event.as_str().into()]).await?;
            if rows.len()!=1||rows[0].text(0)?!=hash||rows[0].integer(2)?!=1{return Err(Error(500,"consumer_identity_mismatch"));}let attempts=rows[0].integer(1)?;
            if lease.execute("UPDATE receipts SET delivery_attempts=$1 WHERE event_id=$2 AND delivery_attempts=$3",vec![(attempts+1).into(),entry.event.as_str().into(),attempts.into()]).await?!=1{return Err(Error(500,"consumer_cas_failed"));}
        }
        lease.commit().await
    }
    async fn acknowledge(&self, entry: &Outbox, delivered: bool) -> Result<()> {
        let mut lease = self.source.open().await?;
        let now = protocol::now();
        let state = if delivered {
            2i64
        } else if entry.attempts >= 16 {
            3
        } else {
            0
        };
        let backoff = std::cmp::min(1000i64 << std::cmp::min(entry.attempts, 18), 300000);
        if lease.execute("UPDATE outbox SET delivery_state=$1,lease_until=0,available_at=$2,completed_at=$3 WHERE event_id=$4 AND attempt_count=$5",vec![state.into(),(now+backoff).into(),(if delivered{now}else{0}).into(),entry.event.as_str().into(),entry.attempts.into()]).await?!=1{return Err(Error(500,"outbox_ack_conflict"));}
        Ok(())
    }
}
struct Outbox {
    event: String,
    payload: Vec<u8>,
    attempts: i64,
}
