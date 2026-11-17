#include "nativeweave.generated.h"
#include <assert.h>
#include <stdio.h>

static uint32_t notify(uintptr_t cookie, uint32_t status) { return cookie == 37 && status == 0 ? 0 : 8; }
int main(void) {
    NwAbiInfoV1 info = {0};
    assert(nw_exports_get_abi_v1(1, sizeof(info), &info) == 0);
    assert(info.struct_size == 32 && info.abi_major == 1 && info.capabilities == 1);
    NwFixtureOwner *owner = NULL;
    assert(nw_exports_create_v1(73, &owner) == 0 && owner != NULL);
    uint64_t value = 0;
    assert(nw_exports_read_v1(owner, &value) == 0 && value == 73);
    assert(nw_exports_start_v1(owner, (uintptr_t)&notify, 37, 0) == 0);
    assert(nw_exports_close_v1(owner) == 0 && nw_exports_drain_v1(owner) == 0);
    nw_exports_release_v1(owner);
    uintptr_t live = 99, released = 0;
    assert(nw_exports_counters_v1(&live, &released) == 0 && live == 0 && released == 1);
    assert(nw_exports_notification_v1(37, 0) == 0);
    assert(nw_exports_invoke_v1((uintptr_t)&notify, 37, 0) == 0);
    assert(nw_exports_invoke_v1((uintptr_t)&notify, 38, 0) == 8);
    puts("PASS A11 C header layout and all ten actual export signatures/semantics.");
    return 0;
}
