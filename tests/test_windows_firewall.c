// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 mp0rta and mqvpn contributors

#ifdef _WIN32

#  include "platform_internal_win.h"

#  include <stdint.h>
#  include <stdio.h>
#  include <string.h>

static DWORD WINAPI test_filter_delete(HANDLE engine, UINT64 id);
static DWORD WINAPI test_sublayer_delete(HANDLE engine, const GUID *key);
static DWORD WINAPI test_engine_close(HANDLE engine);

/* Keep setup linked to the real Windows SDK, but make cleanup observable. */
#  define FwpmFilterDeleteById0    test_filter_delete
#  define FwpmSubLayerDeleteByKey0 test_sublayer_delete
#  define FwpmEngineClose0         test_engine_close
#  include "../src/platform/windows/firewall.c"
#  undef FwpmFilterDeleteById0
#  undef FwpmSubLayerDeleteByKey0
#  undef FwpmEngineClose0

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

static void
fake_reset(void)
{
    memset(g_calls, 0, sizeof(g_calls));
    g_n_calls = 0;
    g_fail_filter = 0;
    g_filter_error = ERROR_SUCCESS;
    g_sublayer_error = ERROR_SUCCESS;
    g_close_error = ERROR_SUCCESS;
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
    if (test_dynamic_session() || test_cleanup_order() ||
        test_not_found_is_idempotent() ||
        test_delete_failure_still_closes_dynamic_session() ||
        test_close_failure_preserves_retry_state())
        return 1;
    puts("test_windows_firewall: OK");
    return 0;
}

#endif /* _WIN32 */
