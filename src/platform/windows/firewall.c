// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 mp0rta and mqvpn contributors

/*
 * firewall.c — WFP-based kill switch for Windows
 *
 * Uses the Windows Filtering Platform (WFP) to block all outbound traffic
 * except:
 *   - Loopback
 *   - Traffic to the VPN server (UDP on original interface)
 *   - Traffic on the TUN (Wintun) interface
 *
 * All filters are added under a single dynamic WFP session and sublayer.
 * Closing the engine is the crash-safety backstop; normal cleanup still
 * deletes filters explicitly before deleting their referenced sublayer.
 */

#ifdef _WIN32

#  include "platform_internal_win.h"
#  include "log.h"

#  include <stdlib.h>
#  include <stdio.h>
#  include <string.h>

/* ── Filter helpers ── */

/* Initialize common FWPM_FILTER0 fields */
static void
wfp_filter_base(FWPM_FILTER0 *f, const GUID *layer, const GUID *sublayer,
                const wchar_t *name, UINT8 weight, UINT32 action)
{
    memset(f, 0, sizeof(*f));
    f->layerKey = *layer;
    f->subLayerKey = *sublayer;
    f->displayData.name = (wchar_t *)name;
    f->weight.type = FWP_UINT8;
    f->weight.uint8 = weight;
    f->action.type = action;
}

/* Add a single WFP filter and track its ID */
static int
add_filter(platform_win_ctx_t *p, const FWPM_FILTER0 *filter)
{
    if (p->n_wfp_filters >= MAX_WFP_FILTERS) {
        LOG_WRN("killswitch: max filter count reached");
        return -1;
    }

    UINT64 fid = 0;
    DWORD err = FwpmFilterAdd0(p->wfp_engine, filter, NULL, &fid);
    if (err != ERROR_SUCCESS) {
        LOG_ERR("FwpmFilterAdd0: error %lu", err);
        return -1;
    }

    p->wfp_filter_ids[p->n_wfp_filters++] = fid;
    return 0;
}

/* PERMIT loopback (IPv4 + IPv6) */
static int
wfp_add_loopback_permit(platform_win_ctx_t *p)
{
    static const GUID *layers[] = {
        &FWPM_LAYER_ALE_AUTH_CONNECT_V4,
        &FWPM_LAYER_ALE_AUTH_CONNECT_V6,
    };
    static const wchar_t *names[] = {
        L"mqvpn: permit loopback v4",
        L"mqvpn: permit loopback v6",
    };

    for (int i = 0; i < 2; i++) {
        FWPM_FILTER0 f;
        wfp_filter_base(&f, layers[i], &p->wfp_sublayer_key, names[i], 15,
                        FWP_ACTION_PERMIT);

        FWPM_FILTER_CONDITION0 cond;
        cond.fieldKey = FWPM_CONDITION_FLAGS;
        cond.matchType = FWP_MATCH_FLAGS_ALL_SET;
        cond.conditionValue.type = FWP_UINT32;
        cond.conditionValue.uint32 = FWP_CONDITION_FLAG_IS_LOOPBACK;

        f.filterCondition = &cond;
        f.numFilterConditions = 1;
        if (add_filter(p, &f) < 0) return -1;
    }
    return 0;
}

/* PERMIT traffic on TUN interface (IPv4 + IPv6) */
static int
wfp_add_iface_permit(platform_win_ctx_t *p)
{
    static const GUID *layers[] = {
        &FWPM_LAYER_ALE_AUTH_CONNECT_V4,
        &FWPM_LAYER_ALE_AUTH_CONNECT_V6,
    };
    static const wchar_t *names[] = {
        L"mqvpn: permit TUN v4",
        L"mqvpn: permit TUN v6",
    };

    for (int i = 0; i < 2; i++) {
        FWPM_FILTER0 f;
        wfp_filter_base(&f, layers[i], &p->wfp_sublayer_key, names[i], 14,
                        FWP_ACTION_PERMIT);

        FWPM_FILTER_CONDITION0 cond;
        cond.fieldKey = FWPM_CONDITION_IP_LOCAL_INTERFACE;
        cond.matchType = FWP_MATCH_EQUAL;
        cond.conditionValue.type = FWP_UINT64;
        cond.conditionValue.uint64 = &p->tun.luid.Value;

        f.filterCondition = &cond;
        f.numFilterConditions = 1;
        if (add_filter(p, &f) < 0) return -1;
    }
    return 0;
}

