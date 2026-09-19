// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 mp0rta and mqvpn contributors

#ifdef _WIN32

#  include "platform_internal_win.h"

#  include <stdint.h>
#  include <stdio.h>
#  include <string.h>
#  include <wchar.h>

static DWORD WINAPI test_filter_delete(HANDLE engine, UINT64 id);
static DWORD WINAPI test_sublayer_delete(HANDLE engine, const GUID *key);
static DWORD WINAPI test_engine_close(HANDLE engine);
static DWORD WINAPI test_filter_add(HANDLE engine, const FWPM_FILTER0 *filter,
                                    PSECURITY_DESCRIPTOR sd, UINT64 *id);
static DWORD WINAPI test_get_app_id(PCWSTR path, FWP_BYTE_BLOB **app_id);
static void WINAPI test_free_memory(void **p);
static DWORD WINAPI test_get_module_filename(HMODULE module, LPWSTR path, DWORD cap);

/* Keep setup linked to the real Windows SDK, but make cleanup observable. */
#  define FwpmFilterDeleteById0     test_filter_delete
#  define FwpmSubLayerDeleteByKey0  test_sublayer_delete
#  define FwpmEngineClose0          test_engine_close
#  define FwpmFilterAdd0            test_filter_add
#  define FwpmGetAppIdFromFileName0 test_get_app_id
#  define FwpmFreeMemory0           test_free_memory
#  define GetModuleFileNameW        test_get_module_filename
#  include "../src/platform/windows/firewall.c"
#  undef FwpmFilterDeleteById0
#  undef FwpmSubLayerDeleteByKey0
#  undef FwpmEngineClose0
#  undef FwpmFilterAdd0
#  undef FwpmGetAppIdFromFileName0
#  undef FwpmFreeMemory0
#  undef GetModuleFileNameW

#  define ASSERT_TRUE(expr, msg)                                        \
      do {                                                              \
          if (!(expr)) {                                                \
              fprintf(stderr, "FAIL: %s (line %d)\n", (msg), __LINE__); \
              return 1;                                                 \
          }                                                             \
      } while (0)

enum {
    CALL_SUBLAYER = -1,
    CALL_CLOSE = -2,
};

static long long g_calls[32];
static int g_n_calls;
static UINT64 g_fail_filter;
static DWORD g_filter_error;
static DWORD g_sublayer_error;
static DWORD g_close_error;
static int g_add_calls;
static int g_server_filter_valid;
static int g_block_filters_valid;
static int g_app_id_freed;
static UINT8 g_app_id_bytes[] = {0x6d, 0x71, 0x76, 0x70, 0x6e};
static FWP_BYTE_BLOB g_app_id = {
    sizeof(g_app_id_bytes),
    g_app_id_bytes,
};

static void
fake_reset(void)
{
    memset(g_calls, 0, sizeof(g_calls));
    g_n_calls = 0;
    g_fail_filter = 0;
    g_filter_error = ERROR_SUCCESS;
    g_sublayer_error = ERROR_SUCCESS;
    g_close_error = ERROR_SUCCESS;
    g_add_calls = 0;
    g_server_filter_valid = 0;
    g_block_filters_valid = 1;
    g_app_id_freed = 0;
}

static DWORD WINAPI
test_filter_delete(HANDLE engine, UINT64 id)
{
    (void)engine;
    g_calls[g_n_calls++] = (long long)id;
    return id == g_fail_filter ? g_filter_error : ERROR_SUCCESS;
}

static DWORD WINAPI
test_sublayer_delete(HANDLE engine, const GUID *key)
{
    (void)engine;
    (void)key;
    g_calls[g_n_calls++] = CALL_SUBLAYER;
    return g_sublayer_error;
}

static DWORD WINAPI
test_engine_close(HANDLE engine)
{
    (void)engine;
    g_calls[g_n_calls++] = CALL_CLOSE;
    return g_close_error;
}

