//! Test-only synchronous ABI for actual component JIT/AOT acceptance. This is not
//! the B04 host API. Fixture runtime construction is explicit and joins all cases.
#[path = "../../../../native/database/sqlite/tests/support/scenarios.rs"]
mod scenarios;
use nativeweave_data_sqlite::{Limits, Options};
use std::{
    path::{Path, PathBuf},
    sync::atomic::{AtomicU32, Ordering},
};

static RUNTIMES: AtomicU32 = AtomicU32::new(0);

fn runtime() -> std::io::Result<tokio::runtime::Runtime> {
    RUNTIMES.fetch_add(1, Ordering::Relaxed);
    tokio::runtime::Builder::new_multi_thread()
        .worker_threads(2)
        .enable_all()
        .build()
}
unsafe fn input<'a>(pointer: *const u8, length: u32) -> Option<&'a [u8]> {
    if pointer.is_null() || length == 0 || length > 4 * 1024 * 1024 {
        return None;
    }
    // SAFETY: each test-only caller promises a live borrowed byte range for this
    // synchronous call; the scalar/null bound checks precede construction.
    Some(unsafe { std::slice::from_raw_parts(pointer, length as usize) })
}
unsafe fn path(pointer: *const u8, length: u32) -> Option<PathBuf> {
    // SAFETY: forwarded test-only input buffer contract; text is copied before retention.
    let text = std::str::from_utf8(unsafe { input(pointer, length) }?).ok()?;
    if text.contains('\0') {
        return None;
    }
    Some(PathBuf::from(text))
}

#[unsafe(no_mangle)]
pub extern "C" fn nw_b03_runtime_count() -> u32 {
    RUNTIMES.load(Ordering::Relaxed)
}

/// Runs all real provider/transaction scenarios in one explicit test runtime.
/// # Safety
/// `root` denotes `length` live UTF-8 bytes borrowed until return.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_b03_run_suite(root: *const u8, length: u32) -> u32 {
    // SAFETY: forwarded synchronous caller buffer contract.
    let Some(root) = (unsafe { path(root, length) }) else {
        return 1;
    };
    match std::panic::catch_unwind(|| -> scenarios::TestResult {
        let runtime = runtime()?;
        runtime.block_on(async {
            for case in 1..=scenarios::CASES {
                scenarios::run(case, &root).await?;
            }
            Ok(())
        })
    }) {
        Ok(Ok(())) => 0,
        Ok(Err(error)) => {
            eprintln!("B03 actual provider suite failed: {error}");
            2
        }
        Err(_) => 3,
    }
}

/// Decodes exact kind9 bytes and opens a real SQLx physical connection, reporting
/// the observed busy/FK/journal/synchronous PRAGMAs as a small fixture-only value.
/// # Safety
/// Inputs denote live borrowed ranges and `observed` points to a live writable u64.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_b03_probe_options(
    root: *const u8,
    root_length: u32,
    record: *const u8,
    record_length: u32,
    observed: *mut u64,
) -> u32 {
    if observed.is_null() || !observed.is_aligned() {
        return 1;
    }
    // SAFETY: forwarded test-only borrowed input range contract.
    let Some(root) = (unsafe { path(root, root_length) }) else {
        return 1;
    };
    // SAFETY: forwarded test-only borrowed input range contract.
    let Some(record) = (unsafe { input(record, record_length) }) else {
        return 1;
    };
    let Ok(options) = Options::from_record(record) else {
        return 1;
    };
    match std::panic::catch_unwind(|| -> scenarios::TestResult<u64> {
        runtime()?.block_on(probe(&root, options))
    }) {
        Ok(Ok(value)) => {
            // SAFETY: caller supplied a live, aligned writable u64 for this call.
            unsafe { observed.write(value) };
            0
        }
        Ok(Err(error)) => {
            eprintln!("B03 actual options probe failed: {error}");
            2
        }
        Err(_) => 3,
    }
}
async fn probe(path: &Path, options: Options) -> scenarios::TestResult<u64> {
    let limits = Limits {
        max_connections: 1,
        ..Limits::default()
    };
    let pool = limits
        .pool_options()?
        .connect_with(options.connect_options(path, limits)?)
        .await?;
    let mut connection = pool.acquire().await?;
    let busy: i64 = sqlx::query_scalar("PRAGMA busy_timeout")
        .fetch_one(&mut *connection)
        .await?;
    let foreign_keys: i64 = sqlx::query_scalar("PRAGMA foreign_keys")
        .fetch_one(&mut *connection)
        .await?;
    let synchronous: i64 = sqlx::query_scalar("PRAGMA synchronous")
        .fetch_one(&mut *connection)
        .await?;
    let journal: String = sqlx::query_scalar("PRAGMA journal_mode")
        .fetch_one(&mut *connection)
        .await?;
    let journal = match journal.as_str() {
        "wal" => 0,
        "delete" => 1,
        "memory" => 2,
        _ => return Err("unexpected SQLite journal".into()),
    };
    let version: String = sqlx::query_scalar("SELECT sqlite_version()")
        .fetch_one(&mut *connection)
        .await?;
    assert_eq!(version, nativeweave_data_sqlite::SQLITE_VERSION);
    connection.close().await?;
    pool.close().await;
    Ok(((busy as u64) << 32)
        | journal
        | ((synchronous as u64) << 8)
        | ((foreign_keys as u64) << 16))
}