static int
wfp_current_app_id(FWP_BYTE_BLOB **out)
{
    /* GetModuleFileNameW has no size-query mode. Allocate its documented
     * extended-length ceiling off-stack so unusual install paths are either
     * represented exactly or rejected, never silently truncated. */
    const DWORD cap = 32768;
    wchar_t *path = (wchar_t *)calloc(cap, sizeof(*path));
    if (!path) return -1;

    DWORD len = GetModuleFileNameW(NULL, path, cap);
    if (len == 0 || len >= cap) {
        LOG_ERR("GetModuleFileNameW: error %lu", GetLastError());
        free(path);
        return -1;
    }

    DWORD err = FwpmGetAppIdFromFileName0(path, out);
    free(path);
    if (err != ERROR_SUCCESS) {
        LOG_ERR("FwpmGetAppIdFromFileName0: error %lu", err);
        return -1;
    }
    return 0;
}

/* PERMIT this mqvpn process's UDP transport to the exact VPN server. */
static int
wfp_add_server_permit(platform_win_ctx_t *p)
{
    FWP_BYTE_BLOB *app_id = NULL;
    if (wfp_current_app_id(&app_id) < 0) return -1;

    int rc = 0;
    if (p->server_addr.ss_family == AF_INET) {
        FWPM_FILTER0 f;
        wfp_filter_base(&f, &FWPM_LAYER_ALE_AUTH_CONNECT_V4, &p->wfp_sublayer_key,
                        L"mqvpn: permit server UDP v4", 13, FWP_ACTION_PERMIT);

        FWPM_FILTER_CONDITION0 conds[4];

        conds[0].fieldKey = FWPM_CONDITION_IP_REMOTE_ADDRESS;
        conds[0].matchType = FWP_MATCH_EQUAL;
        conds[0].conditionValue.type = FWP_UINT32;
        conds[0].conditionValue.uint32 =
            ntohl(((struct sockaddr_in *)&p->server_addr)->sin_addr.s_addr);

        conds[1].fieldKey = FWPM_CONDITION_IP_REMOTE_PORT;
        conds[1].matchType = FWP_MATCH_EQUAL;
        conds[1].conditionValue.type = FWP_UINT16;
        conds[1].conditionValue.uint16 = (UINT16)p->server_port;

        conds[2].fieldKey = FWPM_CONDITION_IP_PROTOCOL;
        conds[2].matchType = FWP_MATCH_EQUAL;
        conds[2].conditionValue.type = FWP_UINT8;
        conds[2].conditionValue.uint8 = IPPROTO_UDP;

        conds[3].fieldKey = FWPM_CONDITION_ALE_APP_ID;
        conds[3].matchType = FWP_MATCH_EQUAL;
        conds[3].conditionValue.type = FWP_BYTE_BLOB_TYPE;
        conds[3].conditionValue.byteBlob = app_id;

        f.filterCondition = conds;
        f.numFilterConditions = 4;
        rc = add_filter(p, &f);

    } else if (p->server_addr.ss_family == AF_INET6) {
        FWPM_FILTER0 f;
        wfp_filter_base(&f, &FWPM_LAYER_ALE_AUTH_CONNECT_V6, &p->wfp_sublayer_key,
                        L"mqvpn: permit server UDP v6", 13, FWP_ACTION_PERMIT);

        FWPM_FILTER_CONDITION0 conds[4];

        conds[0].fieldKey = FWPM_CONDITION_IP_REMOTE_ADDRESS;
        conds[0].matchType = FWP_MATCH_EQUAL;
        conds[0].conditionValue.type = FWP_BYTE_ARRAY16_TYPE;
        conds[0].conditionValue.byteArray16 =
            (FWP_BYTE_ARRAY16 *)&((struct sockaddr_in6 *)&p->server_addr)->sin6_addr;

        conds[1].fieldKey = FWPM_CONDITION_IP_REMOTE_PORT;
        conds[1].matchType = FWP_MATCH_EQUAL;
        conds[1].conditionValue.type = FWP_UINT16;
        conds[1].conditionValue.uint16 = (UINT16)p->server_port;

        conds[2].fieldKey = FWPM_CONDITION_IP_PROTOCOL;
        conds[2].matchType = FWP_MATCH_EQUAL;
        conds[2].conditionValue.type = FWP_UINT8;
        conds[2].conditionValue.uint8 = IPPROTO_UDP;

        conds[3].fieldKey = FWPM_CONDITION_ALE_APP_ID;
        conds[3].matchType = FWP_MATCH_EQUAL;
        conds[3].conditionValue.type = FWP_BYTE_BLOB_TYPE;
        conds[3].conditionValue.byteBlob = app_id;

        f.filterCondition = conds;
        f.numFilterConditions = 4;
        rc = add_filter(p, &f);

    } else {
        LOG_ERR("killswitch: unsupported VPN server address family %d",
                (int)p->server_addr.ss_family);
        rc = -1;
    }

    FwpmFreeMemory0((void **)&app_id);
    return rc;
}

