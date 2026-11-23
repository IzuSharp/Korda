/* Independent C compiler layout of every A01 wire field; no copied byte offsets. */
#include "korda_abi_v1.h"
#include <stdalign.h>
#define L(T, F) offsetof(T, F)
uint64_t nw_a20_c_layout_v1(uint32_t index)
{
    const size_t values[] = {sizeof(nw_abi_info_v1),
                             alignof(nw_abi_info_v1),
                             L(nw_abi_info_v1, struct_size),
                             L(nw_abi_info_v1, abi_major),
                             L(nw_abi_info_v1, abi_minor),
                             L(nw_abi_info_v1, pointer_width_bits),
                             L(nw_abi_info_v1, capabilities),
                             L(nw_abi_info_v1, reserved),
                             sizeof(nw_error_info_v1),
                             alignof(nw_error_info_v1),
                             L(nw_error_info_v1, struct_size),
                             L(nw_error_info_v1, domain),
                             L(nw_error_info_v1, code),
                             L(nw_error_info_v1, flags),
                             L(nw_error_info_v1, correlation_id),
                             sizeof(nw_bytes_view_v1),
                             alignof(nw_bytes_view_v1),
                             L(nw_bytes_view_v1, data),
                             L(nw_bytes_view_v1, length),
                             sizeof(nw_operation_callbacks_v1),
                             alignof(nw_operation_callbacks_v1),
                             L(nw_operation_callbacks_v1, struct_size),
                             L(nw_operation_callbacks_v1, flags),
                             L(nw_operation_callbacks_v1, cookie),
                             L(nw_operation_callbacks_v1, on_terminal),
                             sizeof(nw_callbacks_v1),
                             alignof(nw_callbacks_v1),
                             L(nw_callbacks_v1, struct_size),
                             L(nw_callbacks_v1, flags),
                             L(nw_callbacks_v1, cookie),
                             L(nw_callbacks_v1, on_event)};
    return index < sizeof(values) / sizeof(values[0]) ? values[index] : UINT64_MAX;
}
