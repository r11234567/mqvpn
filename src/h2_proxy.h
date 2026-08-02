// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 mp0rta and mqvpn contributors

#ifndef MQVPN_H2_PROXY_H
#define MQVPN_H2_PROXY_H

#include <stddef.h>
#include <stdint.h>
#include <sys/socket.h>
#include <xquic/xqc_http3.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct h2_proxy_s h2_proxy_t;
typedef struct h2_proxy_stream_s h2_proxy_stream_t;

typedef struct {
    struct sockaddr_storage backend_addr;
    socklen_t backend_addrlen;
    uint32_t max_connections;
    uint32_t max_streams_per_conn;
    uint32_t conn_timeout_sec;
    size_t max_buffered_body;
    int backend_tls;
} h2_proxy_config_t;

typedef struct {
    void (*log)(int level, const char *msg, void *user_ctx);
    void (*register_fd)(int fd, int want_read, int want_write, void *fd_ctx,
                        void *user_ctx);
    void (*unregister_fd)(int fd, void *user_ctx);
    void *user_ctx;
} h2_proxy_callbacks_t;

typedef struct {
    uint64_t total_requests;
    uint64_t active_streams;
    uint64_t active_connections;
    uint64_t backend_errors;
    uint64_t bytes_sent;
    uint64_t bytes_received;
} h2_proxy_stats_t;

h2_proxy_t *h2_proxy_create(const h2_proxy_config_t *config,
                            const h2_proxy_callbacks_t *callbacks);
void h2_proxy_destroy(h2_proxy_t *proxy);

h2_proxy_stream_t *h2_proxy_handle_request(h2_proxy_t *proxy,
                                           xqc_h3_request_t *h3_request,
                                           const xqc_http_headers_t *headers, int fin,
                                           void *h3_stream_user_data);
int h2_proxy_on_h3_body(h2_proxy_stream_t *stream, const uint8_t *data, size_t len,
                        int fin);
int h2_proxy_on_h3_writable(h2_proxy_stream_t *stream);
void h2_proxy_on_h3_close(h2_proxy_stream_t *stream);

int h2_proxy_owns_fd(const h2_proxy_t *proxy, int fd, const void *fd_ctx);
void h2_proxy_on_backend_ready(h2_proxy_t *proxy, int fd, void *fd_ctx, int readable,
                               int writable);
void h2_proxy_tick(h2_proxy_t *proxy, uint64_t now_sec);
void h2_proxy_get_stats(const h2_proxy_t *proxy, h2_proxy_stats_t *stats);

#ifdef __cplusplus
}
#endif

#endif
