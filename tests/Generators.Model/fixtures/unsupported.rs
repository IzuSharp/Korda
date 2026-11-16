// Parser-only negative input. This function must remain in the expected inventory,
// even if a successful raw-binding extraction silently omits its tuple parameter.
#[unsafe(no_mangle)]
pub extern "C" fn unsupported_tuple(value: (u32, u32)) -> u32 { value.0 }