/* BLOCK all other outbound (IPv4 + IPv6) */
static int
wfp_add_block_all(platform_win_ctx_t *p)
{
    static const GUID *layers[] = {
        &FWPM_LAYER_ALE_AUTH_CONNECT_V4,
        &FWPM_LAYER_ALE_AUTH_CONNECT_V6,
    };
    static const wchar_t *names[] = {
        L"mqvpn: block all v4",
        L"mqvpn: block all v6",
    };

    for (int i = 0; i < 2; i++) {
        FWPM_FILTER0 f;
        wfp_filter_base(&f, layers[i], &p->wfp_sublayer_key, names[i], 1,
                        FWP_ACTION_BLOCK);
        f.numFilterConditions = 0;
        if (add_filter(p, &f) < 0) return -1;
    }
    return 0;
}

/* ── Public API ── */

static void
wfp_init_dynamic_session(FWPM_SESSION0 *session)
{
    memset(session, 0, sizeof(*session));
    session->displayData.name = L"mqvpn dynamic WFP session";
    session->flags = FWPM_SESSION_FLAG_DYNAMIC;
}

int
win_setup_killswitch(platform_win_ctx_t *p)
{
    if (!p->killswitch_enabled || p->killswitch_active) return 0;

    /* A session whose close failed is unreachable but may still be blocking.
     * Stacking a second one on top of it cannot help: WFP arbitrates across
     * sublayers by veto, so the stale block-all would keep winning. */
    if (p->wfp_close_failed || p->wfp_engine) {
        LOG_ERR("refusing to open a second WFP session over a stale one");
        return -1;
    }

    DWORD err;

    /* A dynamic session makes every object created through this engine
     * lifetime-bound to the handle. If mqvpn crashes or normal cleanup is
     * interrupted, BFE removes the filters when the handle closes. */
    FWPM_SESSION0 session;
    wfp_init_dynamic_session(&session);
    err = FwpmEngineOpen0(NULL, RPC_C_AUTHN_DEFAULT, NULL, &session, &p->wfp_engine);
    if (err != ERROR_SUCCESS) {
        LOG_ERR("FwpmEngineOpen0: error %lu", err);
        return -1;
    }

    /* Begin transaction */
    err = FwpmTransactionBegin0(p->wfp_engine, 0);
    if (err != ERROR_SUCCESS) {
        LOG_ERR("FwpmTransactionBegin0: error %lu", err);
        FwpmEngineClose0(p->wfp_engine);
        p->wfp_engine = NULL;
        return -1;
    }

    /* Create sublayer */
    CoCreateGuid(&p->wfp_sublayer_key);

    FWPM_SUBLAYER0 sublayer;
    memset(&sublayer, 0, sizeof(sublayer));
    sublayer.subLayerKey = p->wfp_sublayer_key;
    sublayer.displayData.name = L"mqvpn kill switch";
    sublayer.weight = 0xFFFF; /* highest priority */

    err = FwpmSubLayerAdd0(p->wfp_engine, &sublayer, NULL);
    if (err != ERROR_SUCCESS) {
        LOG_ERR("FwpmSubLayerAdd0: error %lu", err);
        FwpmTransactionAbort0(p->wfp_engine);
        FwpmEngineClose0(p->wfp_engine);
        p->wfp_engine = NULL;
        return -1;
    }

    p->n_wfp_filters = 0;

    /* Add filters: permit loopback → permit TUN → permit server → block all */
    if (wfp_add_loopback_permit(p) < 0 || wfp_add_iface_permit(p) < 0 ||
        wfp_add_server_permit(p) < 0 || wfp_add_block_all(p) < 0) {
        FwpmTransactionAbort0(p->wfp_engine);
        FwpmEngineClose0(p->wfp_engine);
        p->wfp_engine = NULL;
        return -1;
    }

    /* Commit transaction */
    err = FwpmTransactionCommit0(p->wfp_engine);
    if (err != ERROR_SUCCESS) {
        LOG_ERR("FwpmTransactionCommit0: error %lu", err);
        FwpmTransactionAbort0(p->wfp_engine);
        FwpmEngineClose0(p->wfp_engine);
        p->wfp_engine = NULL;
        return -1;
    }

    p->killswitch_active = 1;
    LOG_INF("kill switch active (%d WFP filters)", p->n_wfp_filters);
    return 0;
}

