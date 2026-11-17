//! A deterministic cancellation fixture; it adds no production build hook.
fn main() {
    println!("cargo:rerun-if-changed=wait-for-cancellation");
    if std::path::Path::new("wait-for-cancellation").exists() {
        std::fs::write("cancellation-build-started", b"started").unwrap();
        std::thread::sleep(std::time::Duration::from_secs(45));
        std::fs::write("cancellation-build-completed", b"unexpected").unwrap();
    }
}
