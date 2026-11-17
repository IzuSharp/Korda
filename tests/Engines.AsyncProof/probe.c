#include "nativeweave.generated.h"
#include <assert.h>
#include <string.h>
int main(void) {
    NwAbiInfoV1 abi = {0};
    assert(nw_asyncproof_get_abi_v1(1, sizeof abi, &abi) == 0);
    assert(abi.capabilities == 1 && abi.abi_major == 1 && abi.reserved == 0);
    memset(&abi, 0xa5, sizeof abi);
    assert(nw_asyncproof_get_abi_v1(99, sizeof abi, &abi) != 0);
    assert(abi.struct_size == 0xa5a5a5a5u);
    assert(nw_asyncproof_get_abi_v1(1, 4, &abi) != 0);
    NwHttpLimitsV1 limits = {24, 0, 1, 1, 4096, 4096};
    NwHttpHost *host = NULL;
    assert(nw_asyncproof_host_create_v1(&limits, &host) == 0 && host != NULL);
    uint32_t port = 0;
    assert(nw_asyncproof_host_port_v1(host, &port) == 0 && port != 0);
    nw_asyncproof_host_release_v1(host); /* never-started owner needs no runtime */
    uint64_t count = 1;
    assert(nw_asyncproof_counters_v1(0, &count) == 0 && count == 0);
    assert(nw_asyncproof_counters_v1(9, &count) == 0 && count == 0);
    return 0;
}
