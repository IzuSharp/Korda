#include "nativeweave.generated.h"
#include <assert.h>
int main(void) {
    NwAbiInfoV1 abi = {0};
    assert(nw_syncproof_get_abi_v1(1, sizeof abi, &abi) == 0);
    assert(abi.capabilities == 1 && abi.abi_major == 1 && abi.reserved == 0);
    NwCodecLimitsV1 limits = {32, 0, 128, 256, 128};
    NwCodecOwner *owner = NULL;
    const uint8_t *data = NULL;
    uint64_t length = 0;
    NwErrorInfoV1 error = {24, 0, 0, 0, 0};
    const uint8_t input[] = "C generated boundary";
    assert(nw_syncproof_run_v1(input, sizeof input, 0, &limits, &owner, &data, &length, &error) == 0);
    assert(owner != NULL && data != NULL && length > 0);
    nw_syncproof_release_v1(owner);
    uint64_t live = 0, released = 0, capacity = 0, active = 0;
    assert(nw_syncproof_counters_v1(&live, &released, &capacity, &active) == 0);
    assert(live == 0 && released == 1 && capacity == 0 && active == 0);
    return 0;
}