static DWORD WINAPI
test_filter_add(HANDLE engine, const FWPM_FILTER0 *filter, PSECURITY_DESCRIPTOR sd,
                UINT64 *id)
{
    (void)engine;
    (void)sd;
    g_add_calls++;
    *id = (UINT64)(100 + g_add_calls);

    if (filter->action.type == FWP_ACTION_BLOCK) {
        if (filter->numFilterConditions != 0) g_block_filters_valid = 0;
        return ERROR_SUCCESS;
    }

    if (filter->numFilterConditions == 4) {
        const FWPM_FILTER_CONDITION0 *c = filter->filterCondition;
        g_server_filter_valid =
            IsEqualGUID(&c[0].fieldKey, &FWPM_CONDITION_IP_REMOTE_ADDRESS) &&
            c[0].matchType == FWP_MATCH_EQUAL && c[0].conditionValue.type == FWP_UINT32 &&
            c[0].conditionValue.uint32 == 0xcb007109 &&
            IsEqualGUID(&c[1].fieldKey, &FWPM_CONDITION_IP_REMOTE_PORT) &&
            c[1].conditionValue.type == FWP_UINT16 && c[1].conditionValue.uint16 == 443 &&
            IsEqualGUID(&c[2].fieldKey, &FWPM_CONDITION_IP_PROTOCOL) &&
            c[2].conditionValue.type == FWP_UINT8 &&
            c[2].conditionValue.uint8 == IPPROTO_UDP &&
            IsEqualGUID(&c[3].fieldKey, &FWPM_CONDITION_ALE_APP_ID) &&
            c[3].conditionValue.type == FWP_BYTE_BLOB_TYPE &&
            c[3].conditionValue.byteBlob == &g_app_id;
    }
    return ERROR_SUCCESS;
}

static DWORD WINAPI
test_get_app_id(PCWSTR path, FWP_BYTE_BLOB **app_id)
{
    if (wcscmp(path, L"C:\\mqvpn.exe") != 0) return ERROR_FILE_NOT_FOUND;
    *app_id = &g_app_id;
    return ERROR_SUCCESS;
}

static void WINAPI
test_free_memory(void **p)
{
    if (*p == &g_app_id) g_app_id_freed++;
    *p = NULL;
}

static DWORD WINAPI
test_get_module_filename(HMODULE module, LPWSTR path, DWORD cap)
{
    (void)module;
    const wchar_t exe[] = L"C:\\mqvpn.exe";
    if (cap < sizeof(exe) / sizeof(exe[0])) return cap;
    memcpy(path, exe, sizeof(exe));
    return (DWORD)(sizeof(exe) / sizeof(exe[0]) - 1);
}

static platform_win_ctx_t
active_context(void)
{
    platform_win_ctx_t p;
    memset(&p, 0, sizeof(p));
    p.wfp_engine = (HANDLE)(uintptr_t)0x1234;
    p.killswitch_active = 1;
    p.wfp_filter_ids[0] = 11;
    p.wfp_filter_ids[1] = 22;
    p.wfp_filter_ids[2] = 33;
    p.n_wfp_filters = 3;
    return p;
}

static int
test_dynamic_session(void)
{
    FWPM_SESSION0 session;
    memset(&session, 0xa5, sizeof(session));
    wfp_init_dynamic_session(&session);
    ASSERT_TRUE(session.flags == FWPM_SESSION_FLAG_DYNAMIC,
                "WFP session must be dynamic");
    ASSERT_TRUE(session.displayData.name != NULL, "dynamic session has a name");
    return 0;
}

static int
test_server_exception_is_process_udp_endpoint_scoped(void)
{
    fake_reset();
    platform_win_ctx_t p;
    memset(&p, 0, sizeof(p));
    p.wfp_engine = (HANDLE)(uintptr_t)0x1234;
    p.server_addr.ss_family = AF_INET;
    ((struct sockaddr_in *)&p.server_addr)->sin_addr.s_addr = htonl(0xcb007109);
    p.server_port = 443;

    ASSERT_TRUE(wfp_add_server_permit(&p) == 0, "server exception is added");
    ASSERT_TRUE(g_add_calls == 1 && p.n_wfp_filters == 1,
                "server exception creates exactly one tracked filter");
    ASSERT_TRUE(g_server_filter_valid,
                "server exception matches address, port, UDP, and mqvpn app ID");
    ASSERT_TRUE(g_app_id_freed == 1, "temporary app ID is released after filter add");
    return 0;
}

