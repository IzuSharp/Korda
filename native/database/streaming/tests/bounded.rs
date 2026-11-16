use futures_util::{FutureExt, StreamExt, stream};
use nativeweave_data_contracts::{ConnectionDisposition, Profile, wire::Batch};
use nativeweave_data_streaming::{
    Budget, CodecRow, ColumnMetadata, Cursor, ErrorKind, Fetch, Limits, Result, Retirement, Shape,
    Source, StreamError,
};
use std::sync::{
    Arc,
    atomic::{AtomicBool, AtomicUsize, Ordering},
};
use tokio::{
    runtime::Handle,
    sync::Notify,
    time::{Duration, timeout},
};

const PROJECTION: &[u8] = br#"{"version":1,"fields":[{"ordinal":0,"name":"value","codec":5,"codec_config":null,"nullable":true}]}"#;
fn shape() -> Shape {
    Shape::parse(PROJECTION, Profile::Sqlite, 7, &[5], false).unwrap()
}
fn columns() -> Vec<ColumnMetadata> {
    vec![ColumnMetadata {
        ordinal: 0,
        name: "value".into(),
        type_name: Some("TEXT".into()),
    }]
}
fn limits() -> Limits {
    Limits {
        max_rows: 3,
        max_batch_bytes: 256,
        max_field_bytes: 128,
    }
}
fn budget() -> Budget {
    Budget::new(1, 64 * 1024).unwrap()
}
fn receipt() -> Retirement {
    Retirement {
        native_retired: true,
        canceled: false,
        disposition: ConnectionDisposition::Usable,
        provider_rows_peak: 1,
    }
}
struct Row {
    cells: Vec<Option<Vec<u8>>>,
    valid: bool,
    decoded: Arc<AtomicUsize>,
}
impl CodecRow for Row {
    fn preflight(&self, _: &Shape) -> Result<()> {
        if self.valid {
            Ok(())
        } else {
            Err(StreamError {
                kind: ErrorKind::Shape,
                message: "wrong complete shape",
            })
        }
    }
    fn scalar(&self, ordinal: usize, _: &Shape, max: usize) -> Result<Option<Vec<u8>>> {
        self.decoded.fetch_add(1, Ordering::SeqCst);
        let cell = &self.cells[ordinal];
        if cell.as_ref().is_some_and(|v| v.len() > max) {
            return Err(StreamError {
                kind: ErrorKind::LargeValueRequiresStreaming,
                message: "oversized field",
            });
        }
        Ok(cell.clone())
    }
}
fn source(
    count: usize,
    length: usize,
    pulled: Arc<AtomicUsize>,
    stopped: Arc<AtomicBool>,
    joined: Arc<AtomicBool>,
) -> Source<Row> {
    Source {
        columns: columns(),
        rows: Box::pin(stream::iter((0..count).map(move |_| {
            pulled.fetch_add(1, Ordering::SeqCst);
            Ok(Row {
                cells: vec![Some(vec![b'x'; length])],
                valid: true,
                decoded: Arc::new(AtomicUsize::new(0)),
            })
        }))),
        request_stop: Arc::new(move || {
            stopped.store(true, Ordering::SeqCst);
        }),
        join: Box::pin(async move {
            joined.store(true, Ordering::SeqCst);
            Ok(receipt())
        }),
    }
}

#[tokio::test]
async fn empty_one_multibatch_slow_consumer_and_take_once() {
    for count in [0, 1, 17] {
        let budget = budget();
        let pulled = Arc::new(AtomicUsize::new(0));
        let stopped = Arc::new(AtomicBool::new(false));
        let joined = Arc::new(AtomicBool::new(false));
        let mut cursor = Cursor::open(shape(), limits(), &budget, Handle::current(), || async {
            Ok(source(
                count,
                50,
                pulled.clone(),
                stopped.clone(),
                joined.clone(),
            ))
        })
        .await
        .unwrap();
        assert_eq!(
            pulled.load(Ordering::SeqCst),
            0,
            "no B06 prefetch before fetch"
        );
        let expected = shape().expectation().clone();
        let mut seen = 0;
        let mut batches = 0;
        while let Fetch::Ready { rows, end } = cursor.fetch().await.unwrap() {
            assert!(rows <= 3);
            let owner = cursor.take_batch().unwrap();
            assert!(owner.bytes().len() <= 256);
            assert_eq!(owner.rows(), rows);
            assert_eq!(owner.end(), end);
            assert!(cursor.take_batch().is_err());
            assert!(cursor.fetch().await.is_err());
            let before = pulled.load(Ordering::SeqCst);
            tokio::time::sleep(Duration::from_millis(5)).await;
            assert_eq!(
                before,
                pulled.load(Ordering::SeqCst),
                "slow consumer stops producer pulls"
            );
            assert_eq!(
                Batch::parse(owner.bytes(), expected.clone())
                    .unwrap()
                    .rows() as usize,
                rows
            );
            assert_eq!(budget.snapshot().live_batches, 1);
            seen += rows;
            batches += 1;
            drop(owner);
        }
        assert_eq!(seen, count);
        assert!(batches > 0);
        assert!(joined.load(Ordering::SeqCst));
        assert!(cursor.close().await.unwrap().native_retired);
        assert!(cursor.close().await.unwrap().native_retired);
        drop(cursor);
        let snap = budget.snapshot();
        assert_eq!(snap.reserved_bytes, 0);
        assert_eq!(snap.live_batches, 0);
        assert_eq!(snap.live_cursors, 0);
        assert!(snap.peak_batch_bytes <= 256);
    }
}

