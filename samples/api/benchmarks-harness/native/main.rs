//! Line-protocol target used only to verify the A21 controller.
mod operation;

use std::io::{self, BufRead, Write};

fn counters() -> String {
    let bytes = [
        "managed_allocated_bytes",
        "native_allocated_bytes",
        "copied_bytes",
        "pinned_bytes",
        "retained_bytes",
        "byte_credits",
        "gc_heap_live_bytes",
        "gc_heap_committed_bytes",
        "native_allocator_live_bytes",
        "native_allocator_retained_bytes",
    ];
    let counts = [
        "native_allocations",
        "ffi_calls",
        "live_owners",
        "live_permits",
        "callback_roots",
        "operations",
        "queue_slots",
        "connections",
        "runtime_instances",
    ];
    let mut fields = Vec::new();
    for (names, unit) in [(bytes.as_slice(), "bytes"), (counts.as_slice(), "count")] {
        for name in names {
            if *name == "ffi_calls" {
                fields.push(format!("\"{name}\":{{\"value\":0,\"unit\":\"{unit}\",\"availability\":\"derived\",\"method\":\"Direct scalar call, no foreign boundary\"}}"));
            } else {
                fields.push(format!("\"{name}\":{{\"value\":null,\"unit\":\"{unit}\",\"availability\":\"unknown\",\"method\":\"Scalar fixture has no domain instrumentation for this counter\"}}"));
            }
        }
    }
    format!("{{{}}}", fields.join(","))
}

fn main() -> io::Result<()> {
    let mut output = io::BufWriter::new(io::stdout().lock());
    writeln!(
        output,
        "{{\"kind\":\"hello\",\"route\":\"direct-rust\",\"dynamic_code_supported\":false}}"
    )?;
    output.flush()?;
    let counters = counters();
    for line in io::stdin().lock().lines() {
        let line = line?;
        let fields: Vec<_> = line.split('\t').collect();
        if fields.len() != 3 {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "Expected id/value/mode",
            ));
        }
        let id: u64 = fields[0]
            .parse()
            .map_err(|_| io::Error::new(io::ErrorKind::InvalidInput, "id"))?;
        let value: u64 = fields[1]
            .parse()
            .map_err(|_| io::Error::new(io::ErrorKind::InvalidInput, "value"))?;
        writeln!(output, "{{\"kind\":\"admitted\",\"id\":{id}}}")?;
        output.flush()?;
        let status = match fields[2] {
            "ok" => "ok",
            "rejected" => "rejected",
            "failed" => "failed",
            "cancelled" => "cancelled",
            "timeout" => continue,
            _ => "unsupported",
        };
        let result = operation::mix(value);
        writeln!(
            output,
            "{{\"kind\":\"completed\",\"id\":{id},\"status\":\"{status}\",\"value\":{result},\"counters\":{counters}}}"
        )?;
        output.flush()?;
    }
    Ok(())
}
