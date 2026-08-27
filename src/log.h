// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 mp0rta and mqvpn contributors

#ifndef MQVPN_LOG_H
#define MQVPN_LOG_H

#include <stdio.h>
#include <stdarg.h>
#include <stdint.h>

#ifndef MQVPN_LOG_LEVEL_DEFINED
#  define MQVPN_LOG_LEVEL_DEFINED
typedef enum {
    MQVPN_LOG_DEBUG = 0,
    MQVPN_LOG_INFO,
    MQVPN_LOG_WARN,
    MQVPN_LOG_ERROR,
} mqvpn_log_level_t;
#endif

void mqvpn_log_set_level(mqvpn_log_level_t level);
#ifdef _MSC_VER
void mqvpn_log(mqvpn_log_level_t level, const char *fmt, ...);
#else
void mqvpn_log(mqvpn_log_level_t level, const char *fmt, ...)
    __attribute__((format(printf, 2, 3)));
#endif

#define LOG_DBG(fmt, ...) mqvpn_log(MQVPN_LOG_DEBUG, fmt, ##__VA_ARGS__)
#define LOG_INF(fmt, ...) mqvpn_log(MQVPN_LOG_INFO, fmt, ##__VA_ARGS__)
#define LOG_WRN(fmt, ...) mqvpn_log(MQVPN_LOG_WARN, fmt, ##__VA_ARGS__)
#define LOG_ERR(fmt, ...) mqvpn_log(MQVPN_LOG_ERROR, fmt, ##__VA_ARGS__)

/* Nonzero when `status` differs from the last status logged for `handle`.
 *
 * The public path_event callback fires on every internal lifecycle
 * transition, and the five-state public status collapses three lifecycle
 * states onto CLOSED and three onto PENDING -- so a single close arrives as
 * two or three callbacks carrying the identical status. That repeat is
 * meaningful to an observer (it is the cue to re-read the path table) but it
 * is not meaningful in an INFO log, where "path 2 -> CLOSED" twice five
 * seconds apart reads as two separate closes. The platform log callbacks
 * gate on this so INFO carries the public-status edge only; the underlying
 * transition, with both lifecycle names and the reason, is logged in full at
 * DEBUG by path_log_state_change.
 *
 * Log-only state, so a handle it has never seen counts as changed. Not
 * thread-safe and not meant to be: all three callers run on the platform
 * event loop. */
int mqvpn_log_path_status_changed(int64_t handle, int status);

#endif /* MQVPN_LOG_H */
