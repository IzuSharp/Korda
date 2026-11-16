use nativeweave_interop_errors::{EngineBoundary, Failure};

struct Dangerous;
impl Drop for Dangerous {
    fn drop(&mut self) {
        panic!("payload-drop");
    }
}

extern "C" fn illegal_escape() {
    panic!("ffi-unwind-must-abort");
}

fn main() {
    let mode = std::env::args().nth(1).unwrap_or_default();
    if mode == "hook" {
        std::panic::set_hook(Box::new(|_| panic!("panic-in-hook")));
    }
    match mode.as_str() {
        "drop" => {
            let payload =
                std::panic::catch_unwind(|| std::panic::panic_any(Dangerous)).unwrap_err();
            drop(payload);
        }
        "double" => {
            let _dangerous = Dangerous;
            panic!("unwind-with-panicking-drop");
        }
        "ffi" => illegal_escape(),
        "ordinary" | "quarantine" | "hook" | "abort" => {
            let boundary = EngineBoundary::new();
            let result = boundary.business(|| -> Result<(), Failure> {
                if mode == "quarantine" {
                    std::panic::panic_any(Dangerous);
                }
                panic!("isolated-product-boundary-panic");
            });
            println!(
                "status={} poisoned={} quarantined={} active={}",
                result.unwrap_err().status,
                boundary.is_poisoned(),
                boundary.quarantined_payloads(),
                boundary.active_calls()
            );
        }
        _ => std::process::exit(2),
    }
}
