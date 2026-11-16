#[path = "support/scenarios.rs"]
mod scenarios;

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn actual_sqlite_provider_and_transaction_composition() -> scenarios::TestResult {
    let root = std::env::temp_dir().join(format!(
        "nativeweave-b03-{}-{}",
        std::process::id(),
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)?
            .as_nanos()
    ));
    for case in 1..=12 {
        scenarios::run(case, &root).await?;
    }
    std::fs::remove_dir_all(root)?;
    Ok(())
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn last_parent_retirement_joins_actual_worker() -> scenarios::TestResult {
    let root = std::env::temp_dir().join(format!(
        "nativeweave-b03-last-parent-{}-{}",
        std::process::id(),
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)?
            .as_nanos()
    ));
    scenarios::run(13, &root).await?;
    std::fs::remove_dir_all(root)?;
    Ok(())
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn row_handoff_retains_finite_provider_budget() -> scenarios::TestResult {
    let root = std::env::temp_dir().join(format!(
        "nativeweave-b03-row-handoff-{}-{}",
        std::process::id(),
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)?
            .as_nanos()
    ));
    scenarios::run(scenarios::CASES, &root).await?;
    std::fs::remove_dir_all(root)?;
    Ok(())
}
