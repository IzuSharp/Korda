use nativeweave_data_contracts::{
    ConnectionDisposition, LocalLifetime, Profile, TransactionOutcome,
};
use nativeweave_data_transactions::*;
use std::sync::{
    Arc, Mutex,
    atomic::{AtomicUsize, Ordering},
};
use std::time::Duration;
use tokio::sync::Semaphore;

#[derive(Clone, Copy)]
enum Mode {
    Commit,
    RolledBack,
    Lost,
    Block,
}
struct Probe {
    events: Mutex<Vec<&'static str>>,
    started: Semaphore,
    proceed: Semaphore,
    retired: Semaphore,
    mode: Mode,
    rollback_error: bool,
    block_retire: bool,
    untrusted: bool,
    drops: AtomicUsize,
}
impl Probe {
    fn new(mode: Mode) -> Arc<Self> {
        Arc::new(Self {
            events: Mutex::new(Vec::new()),
            started: Semaphore::new(0),
            proceed: Semaphore::new(0),
            retired: Semaphore::new(0),
            mode,
            rollback_error: false,
            block_retire: false,
            untrusted: false,
            drops: AtomicUsize::new(0),
        })
    }
    fn event(&self, value: &'static str) {
        self.events.lock().unwrap().push(value);
    }
    fn calls(&self, value: &str) -> usize {
        self.events
            .lock()
            .unwrap()
            .iter()
            .filter(|v| **v == value)
            .count()
    }
}
struct Driver {
    probe: Arc<Probe>,
    profile: Profile,
}
impl Drop for Driver {
    fn drop(&mut self) {
        self.probe.drops.fetch_add(1, Ordering::SeqCst);
    }
}
fn failure() -> ProviderFailure {
    ProviderFailure {
        code: Some("08006".into()),
        correlation_id: 71,
    }
}
impl TransactionDriver for Driver {
    fn capabilities(&self) -> Capabilities {
        Capabilities {
            profile: self.profile,
            read_only: self.profile == Profile::PostgreSql,
        }
    }
    fn session_id(&self) -> u64 {
        42
    }
    async fn begin(&mut self, _: &Options) -> Result<(), ProviderFailure> {
        self.probe.event("begin");
        Ok(())
    }
    async fn commit(&mut self) -> Result<Confirmation, ProviderFailure> {
        self.probe.event("commit");
        self.probe.started.add_permits(1);
        match self.probe.mode {
            Mode::Commit => Ok(Confirmation::Committed),
            Mode::RolledBack => Ok(Confirmation::RolledBack(failure())),
            Mode::Lost => Err(failure()),
            Mode::Block => {
                self.probe.proceed.acquire().await.unwrap().forget();
                Ok(Confirmation::Committed)
            }
        }
    }
    async fn rollback(&mut self) -> Result<ProtocolState, ProviderFailure> {
        self.probe.event("rollback");
        if self.probe.rollback_error {
            Err(failure())
        } else {
            Ok(ProtocolState::Clean)
        }
    }
    async fn savepoint_begin(&mut self) -> Result<(), ProviderFailure> {
        self.probe.event("savepoint");
        Ok(())
    }
    async fn savepoint_rollback(&mut self) -> Result<(), ProviderFailure> {
        self.probe.event("rollback-to");
        Ok(())
    }
    async fn savepoint_release(&mut self) -> Result<(), ProviderFailure> {
        self.probe.event("release");
        Ok(())
    }
    async fn recover(&mut self) -> Result<ProtocolState, ProviderFailure> {
        self.probe.event("recover");
        Ok(if self.probe.untrusted {
            ProtocolState::Untrusted
        } else {
            ProtocolState::Clean
        })
    }
    async fn retire(&mut self, _: ConnectionDisposition) {
        self.probe.event("retire");
        self.probe.retired.add_permits(1);
        if self.probe.block_retire {
            self.probe.proceed.acquire().await.unwrap().forget();
        }
    }
}
async fn start(probe: Arc<Probe>, options: Options) -> Transaction<Driver> {
    Transaction::begin(
        Driver {
            probe,
            profile: Profile::PostgreSql,
        },
        options,
        &AdmissionToken::default(),
    )
    .await
    .unwrap()
}
#[tokio::test]
async fn cancellation_before_commit_has_no_provider_effect() {
    let p = Probe::new(Mode::Commit);
    let tx = start(p.clone(), Options::default()).await;
    let cancellation = AdmissionToken::default();
    cancellation.cancel();
    assert_eq!(
        tx.commit(&cancellation).await,
        Err(Error::CanceledBeforeDispatch)
    );
    let s = tx.snapshot();
    assert_eq!(s.state, Lifecycle::Active);
    assert_eq!(s.outcome, TransactionOutcome::InProgress);
    assert!(!s.commit_attempted);
    assert_eq!(p.calls("commit"), 0);
    assert!(tx.join().await.local_resources_released);
    assert_eq!(p.calls("rollback"), 1);
}
#[tokio::test]
async fn request_cancellation_after_dispatch_cannot_cancel_commit() {
    let p = Probe::new(Mode::Block);
    let tx = start(p.clone(), Options::default()).await;
    let other = tx.clone();
    let token = AdmissionToken::default();
    let remote_token = token.clone();
    let work = tokio::spawn(async move { other.commit(&remote_token).await });
    p.started.acquire().await.unwrap().forget();
    token.cancel();
    assert_eq!(tx.snapshot().state, Lifecycle::CommitInFlight);
    assert!(tx.snapshot().commit_attempted);
    assert_eq!(
        tx.rollback(&AdmissionToken::default()).await,
        Err(Error::InvalidState)
    );
    p.proceed.add_permits(1);
    assert_eq!(work.await.unwrap(), Ok(()));
    assert_eq!(tx.join().await.outcome, TransactionOutcome::Committed);
    assert_eq!(p.calls("commit"), 1);
}
#[tokio::test]
async fn deadline_retains_running_future_and_unknown_after_late_confirmation() {
    let p = Probe::new(Mode::Block);
    let tx = start(
        p.clone(),
        Options {
            commit_timeout: Duration::from_millis(5),
            ..Options::default()
        },
    )
    .await;
    assert!(matches!(
        tx.commit(&AdmissionToken::default()).await,
        Err(Error::CommitUnknown { .. })
    ));
    let s = tx.close(Duration::ZERO).await;
    assert_eq!(s.state, Lifecycle::Closed);
    assert_eq!(s.outcome, TransactionOutcome::Unknown);
    assert_eq!(s.cleanup, CleanupStatus::Pending);
    assert_eq!(s.lifetime, LocalLifetime::Closing);
    assert_eq!(s.resources.physical_lease, 1);
    assert_eq!(s.resources.driver_operations, 1);
    assert!(!s.local_resources_released);
    assert_eq!(p.drops.load(Ordering::SeqCst), 0);
    p.proceed.add_permits(1);
    let s = tx.join().await;
    assert_eq!(s.outcome, TransactionOutcome::Unknown);
    assert!(s.local_resources_released);
    assert_eq!(s.resources.physical_lease, 0);
    assert_eq!(p.drops.load(Ordering::SeqCst), 1);
}
#[tokio::test]
async fn response_loss_remains_unknown_after_cleanup_rollback() {
    let p = Probe::new(Mode::Lost);
    let identity = RecoveryIdentity {
        key: "operation-7".into(),
        scope: Some("tenant-a".into()),
        request_hash: Some([7; 32]),
    };
    let tx = start(
        p.clone(),
        Options {
            recovery: Some(identity.clone()),
            ..Options::default()
        },
    )
    .await;
    assert_eq!(
        tx.commit(&AdmissionToken::default()).await,
        Err(Error::CommitUnknown {
            provider_code: Some("08006".into()),
            correlation_id: 71
        })
    );
    assert_eq!(tx.join().await.outcome, TransactionOutcome::Unknown);
    assert_eq!(tx.recovery(), Some(&identity));
    assert_eq!(p.calls("commit"), 1);
    assert_eq!(p.calls("rollback"), 1);
}
#[tokio::test]
async fn failed_transaction_cannot_commit_and_real_rollback_is_distinct() {
    let p = Probe::new(Mode::Commit);
    let tx = start(p.clone(), Options::default()).await;
    let lease = tx.admit_command(false, &AdmissionToken::default()).unwrap();
    assert!(matches!(
        tx.admit_command(false, &AdmissionToken::default()),
        Err(Error::InvalidState)
    ));
    lease.complete(true, ProtocolState::Clean);
    assert_eq!(tx.snapshot().state, Lifecycle::Failed);
    assert_eq!(
        tx.commit(&AdmissionToken::default()).await,
        Err(Error::InvalidState)
    );
    assert_eq!(tx.rollback(&AdmissionToken::default()).await, Ok(()));
    let s = tx.join().await;
    assert_eq!(s.outcome, TransactionOutcome::RolledBack);
    assert_eq!(s.session_id, 42);
    assert_eq!(p.calls("commit"), 0);
}
#[tokio::test]
async fn provider_rollback_confirmation_is_never_commit_success() {
    let p = Probe::new(Mode::RolledBack);
    let tx = start(p, Options::default()).await;
    assert!(matches!(
        tx.commit(&AdmissionToken::default()).await,
        Err(Error::Provider { .. })
    ));
    assert_eq!(tx.join().await.outcome, TransactionOutcome::RolledBack);
}
#[tokio::test]
async fn unconfirmed_rollback_without_commit_preserves_inprogress() {
    let mut p = Probe::new(Mode::Commit);
    let config = Arc::get_mut(&mut p).unwrap();
    config.rollback_error = true;
    config.untrusted = true;
    let tx = start(p, Options::default()).await;
    assert!(matches!(
        tx.rollback(&AdmissionToken::default()).await,
        Err(Error::Cleanup { .. })
    ));
    let s = tx.join().await;
    assert_eq!(s.state, Lifecycle::Closed);
    assert_eq!(s.outcome, TransactionOutcome::InProgress);
    assert!(!s.commit_attempted);
    assert_eq!(s.cleanup, CleanupStatus::RollbackUnconfirmed);
    assert_eq!(s.disposition, ConnectionDisposition::Discarded);
    assert!(s.local_resources_released);
}
#[tokio::test]
async fn savepoints_are_lifo_keep_rollback_scope_and_preserve_outer() {
    let p = Probe::new(Mode::Commit);
    let tx = start(p.clone(), Options::default()).await;
    let token = AdmissionToken::default();
    let a = tx.create_savepoint(&token).await.unwrap();
    let b = tx.create_savepoint(&token).await.unwrap();
    assert_eq!(
        tx.release_savepoint(a, &token).await,
        Err(Error::SavepointOrder)
    );
    tx.admit_command(false, &token)
        .unwrap()
        .complete(true, ProtocolState::Clean);
    assert_eq!(
        tx.release_savepoint(b, &token).await,
        Err(Error::InvalidState)
    );
    tx.rollback_savepoint(b, &token).await.unwrap();
    assert_eq!(tx.snapshot().state, Lifecycle::Active);
    tx.release_savepoint(b, &token).await.unwrap();
    tx.dispose_savepoint(a).await.unwrap();
    assert_eq!(tx.snapshot().state, Lifecycle::Active);
    assert_eq!(p.calls("commit"), 0);
    assert_eq!(p.calls("rollback-to"), 2);
    assert_eq!(p.calls("release"), 2);
    tx.join().await;
    assert_eq!(tx.dispose_savepoint(a).await, Ok(()));
}
#[tokio::test]
async fn nontop_disposal_closes_admission_without_reordering_sql() {
    let p = Probe::new(Mode::Commit);
    let tx = start(p.clone(), Options::default()).await;
    let token = AdmissionToken::default();
    let a = tx.create_savepoint(&token).await.unwrap();
    let _b = tx.create_savepoint(&token).await.unwrap();
    assert_eq!(tx.dispose_savepoint(a).await, Err(Error::SavepointOrder));
    let s = tx.join().await;
    assert_eq!(s.cleanup, CleanupStatus::Failed);
    assert_eq!(p.calls("rollback-to"), 0);
    assert_eq!(p.calls("rollback"), 1);
}
#[tokio::test]
async fn bounded_close_never_reports_queued_retirement_as_released() {
    let mut p = Probe::new(Mode::Commit);
    Arc::get_mut(&mut p).unwrap().block_retire = true;
    let tx = start(
        p.clone(),
        Options {
            cleanup_timeout: Duration::from_millis(5),
            ..Options::default()
        },
    )
    .await;
    assert_eq!(tx.commit(&AdmissionToken::default()).await, Ok(()));
    p.retired.acquire().await.unwrap().forget();
    let s = tx.close(Duration::from_millis(10)).await;
    assert_eq!(s.cleanup, CleanupStatus::Pending);
    assert!(!s.local_resources_released);
    assert!(s.deadline_exceeded);
    assert_eq!(s.resources.physical_lease, 1);
    p.proceed.add_permits(1);
    assert!(tx.join().await.local_resources_released);
}
#[tokio::test]
async fn stale_generation_and_cancellation_cannot_affect_next_owner() {
    let p = Probe::new(Mode::Commit);
    let tx = start(p.clone(), Options::default()).await;
    let old = AdmissionToken::default();
    let savepoint = tx.create_savepoint(&old).await.unwrap();
    tx.join().await;
    let next = start(p.clone(), Options::default()).await;
    let current = AdmissionToken::default();
    let valid = next.create_savepoint(&current).await.unwrap();
    old.cancel();
    assert_eq!(
        next.rollback_savepoint(savepoint, &current).await,
        Err(Error::SavepointOrder)
    );
    next.release_savepoint(valid, &current).await.unwrap();
    assert_eq!(next.commit(&current).await, Ok(()));
    next.join().await;
}
#[tokio::test]
async fn capability_and_raw_control_reject_before_provider_traffic() {
    let p = Probe::new(Mode::Commit);
    for options in [
        Options {
            isolation: Some(Isolation::ReadCommitted),
            ..Options::default()
        },
        Options {
            read_only: true,
            ..Options::default()
        },
    ] {
        assert!(matches!(
            Transaction::begin(
                Driver {
                    probe: p.clone(),
                    profile: Profile::Sqlite
                },
                options,
                &AdmissionToken::default()
            )
            .await,
            Err(Error::UnsupportedCapability)
        ));
    }
    assert_eq!(p.calls("begin"), 0);
    let tx = start(p.clone(), Options::default()).await;
    assert!(matches!(
        tx.admit_command(true, &AdmissionToken::default()),
        Err(Error::InvalidState)
    ));
    tx.join().await;
}
#[tokio::test]
async fn last_owner_drop_retains_cleanup_until_driver_retirement() {
    let mut p = Probe::new(Mode::Commit);
    Arc::get_mut(&mut p).unwrap().block_retire = true;
    let tx = start(p.clone(), Options::default()).await;
    drop(tx);
    p.retired.acquire().await.unwrap().forget();
    assert_eq!(p.calls("rollback"), 1);
    assert_eq!(p.drops.load(Ordering::SeqCst), 0);
    p.proceed.add_permits(1);
    tokio::time::timeout(Duration::from_secs(1), async {
        while p.drops.load(Ordering::SeqCst) == 0 {
            tokio::task::yield_now().await;
        }
    })
    .await
    .unwrap();
}
