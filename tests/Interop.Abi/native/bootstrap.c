#include "nativeweave_abi_v1.h"
#include <assert.h>
#include <stdio.h>
#include <string.h>

extern nw_status NW_CDECL nw_abi_conformance_get_abi_v1(uint32_t requested_major, uint32_t out_capacity,
                                                        nw_abi_info_v1* out_info);

static nw_status NW_CDECL terminal(uintptr_t cookie, uint32_t status, const nw_error_info_v1* error)
{
    assert(cookie == 7 && status == NW_CANCELED && error == NULL);
    return NW_OK;
}

static nw_status NW_CDECL event(uintptr_t cookie, uint32_t kind, const void* payload, uint64_t length)
{
    assert(cookie == 9 && kind == 1 && payload == NULL && length == 0);
    return NW_OK;
}

int main(void)
{
    const nw_status statuses[] = {NW_OK,
                                  NW_PENDING,
                                  NW_CANCELED,
                                  NW_INVALID_ARGUMENT,
                                  NW_INVALID_STATE,
                                  NW_OUT_OF_MEMORY,
                                  NW_NOT_SUPPORTED,
                                  NW_BUFFER_TOO_SMALL,
                                  NW_CALLBACK_FAILED,
                                  NW_NATIVE_PANIC,
                                  NW_ENGINE_POISONED,
                                  NW_CONTRACT_VIOLATION,
                                  NW_TIMEOUT,
                                  NW_DOMAIN_ERROR};
    for (uint32_t i = 0; i < 14; i++)
        assert(statuses[i] == i);
    nw_get_abi_v1_fn bootstrap = nw_abi_conformance_get_abi_v1;
    nw_operation_callbacks_v1 operation = {sizeof(operation), 0, 7, terminal};
    nw_callbacks_v1 subscription = {sizeof(subscription), 0, 9, event};
    assert(operation.on_terminal(operation.cookie, NW_CANCELED, NULL) == NW_OK);
    assert(subscription.on_event(subscription.cookie, 1, NULL, 0) == NW_OK);
    struct
    {
        nw_abi_info_v1 info;
        uint8_t tail[32];
    } output;
    memset(&output, 0xa5, sizeof(output));
    assert(bootstrap(1, 31, &output.info) == NW_BUFFER_TOO_SMALL);
    const uint8_t* bytes = (const uint8_t*)&output;
    for (size_t i = 0; i < sizeof(output); i++)
        assert(bytes[i] == 0xa5);
    assert(bootstrap(1, sizeof(output), &output.info) == NW_OK);
    assert(output.info.struct_size == 32 && output.info.abi_major == 1);
    assert(output.info.pointer_width_bits == 64 && output.info.reserved == 0);
    for (size_t i = 0; i < sizeof(output.tail); i++)
        assert(output.tail[i] == 0xa5);
    puts("{\"passed\":true,\"cdecl_bootstrap\":true,\"callback_types\":2,\"tail_untouched\":true}");
    return 0;
}
