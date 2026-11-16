//! Test-only ABI fixture. The runtime belongs to one invocation and is joined
//! after the actual product provider's owners/pools retire. No fixture export is
//! advertised by the production component Engine descriptor.
mod oracles;
mod proxy;

/// Execute the exact native product-provider oracles from a JIT or AOT process.
/// Configuration/report paths are environment-selected test inputs only.
#[unsafe(no_mangle)]
pub extern "C" fn nw_postgres_fixture_run_v1() -> u32 {
    match std::panic::catch_unwind(|| {
        let runtime = tokio::runtime::Builder::new_multi_thread()
            .worker_threads(2)
            .enable_all()
            .build()
            .map_err(|e| e.to_string())?;
        let result = runtime.block_on(oracles::run()).map_err(|e| e.to_string());
        runtime.shutdown_timeout(std::time::Duration::from_secs(10));
        result
    }) {
        Ok(Ok(())) => 0,
        Ok(Err(error)) => {
            eprintln!("B02 fixture failure: {error}");
            1
        }
        Err(_) => {
            eprintln!("B02 fixture panic");
            2
        }
    }
}
/// Test-only bounded option-record crossing. No pointer survives this call.
/// # Safety
/// `record` denotes exactly `length` live readable bytes; it is never retained.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nw_postgres_fixture_options_v1(record: *const u8, length: u32) -> u32 {
    if record.is_null() || length > 8192 {
        return 1;
    }
    // SAFETY: the fixture caller supplies the exact readable range stated above.
    let bytes = unsafe { std::slice::from_raw_parts(record, length as usize) };
    let Ok(url) = std::env::var("NW_PG_URL") else {
        return 2;
    };
    match nativeweave_data_postgres::ConnectConfig::from_record(url, bytes) {
        Ok(_) => 0,
        Err(_) => 3,
    }
}

#[cfg(test)]
mod tests {
    #[test]
    fn actual_postgres_oracles() {
        assert_eq!(super::nw_postgres_fixture_run_v1(), 0);
    }
}
