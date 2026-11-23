/* Generated from abi-v1.json; SHA-256 62171763fb2d1b1a8350d2f381a85910c0368515923706923d29f4ff69681dab. Do not edit. */
#include "korda_abi_v1.h"
#include <stdio.h>
#include <stdalign.h>
int main(void) {
    printf("{\"pointer_width_bits\":%zu,\"records\":{", sizeof(void*) * 8);
    printf("\"nw_abi_info_v1\":{\"size\":%zu,\"align\":%zu,\"fields\":{", sizeof(nw_abi_info_v1), alignof(nw_abi_info_v1));
    printf("\"struct_size\":{\"offset\":%zu,\"width\":%zu}", offsetof(nw_abi_info_v1, struct_size), sizeof(((nw_abi_info_v1*)0)->struct_size));
    printf(",\"abi_major\":{\"offset\":%zu,\"width\":%zu}", offsetof(nw_abi_info_v1, abi_major), sizeof(((nw_abi_info_v1*)0)->abi_major));
    printf(",\"abi_minor\":{\"offset\":%zu,\"width\":%zu}", offsetof(nw_abi_info_v1, abi_minor), sizeof(((nw_abi_info_v1*)0)->abi_minor));
    printf(",\"pointer_width_bits\":{\"offset\":%zu,\"width\":%zu}", offsetof(nw_abi_info_v1, pointer_width_bits), sizeof(((nw_abi_info_v1*)0)->pointer_width_bits));
    printf(",\"capabilities\":{\"offset\":%zu,\"width\":%zu}", offsetof(nw_abi_info_v1, capabilities), sizeof(((nw_abi_info_v1*)0)->capabilities));
    printf(",\"reserved\":{\"offset\":%zu,\"width\":%zu}", offsetof(nw_abi_info_v1, reserved), sizeof(((nw_abi_info_v1*)0)->reserved));
    printf("}}");
    printf(",\"nw_error_info_v1\":{\"size\":%zu,\"align\":%zu,\"fields\":{", sizeof(nw_error_info_v1), alignof(nw_error_info_v1));
    printf("\"struct_size\":{\"offset\":%zu,\"width\":%zu}", offsetof(nw_error_info_v1, struct_size), sizeof(((nw_error_info_v1*)0)->struct_size));
    printf(",\"domain\":{\"offset\":%zu,\"width\":%zu}", offsetof(nw_error_info_v1, domain), sizeof(((nw_error_info_v1*)0)->domain));
    printf(",\"code\":{\"offset\":%zu,\"width\":%zu}", offsetof(nw_error_info_v1, code), sizeof(((nw_error_info_v1*)0)->code));
    printf(",\"flags\":{\"offset\":%zu,\"width\":%zu}", offsetof(nw_error_info_v1, flags), sizeof(((nw_error_info_v1*)0)->flags));
    printf(",\"correlation_id\":{\"offset\":%zu,\"width\":%zu}", offsetof(nw_error_info_v1, correlation_id), sizeof(((nw_error_info_v1*)0)->correlation_id));
    printf("}}");
    printf(",\"nw_bytes_view_v1\":{\"size\":%zu,\"align\":%zu,\"fields\":{", sizeof(nw_bytes_view_v1), alignof(nw_bytes_view_v1));
    printf("\"data\":{\"offset\":%zu,\"width\":%zu}", offsetof(nw_bytes_view_v1, data), sizeof(((nw_bytes_view_v1*)0)->data));
    printf(",\"length\":{\"offset\":%zu,\"width\":%zu}", offsetof(nw_bytes_view_v1, length), sizeof(((nw_bytes_view_v1*)0)->length));
    printf("}}");
    printf(",\"nw_operation_callbacks_v1\":{\"size\":%zu,\"align\":%zu,\"fields\":{", sizeof(nw_operation_callbacks_v1), alignof(nw_operation_callbacks_v1));
    printf("\"struct_size\":{\"offset\":%zu,\"width\":%zu}", offsetof(nw_operation_callbacks_v1, struct_size), sizeof(((nw_operation_callbacks_v1*)0)->struct_size));
    printf(",\"flags\":{\"offset\":%zu,\"width\":%zu}", offsetof(nw_operation_callbacks_v1, flags), sizeof(((nw_operation_callbacks_v1*)0)->flags));
    printf(",\"cookie\":{\"offset\":%zu,\"width\":%zu}", offsetof(nw_operation_callbacks_v1, cookie), sizeof(((nw_operation_callbacks_v1*)0)->cookie));
    printf(",\"on_terminal\":{\"offset\":%zu,\"width\":%zu}", offsetof(nw_operation_callbacks_v1, on_terminal), sizeof(((nw_operation_callbacks_v1*)0)->on_terminal));
    printf("}}");
    printf(",\"nw_callbacks_v1\":{\"size\":%zu,\"align\":%zu,\"fields\":{", sizeof(nw_callbacks_v1), alignof(nw_callbacks_v1));
    printf("\"struct_size\":{\"offset\":%zu,\"width\":%zu}", offsetof(nw_callbacks_v1, struct_size), sizeof(((nw_callbacks_v1*)0)->struct_size));
    printf(",\"flags\":{\"offset\":%zu,\"width\":%zu}", offsetof(nw_callbacks_v1, flags), sizeof(((nw_callbacks_v1*)0)->flags));
    printf(",\"cookie\":{\"offset\":%zu,\"width\":%zu}", offsetof(nw_callbacks_v1, cookie), sizeof(((nw_callbacks_v1*)0)->cookie));
    printf(",\"on_event\":{\"offset\":%zu,\"width\":%zu}", offsetof(nw_callbacks_v1, on_event), sizeof(((nw_callbacks_v1*)0)->on_event));
    printf("}}");
    puts("}}");
    return 0;
}