int
win_cleanup_killswitch(platform_win_ctx_t *p)
{
    if (!p->wfp_engine) {
        p->killswitch_active = 0;
        p->n_wfp_filters = 0;
        return 0;
    }

    /* Never close a handle whose close already failed: a WFP engine handle is
     * an RPC context handle, and its state after a failed close is undefined.
     * The caller's job now is to end the process, not to retry. */
    if (p->wfp_close_failed) return -1;

    /*
     * Closing the engine ends the dynamic session, and BFE then removes the
     * sublayer and every filter added through it.
     *
     * Do not delete the objects by hand first. WFP has no cascade delete --
     * "An object cannot be deleted until all objects that reference it have
     * first been deleted" (WFP Object Management) -- so deleting the sublayer
     * alone used to leave every filter in place, block-all among them, which
     * is the shape of mp0rta/mqvpn#330. Deleting each filter by ID as well
     * closes that hole but only on the orderly path; the session is what also
     * covers a crash or a kill, because BFE runs a dynamic session down with
     * its owner.
     */
    DWORD err = FwpmEngineClose0(p->wfp_engine);
    if (err != ERROR_SUCCESS) {
        /*
         * The session may still be blocking everything, and nothing can reach
         * it any more: this handle was the only reference, and the next setup
         * would mint a fresh sublayer GUID. Keep wfp_engine and
         * killswitch_active set so no second session is stacked on the stale
         * one -- WFP arbitrates across sublayers by veto, so a stale
         * block-all keeps winning while its permits stay pinned to the
         * previous adapter LUID and server endpoint, and the host stays cut
         * off for the rest of the process lifetime (mp0rta/mqvpn#331).
         */
        p->wfp_close_failed = 1;
        LOG_ERR("FwpmEngineClose0: error %lu; kill switch filters are still "
                "live and can no longer be addressed - shutting down so BFE "
                "removes them",
                err);
        return -1;
    }

    p->wfp_engine = NULL;
    p->killswitch_active = 0;
    p->n_wfp_filters = 0;

    LOG_INF("kill switch deactivated");
    return 0;
}

#endif /* _WIN32 */