#[tokio::test]
async fn owned_batch_outlives_cursor_and_transfers_its_budget() {
    let budget = budget();
    let mut cursor = Cursor::open(shape(), limits(), &budget, Handle::current(), || async {
        Ok(source(
            5,
            10,
            Arc::new(AtomicUsize::new(0)),
            Arc::new(AtomicBool::new(false)),
            Arc::new(AtomicBool::new(false)),
        ))
    })
    .await
    .unwrap();
    cursor.fetch().await.unwrap();
    let owner = cursor.take_batch().unwrap();
    cursor.close().await.unwrap();
    drop(cursor);
    assert_eq!(budget.snapshot().live_cursors, 1);
    assert_eq!(budget.snapshot().live_batches, 1);
    assert_eq!(
        Batch::parse(owner.bytes(), shape().expectation().clone())
            .unwrap()
            .rows(),
        3
    );
    let attempts = Arc::new(AtomicUsize::new(0));
    let called = attempts.clone();
    let rejected = Cursor::open(
        shape(),
        limits(),
        &budget,
        Handle::current(),
        || async move {
            called.fetch_add(1, Ordering::SeqCst);
            Ok(source(
                0,
                0,
                Arc::new(AtomicUsize::new(0)),
                Arc::new(AtomicBool::new(false)),
                Arc::new(AtomicBool::new(false)),
            ))
        },
    )
    .await;
    assert!(matches!(
        rejected,
        Err(StreamError {
            kind: ErrorKind::Capacity,
            ..
        })
    ));
    assert_eq!(attempts.load(Ordering::SeqCst), 0);
    drop(owner);
    assert_eq!(budget.snapshot().reserved_bytes, 0);
}

#[tokio::test]
async fn oversized_field_and_shape_errors_stop_before_more_reads() {
    let budget = budget();
    let pulled = Arc::new(AtomicUsize::new(0));
    let stopped = Arc::new(AtomicBool::new(false));
    let joined = Arc::new(AtomicBool::new(false));
    let mut cursor = Cursor::open(shape(), limits(), &budget, Handle::current(), || async {
        Ok(source(
            5,
            129,
            pulled.clone(),
            stopped.clone(),
            joined.clone(),
        ))
    })
    .await
    .unwrap();
    assert_eq!(
        cursor.fetch().await.unwrap_err().kind,
        ErrorKind::LargeValueRequiresStreaming
    );
    assert_eq!(pulled.load(Ordering::SeqCst), 1);
    assert!(stopped.load(Ordering::SeqCst));
    assert!(joined.load(Ordering::SeqCst));
    drop(cursor);
    assert_eq!(budget.snapshot().reserved_bytes, 0);
    let decoded = Arc::new(AtomicUsize::new(0));
    let invalid = Row {
        cells: vec![None],
        valid: false,
        decoded: decoded.clone(),
    };
    let mut cursor = Cursor::open(shape(), limits(), &budget, Handle::current(), || async {
        Ok(Source {
            columns: columns(),
            rows: Box::pin(stream::iter(vec![Ok(invalid)])),
            request_stop: Arc::new(|| {}),
            join: Box::pin(async { Ok(receipt()) }),
        })
    })
    .await
    .unwrap();
    assert_eq!(cursor.fetch().await.unwrap_err().kind, ErrorKind::Shape);
    assert_eq!(decoded.load(Ordering::SeqCst), 0);
    drop(cursor);
    let mut empty = source(
        0,
        0,
        Arc::new(AtomicUsize::new(0)),
        stopped.clone(),
        joined.clone(),
    );
    empty.columns.clear();
    assert!(matches!(
        Cursor::open(shape(), limits(), &budget, Handle::current(), || async {
            Ok(empty)
        })
        .await,
        Err(StreamError {
            kind: ErrorKind::Shape,
            ..
        })
    ));
    assert_eq!(budget.snapshot().reserved_bytes, 0);
}

