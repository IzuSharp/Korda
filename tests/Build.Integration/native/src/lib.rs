//! Build-pipeline conformance asset; not the Engine ABI or a published package.

/// Identifies the fixture profile without transferring resources.
#[unsafe(no_mangle)]
pub extern "C" fn nw_build_version() -> u32 {
    1
}

/// A real native computation used to detect stale or substituted artifacts.
#[unsafe(no_mangle)]
pub extern "C" fn nw_build_value(value: u64) -> u64 {
    value.wrapping_add(37)
}
