// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 mp0rta and mqvpn contributors

/*
 * h2_proxy.h — HTTP/2 proxy for non-MASQUE traffic
 *
 * When a QUIC/HTTP3 connection arrives with the correct SNI but does NOT
 * request CONNECT-IP, this module proxies the request to an upstream HTTP/2
 * backend, acting as a protocol translator.
 *
 * Architecture:
 *   Client (HTTP/3) <-> mqvpn (this proxy) <-> Backend (HTTP/2)
 */

#ifndef MQVPN_H2_PROXY_H
#define MQVPN_H2_PROXY_H

#include <stddef.h>
#include <stdint.h>
#include <sys/socket.h>
#include <xquic/xqc_http3.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Forward declarations */
typedef struct h2_proxy_s h2_proxy_t;
typedef struct h2_proxy_stream_s h2_proxy_stream_t;

/* Configuration for HTTP/2 proxy */
typedef struct {
    /* Upstream backend address (HTTP/2 server) */
    struct sockaddr_storage backend_addr;
    socklen_t backend_addrlen;

    /* Connection pool settings */
    uint32_t max_connections;      /* Max concurrent H2 connections to backend */
    uint32_t max_streams_per_conn; /* Max streams per H2 connection */
    uint32_t conn_timeout_sec;     /* Idle connection timeout */

    /* Whether to use TLS for backend connections */
    int backend_tls;

    /* TLS SNI for backend (if backend_tls = 1) */
    char backend_sni[256];

    /* Backend path prefix (e.g., "/" or "/api") */
    char path_prefix[256];

    /* Connection reuse strategy */
    int enable_connection_reuse;
} h2_proxy_config_t;

/* Callbacks for integration with mqvpn server */
typedef struct {
    /* Log callback */
    void (*log)(int level, const char *msg, void *user_ctx);

    /* Socket I/O callbacks - for non-blocking operation */
    int (*register_fd)(int fd, void *fd_ctx, void *user_ctx);
    void (*unregister_fd)(int fd, void *user_ctx);

    void *user_ctx;
} h2_proxy_callbacks_t;

/*
 * Create a new HTTP/2 proxy instance.
 * Returns NULL on allocation failure or invalid config.
 */
h2_proxy_t *h2_proxy_create(const h2_proxy_config_t *config,
                            const h2_proxy_callbacks_t *callbacks);

/*
 * Destroy HTTP/2 proxy and free all resources.
 */
void h2_proxy_destroy(h2_proxy_t *proxy);

/*
 * Handle incoming HTTP/3 request that should be proxied to HTTP/2 backend.
 *
 * This is called from mqvpn_server's cb_request_read when:
 * 1. SNI matches (passed SNI router)
 * 2. :protocol is NOT "connect-ip" (not a MASQUE request)
 *
 * The function will:
 * 1. Parse HTTP/3 headers
 * 2. Establish or reuse HTTP/2 connection to backend
 * 3. Forward request to backend
 * 4. Stream response back to client
 *
 * Returns 0 on success (async operation started), -1 on immediate error.
 */
int h2_proxy_handle_request(h2_proxy_t *proxy, xqc_h3_request_t *h3_request,
                            void *h3_stream_user_data);

/*
 * Handle data received from HTTP/3 client.
 * Called from xqc_h3_request_read_notify callback.
 */
int h2_proxy_on_h3_body(h2_proxy_stream_t *stream, const uint8_t *data, size_t len,
                        int fin);

/*
 * Handle HTTP/3 stream close.
 * Called from xqc_h3_request_close_notify callback.
 */
void h2_proxy_on_h3_close(h2_proxy_stream_t *stream);

/*
 * Handle backend socket ready for read/write.
 * Called from event loop when backend fd has events.
 */
void h2_proxy_on_backend_ready(h2_proxy_t *proxy, int fd, void *fd_ctx, int readable,
                               int writable);

/*
 * Periodic maintenance - clean up idle connections, timeouts, etc.
 * Should be called periodically (e.g., every second).
 */
void h2_proxy_tick(h2_proxy_t *proxy, uint64_t now_sec);

/*
 * Get statistics for monitoring.
 */
typedef struct {
    uint64_t total_requests;
    uint64_t active_streams;
    uint64_t active_connections;
    uint64_t backend_errors;
    uint64_t bytes_sent;
    uint64_t bytes_received;
} h2_proxy_stats_t;

void h2_proxy_get_stats(h2_proxy_t *proxy, h2_proxy_stats_t *stats);

#ifdef __cplusplus
}
#endif

#endif /* MQVPN_H2_PROXY_H */
