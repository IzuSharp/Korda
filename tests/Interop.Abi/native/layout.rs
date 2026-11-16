// Generated from abi-v1.json; SHA-256 62171763fb2d1b1a8350d2f381a85910c0368515923706923d29f4ff69681dab. Do not edit.
use nativeweave_interop_abi::*;
fn main() {
    print!("{{\"pointer_width_bits\":{},\"records\":{{", usize::BITS);
    print!("\"nw_abi_info_v1\":{{\"size\":{},\"align\":{},\"fields\":{{", size_of::<NwAbiInfoV1>(), align_of::<NwAbiInfoV1>());
    print!("\"struct_size\":{{\"offset\":{},\"width\":{}}}", core::mem::offset_of!(NwAbiInfoV1, struct_size), size_of::<u32>());
    print!(",\"abi_major\":{{\"offset\":{},\"width\":{}}}", core::mem::offset_of!(NwAbiInfoV1, abi_major), size_of::<u32>());
    print!(",\"abi_minor\":{{\"offset\":{},\"width\":{}}}", core::mem::offset_of!(NwAbiInfoV1, abi_minor), size_of::<u32>());
    print!(",\"pointer_width_bits\":{{\"offset\":{},\"width\":{}}}", core::mem::offset_of!(NwAbiInfoV1, pointer_width_bits), size_of::<u32>());
    print!(",\"capabilities\":{{\"offset\":{},\"width\":{}}}", core::mem::offset_of!(NwAbiInfoV1, capabilities), size_of::<u64>());
    print!(",\"reserved\":{{\"offset\":{},\"width\":{}}}", core::mem::offset_of!(NwAbiInfoV1, reserved), size_of::<u64>());
    print!("}}}}");
    print!(",\"nw_error_info_v1\":{{\"size\":{},\"align\":{},\"fields\":{{", size_of::<NwErrorInfoV1>(), align_of::<NwErrorInfoV1>());
    print!("\"struct_size\":{{\"offset\":{},\"width\":{}}}", core::mem::offset_of!(NwErrorInfoV1, struct_size), size_of::<u32>());
    print!(",\"domain\":{{\"offset\":{},\"width\":{}}}", core::mem::offset_of!(NwErrorInfoV1, domain), size_of::<u32>());
    print!(",\"code\":{{\"offset\":{},\"width\":{}}}", core::mem::offset_of!(NwErrorInfoV1, code), size_of::<i32>());
    print!(",\"flags\":{{\"offset\":{},\"width\":{}}}", core::mem::offset_of!(NwErrorInfoV1, flags), size_of::<u32>());
    print!(",\"correlation_id\":{{\"offset\":{},\"width\":{}}}", core::mem::offset_of!(NwErrorInfoV1, correlation_id), size_of::<u64>());
    print!("}}}}");
    print!(",\"nw_bytes_view_v1\":{{\"size\":{},\"align\":{},\"fields\":{{", size_of::<NwBytesViewV1>(), align_of::<NwBytesViewV1>());
    print!("\"data\":{{\"offset\":{},\"width\":{}}}", core::mem::offset_of!(NwBytesViewV1, data), size_of::<*const u8>());
    print!(",\"length\":{{\"offset\":{},\"width\":{}}}", core::mem::offset_of!(NwBytesViewV1, length), size_of::<u64>());
    print!("}}}}");
    print!(",\"nw_operation_callbacks_v1\":{{\"size\":{},\"align\":{},\"fields\":{{", size_of::<NwOperationCallbacksV1>(), align_of::<NwOperationCallbacksV1>());
    print!("\"struct_size\":{{\"offset\":{},\"width\":{}}}", core::mem::offset_of!(NwOperationCallbacksV1, struct_size), size_of::<u32>());
    print!(",\"flags\":{{\"offset\":{},\"width\":{}}}", core::mem::offset_of!(NwOperationCallbacksV1, flags), size_of::<u32>());
    print!(",\"cookie\":{{\"offset\":{},\"width\":{}}}", core::mem::offset_of!(NwOperationCallbacksV1, cookie), size_of::<usize>());
    print!(",\"on_terminal\":{{\"offset\":{},\"width\":{}}}", core::mem::offset_of!(NwOperationCallbacksV1, on_terminal), size_of::<Option<NwOnTerminalFn>>());
    print!("}}}}");
    print!(",\"nw_callbacks_v1\":{{\"size\":{},\"align\":{},\"fields\":{{", size_of::<NwCallbacksV1>(), align_of::<NwCallbacksV1>());
    print!("\"struct_size\":{{\"offset\":{},\"width\":{}}}", core::mem::offset_of!(NwCallbacksV1, struct_size), size_of::<u32>());
    print!(",\"flags\":{{\"offset\":{},\"width\":{}}}", core::mem::offset_of!(NwCallbacksV1, flags), size_of::<u32>());
    print!(",\"cookie\":{{\"offset\":{},\"width\":{}}}", core::mem::offset_of!(NwCallbacksV1, cookie), size_of::<usize>());
    print!(",\"on_event\":{{\"offset\":{},\"width\":{}}}", core::mem::offset_of!(NwCallbacksV1, on_event), size_of::<Option<NwOnEventFn>>());
    print!("}}}}");
    println!("}}}}");
}
