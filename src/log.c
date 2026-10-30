// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 mp0rta and mqvpn contributors

#include "log.h"

#include <time.h>
#ifdef _WIN32
#  include <windows.h>
#else
#  include <sys/time.h>
#endif

static mqvpn_log_level_t g_log_level = MQVPN_LOG_INFO;

static const char *level_str[] = {
    [MQVPN_LOG_DEBUG] = "DBG",
    [MQVPN_LOG_INFO] = "INF",
    [MQVPN_LOG_WARN] = "WRN",
    [MQVPN_LOG_ERROR] = "ERR",
};

void
mqvpn_log_set_level(mqvpn_log_level_t level)
{
    g_log_level = level;
}

void
mqvpn_log(mqvpn_log_level_t level, const char *fmt, ...)
{
    if (level < g_log_level) {
        return;
    }

#ifdef _WIN32
    SYSTEMTIME st;
    GetLocalTime(&st);
    fprintf(stderr, "%02d:%02d:%02d.%03d [%s] ", st.wHour, st.wMinute, st.wSecond,
            st.wMilliseconds, level_str[level]);
#else
    struct timeval tv;
    gettimeofday(&tv, NULL);
    struct tm tm;
    localtime_r(&tv.tv_sec, &tm);
    fprintf(stderr, "%02d:%02d:%02d.%03d [%s] ", tm.tm_hour, tm.tm_min, tm.tm_sec,
            (int)(tv.tv_usec / 1000), level_str[level]);
#endif

    va_list ap;
    va_start(ap, fmt);
    vfprintf(stderr, fmt, ap);
    va_end(ap);

    fprintf(stderr, "\n");
}

/* Direct-mapped, 4x MQVPN_MAX_PATHS so the live set never self-evicts;
 * handles rise monotonically across re-adds, so an old entry is eventually
 * displaced by a newer handle landing on its slot. A collision only costs a
 * repeated log line, never a suppressed real transition -- the fallible
 * direction here is the harmless one. */
#define LOG_PATH_STATUS_SLOTS 32

static struct {
    int64_t handle;
    int status;
    int seen;
} g_path_status[LOG_PATH_STATUS_SLOTS];

int
mqvpn_log_path_status_changed(int64_t handle, int status)
{
    uint64_t u = (uint64_t)handle;
    size_t i = (size_t)(u % LOG_PATH_STATUS_SLOTS);

    if (g_path_status[i].seen && g_path_status[i].handle == handle &&
        g_path_status[i].status == status)
        return 0;

    g_path_status[i].handle = handle;
    g_path_status[i].status = status;
    g_path_status[i].seen = 1;
    return 1;
}