#[tokio::test]
async fn abandoned_cursor_and_canceled_close_keep_cleanup_charge_until_receipt() {
    let budget = budget();
    let allow = Arc::new(Notify::new());
    let joined = Arc::new(AtomicBool::new(false));
    let stopped = Arc::new(AtomicBool::new(false));
    let permit = allow.clone();
    let done = joined.clone();
    let stop = stopped.clone();
    let mut cursor = Cursor::open(shape(), limits(), &budget, Handle::current(), || async {
        Ok(Source::<Row> {
            columns: columns(),
            rows: Box::pin(stream::pending()),
            request_stop: Arc::new(move || {
                stop.store(true, Ordering::SeqCst);
            }),
            join: Box::pin(async move {
                permit.notified().await;
                done.store(true, Ordering::SeqCst);
                Ok(receipt())
            }),
        })
    })
    .await
    .unwrap();
    {
        let mut close = Box::pin(cursor.close());
        assert!(close.as_mut().now_or_never().is_none());
    }
    assert!(stopped.load(Ordering::SeqCst));
    assert!(!joined.load(Ordering::SeqCst));
    assert_eq!(budget.snapshot().live_cursors, 1);
    drop(cursor);
    tokio::task::yield_now().await;
    assert_eq!(
        budget.snapshot().live_cursors,
        1,
        "dropping a future is not retirement"
    );
    allow.notify_one();
    timeout(Duration::from_secs(2), async {
        while budget.snapshot().live_cursors != 0 {
            tokio::task::yield_now().await;
        }
    })
    .await
    .unwrap();
    assert!(joined.load(Ordering::SeqCst));
    assert_eq!(budget.snapshot().reserved_bytes, 0);
}

#[tokio::test]
async fn abandoned_partial_fetch_rejects_resume_and_close_still_joins() {
    let budget = budget();
    let decoded = Arc::new(AtomicUsize::new(0));
    let stopped = Arc::new(AtomicBool::new(false));
    let joined = Arc::new(AtomicBool::new(false));
    let allow = Arc::new(Notify::new());
    let stop = stopped.clone();
    let done = joined.clone();
    let permit = allow.clone();
    let first = Row {
        cells: vec![Some(vec![b'x'; 5])],
        valid: true,
        decoded: decoded.clone(),
    };
    let mut cursor = Cursor::open(shape(), limits(), &budget, Handle::current(), || async {
        Ok(Source {
            columns: columns(),
            rows: Box::pin(stream::iter([Ok(first)]).chain(stream::pending())),
            request_stop: Arc::new(move || stop.store(true, Ordering::SeqCst)),
            join: Box::pin(async move {
                permit.notified().await;
                done.store(true, Ordering::SeqCst);
                Ok(receipt())
            }),
        })
    })
    .await
    .unwrap();
    {
        let mut fetch = Box::pin(cursor.fetch());
        assert!(fetch.as_mut().now_or_never().is_none());
        assert_eq!(decoded.load(Ordering::SeqCst), 1);
    }
    assert_eq!(
        cursor.fetch().await.unwrap_err().kind,
        ErrorKind::InvalidState
    );
    assert!(cursor.take_batch().is_err());
    assert_eq!(budget.snapshot().live_cursors, 1);
    assert!(!joined.load(Ordering::SeqCst));
    let mut close = Box::pin(cursor.close());
    assert!(close.as_mut().now_or_never().is_none());
    assert!(stopped.load(Ordering::SeqCst));
    allow.notify_one();
    assert!(close.await.unwrap().native_retired);
    drop(cursor);
    assert_eq!(budget.snapshot().reserved_bytes, 0);
    assert!(joined.load(Ordering::SeqCst));
}

#[tokio::test]
async fn unknown_retirement_isolated_and_invalid_limits_never_dispatch() {
    let budget = budget();
    let mut cursor = Cursor::open(shape(), limits(), &budget, Handle::current(), || async {
        Ok(Source::<Row> {
            columns: columns(),
            rows: Box::pin(stream::pending()),
            request_stop: Arc::new(|| {}),
            join: Box::pin(async {
                Ok(Retirement {
                    native_retired: false,
                    canceled: false,
                    disposition: ConnectionDisposition::Quarantined,
                    provider_rows_peak: 0,
                })
            }),
        })
    })
    .await
    .unwrap();
    assert_eq!(
        cursor.close().await.unwrap_err().kind,
        ErrorKind::CleanupPending
    );
    drop(cursor);
    assert_eq!(
        budget.snapshot().live_cursors,
        1,
        "unknown cleanup retains a bounded reservation"
    );
    assert!(
        Limits {
            max_rows: usize::MAX,
            ..limits()
        }
        .validate()
        .is_err()
    );
    assert!(
        shape()
            .validate_limits(Limits {
                max_batch_bytes: 135,
                ..limits()
            })
            .is_err()
    );
    let mut fields = columns();
    fields[0].name = "missing_nullable".into();
    assert!(shape().validate_columns(&fields).is_err());
    fields[0].name = "value".into();
    fields[0].type_name = Some("INTEGER".into());
    assert!(shape().validate_columns(&fields).is_err());
}