static int
test_interface_bound_traffic_reaches_unconditional_block(void)
{
    fake_reset();
    platform_win_ctx_t p;
    memset(&p, 0, sizeof(p));
    p.wfp_engine = (HANDLE)(uintptr_t)0x1234;

    ASSERT_TRUE(wfp_add_block_all(&p) == 0, "IPv4 and IPv6 block filters are added");
    ASSERT_TRUE(g_add_calls == 2 && p.n_wfp_filters == 2,
                "both block filters are tracked");
    ASSERT_TRUE(g_block_filters_valid,
                "block filters have no route or interface condition to bypass");
    return 0;
}

static int
test_cleanup_order(void)
{
    fake_reset();
    platform_win_ctx_t p = active_context();
    ASSERT_TRUE(win_cleanup_killswitch(&p) == 0, "cleanup succeeds");
    ASSERT_TRUE(g_n_calls == 5, "three filters, sublayer, and engine are handled");
    ASSERT_TRUE(g_calls[0] == 33 && g_calls[1] == 22 && g_calls[2] == 11,
                "filters are deleted in reverse creation order");
    ASSERT_TRUE(g_calls[3] == CALL_SUBLAYER && g_calls[4] == CALL_CLOSE,
                "sublayer is deleted before engine close");
    ASSERT_TRUE(!p.killswitch_active && !p.wfp_engine && p.n_wfp_filters == 0,
                "successful cleanup clears bookkeeping");
    return 0;
}

static int
test_not_found_is_idempotent(void)
{
    fake_reset();
    platform_win_ctx_t p = active_context();
    g_fail_filter = 22;
    g_filter_error = FWP_E_FILTER_NOT_FOUND;
    g_sublayer_error = FWP_E_SUBLAYER_NOT_FOUND;
    ASSERT_TRUE(win_cleanup_killswitch(&p) == 0, "missing WFP objects are already clean");
    ASSERT_TRUE(!p.wfp_engine && !p.killswitch_active,
                "idempotent cleanup clears bookkeeping");
    return 0;
}

static int
test_delete_failure_still_closes_dynamic_session(void)
{
    fake_reset();
    platform_win_ctx_t p = active_context();
    g_fail_filter = 22;
    g_filter_error = ERROR_ACCESS_DENIED;
    ASSERT_TRUE(win_cleanup_killswitch(&p) < 0, "filter deletion failure is reported");
    ASSERT_TRUE(g_n_calls == 5, "cleanup continues after one filter failure");
    ASSERT_TRUE(g_calls[2] == 11 && g_calls[3] == CALL_SUBLAYER &&
                    g_calls[4] == CALL_CLOSE,
                "remaining objects and dynamic session are still cleaned");
    ASSERT_TRUE(!p.wfp_engine && !p.killswitch_active,
                "successful engine close releases the dynamic session");
    return 0;
}

static int
test_close_failure_preserves_retry_state(void)
{
    fake_reset();
    platform_win_ctx_t p = active_context();
    g_close_error = ERROR_ACCESS_DENIED;
    ASSERT_TRUE(win_cleanup_killswitch(&p) < 0, "engine close failure is reported");
    ASSERT_TRUE(p.wfp_engine != NULL && p.killswitch_active && p.n_wfp_filters == 3,
                "failed close retains state for a later retry");
    return 0;
}

int
main(void)
{
    if (test_dynamic_session() ||
        test_server_exception_is_process_udp_endpoint_scoped() ||
        test_interface_bound_traffic_reaches_unconditional_block() ||
        test_cleanup_order() || test_not_found_is_idempotent() ||
        test_delete_failure_still_closes_dynamic_session() ||
        test_close_failure_preserves_retry_state())
        return 1;
    puts("test_windows_firewall: OK");
    return 0;
}

#endif /* _WIN32 */
