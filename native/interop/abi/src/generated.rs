// Generated from abi-v1.json; SHA-256 62171763fb2d1b1a8350d2f381a85910c0368515923706923d29f4ff69681dab. Do not edit.
pub type NwStatus = u32;
pub const NW_OK: NwStatus = 0;
pub const NW_PENDING: NwStatus = 1;
pub const NW_CANCELED: NwStatus = 2;
pub const NW_INVALID_ARGUMENT: NwStatus = 3;
pub const NW_INVALID_STATE: NwStatus = 4;
pub const NW_OUT_OF_MEMORY: NwStatus = 5;
pub const NW_NOT_SUPPORTED: NwStatus = 6;
pub const NW_BUFFER_TOO_SMALL: NwStatus = 7;
pub const NW_CALLBACK_FAILED: NwStatus = 8;
pub const NW_NATIVE_PANIC: NwStatus = 9;
pub const NW_ENGINE_POISONED: NwStatus = 10;
pub const NW_CONTRACT_VIOLATION: NwStatus = 11;
pub const NW_TIMEOUT: NwStatus = 12;
pub const NW_DOMAIN_ERROR: NwStatus = 13;
#[rustfmt::skip]
pub type NwOnTerminalFn = unsafe extern "C" fn(cookie: usize, terminal_status: u32, borrowed_error: *const NwErrorInfoV1) -> NwStatus;
#[rustfmt::skip]
pub type NwOnEventFn = unsafe extern "C" fn(cookie: usize, event_kind: u32, payload: *const core::ffi::c_void, payload_bytes: u64) -> NwStatus;
#[repr(C)]
#[derive(Clone, Copy)]
pub struct NwAbiInfoV1 {
    pub struct_size: u32,
    pub abi_major: u32,
    pub abi_minor: u32,
    pub pointer_width_bits: u32,
    pub capabilities: u64,
    pub reserved: u64,
}
impl NwAbiInfoV1 {
    /// Checks only defined reserved fields; it does not validate pointer provenance.
    pub const fn reserved_are_zero(&self) -> bool {
        self.reserved == 0
    }
}
#[repr(C)]
#[derive(Clone, Copy)]
pub struct NwErrorInfoV1 {
    pub struct_size: u32,
    pub domain: u32,
    pub code: i32,
    pub flags: u32,
    pub correlation_id: u64,
}
impl NwErrorInfoV1 {
    /// Checks only defined reserved fields; it does not validate pointer provenance.
    pub const fn reserved_are_zero(&self) -> bool {
        self.flags == 0
    }
}
#[repr(C)]
#[derive(Clone, Copy)]
pub struct NwBytesViewV1 {
    pub data: *const u8,
    pub length: u64,
}
#[repr(C)]
#[derive(Clone, Copy)]
pub struct NwOperationCallbacksV1 {
    pub struct_size: u32,
    pub flags: u32,
    pub cookie: usize,
    pub on_terminal: Option<NwOnTerminalFn>,
}
impl NwOperationCallbacksV1 {
    /// Checks only defined reserved fields; it does not validate pointer provenance.
    pub const fn reserved_are_zero(&self) -> bool {
        self.flags == 0
    }
}
#[repr(C)]
#[derive(Clone, Copy)]
pub struct NwCallbacksV1 {
    pub struct_size: u32,
    pub flags: u32,
    pub cookie: usize,
    pub on_event: Option<NwOnEventFn>,
}
impl NwCallbacksV1 {
    /// Checks only defined reserved fields; it does not validate pointer provenance.
    pub const fn reserved_are_zero(&self) -> bool {
        self.flags == 0
    }
}
