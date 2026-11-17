fn main() {
    let route = std::env::args()
        .nth(1)
        .unwrap_or_else(|| "7".into())
        .parse()
        .expect("u32 route");
    let (status, bytes, chunks) = nativeweave_engine_asyncproof::oracle::run(route);
    let hex: String = bytes.iter().map(|byte| format!("{byte:02x}")).collect();
    let retained = chunks != 0;
    println!(
        "{{\"route\":{route},\"status\":{status},\"body_hex\":\"{hex}\",\"chunks\":{chunks},\"retained_after_host_close\":{retained},\"live_resources\":0}}"
    );
}
