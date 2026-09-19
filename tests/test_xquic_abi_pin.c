// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 mp0rta and mqvpn contributors

/*
 * test_xquic_abi_pin.c — compile-time pin for xquic private-enum values
 * that libmqvpn depends on by numeric value.
 *
 * The library links shared xquic and includes only its PUBLIC header
 * (<xquic/xquic.h>), which types xqc_path_metrics_t.path_state as a bare
 * uint8_t. mqvpn depends on ALL of that private enum's values by number:
 * mqvpn_path_state_label() (mqvpn_server.c) maps every value to a string,
 * the validation poll (mqvpn_client.c) and mp-state label test ACTIVE, and
 * the raw value is surfaced through the public control API
 * (mqvpn_path_stat_t.state). So every value is mirrored in mqvpn_internal.h
 * as MQVPN_XQC_PATH_STATE_*.
 *
 * This test TU is the ONE place that includes xquic's private
 * src/transport/xqc_multipath.h, so it can assert each mqvpn-side mirror
 * still equals the real xqc_path_state_t enumerator at compile time. If a
 * future xquic merge renumbers the enum, this file fails to compile and the
 * mirror must be updated in lockstep.
 *
 * app_path_status is deliberately NOT pinned here: mqvpn only relays those
 * classes to xquic through the named xqc_conn_mark_path_* calls (see
 * client_notify_xqc_path_state), never by numeric value, so there is no ABI
 * value dependency to guard.
 *
 * There is no runtime behaviour here — the _Static_asserts are the test.
 */

#include "mqvpn_internal.h"              /* MQVPN_XQC_PATH_STATE_* */
#include <xquic/xqc_http3.h>             /* H3 proxy backpressure API */
#include "src/transport/xqc_multipath.h" /* xqc_path_state_t (private)  */

#define PIN_PATH_STATE(mirror, real)                                                 \
    _Static_assert((mirror) == (real), "mqvpn " #mirror " drifted from xquic " #real \
                                       " -- update the mirror in mqvpn_internal.h")

PIN_PATH_STATE(MQVPN_XQC_PATH_STATE_INIT, XQC_PATH_STATE_INIT);
PIN_PATH_STATE(MQVPN_XQC_PATH_STATE_VALIDATING, XQC_PATH_STATE_VALIDATING);
PIN_PATH_STATE(MQVPN_XQC_PATH_STATE_ACTIVE, XQC_PATH_STATE_ACTIVE);
PIN_PATH_STATE(MQVPN_XQC_PATH_STATE_CLOSING, XQC_PATH_STATE_CLOSING);
PIN_PATH_STATE(MQVPN_XQC_PATH_STATE_CLOSED, XQC_PATH_STATE_CLOSED);

/* ── mqvpn public-ABI freeze pins (kindred purpose, different source) ──
 * Constants baked into public struct LAYOUT: changing one silently breaks
 * callers built against the older header (the library writes these arrays
 * bounded by its own compiled-in value — see the ABI-FROZEN comments in
 * libmqvpn.h). This assert turns the comment into enforcement: a bump
 * cannot compile without touching this pin, forcing the SemVer/layout
 * conversation the comment asks for. */
_Static_assert(MQVPN_MAX_PATHS == 8,
               "MQVPN_MAX_PATHS is ABI-frozen (embedded in mqvpn_client_info_t "
               "layout) -- see libmqvpn.h before changing this pin");

/* The H3-to-h2c proxy relies on these public symbols from the pinned xquic
 * fork. Keeping typed references here makes an accidental submodule rollback
 * fail at compile time instead of silently restoring the unbounded reader. */
static uint64_t (*const pin_h3_send_queue_bytes)(xqc_h3_request_t *) =
    xqc_h3_request_get_send_queue_bytes;
static xqc_int_t (*const pin_h3_write_notify)(xqc_h3_request_t *,
                                              uint8_t) = xqc_h3_request_set_write_notify;

int
main(void)
{
    (void)pin_h3_send_queue_bytes;
    (void)pin_h3_write_notify;
    return 0;
}
