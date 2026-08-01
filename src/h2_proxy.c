// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 mp0rta and mqvpn contributors

#include "h2_proxy.h"

#include <arpa/inet.h>
#include <assert.h>
#include <errno.h>
#include <fcntl.h>
#include <netinet/in.h>
#include <netinet/tcp.h>
#include <nghttp2/nghttp2.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <time.h>
#include <unistd.h>

/* HTTP/2 connection to backend */
typedef struct h2_backend_conn_s {
    int fd;
    nghttp2_session *session;
    struct sockaddr_storage addr;
    socklen_t addrlen;
    uint64_t last_active;
    int is_connected;
    int stream_count;
    struct h2_backend_conn_s *next;
} h2_backend_conn_t;

/* Stream context - links H3 client stream to H2 backend stream */
struct h2_proxy_stream_s {
    h2_proxy_t *proxy;
    xqc_h3_request_t *h3_request;
    h2_backend_conn_t *backend_conn;
    int32_t backend_stream_id;
    void *h3_user_data;

    /* Buffering for flow control */
    uint8_t *recv_buf;
    size_t recv_len;
    size_t recv_cap;

    int h3_eof_received;
    int h2_eof_received;
    int closed;

    struct h2_proxy_stream_s *next;
};

/* HTTP/2 proxy instance */
struct h2_proxy_s {
    h2_proxy_config_t config;
    h2_proxy_callbacks_t callbacks;

    /* Connection pool */
    h2_backend_conn_t *conn_pool;
    size_t conn_count;

    /* Active streams */
    h2_proxy_stream_t *streams;
    size_t stream_count;

    /* Statistics */
    h2_proxy_stats_t stats;
};

/* ─── Logging helpers ─── */

#define LOG_ERROR 0
#define LOG_WARN  1
#define LOG_INFO  2
#define LOG_DEBUG 3

static void
h2_log(h2_proxy_t *p, int level, const char *fmt, ...)
{
    if (!p || !p->callbacks.log) return;
    char buf[512];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(buf, sizeof(buf), fmt, ap);
    va_end(ap);
    p->callbacks.log(level, buf, p->callbacks.user_ctx);
}

/* ─── Backend connection management ─── */

static int
set_nonblocking(int fd)
{
    int flags = fcntl(fd, F_GETFL, 0);
    if (flags == -1) return -1;
    return fcntl(fd, F_SETFL, flags | O_NONBLOCK);
}

static int
set_tcp_nodelay(int fd)
{
    int val = 1;
    return setsockopt(fd, IPPROTO_TCP, TCP_NODELAY, &val, sizeof(val));
}

static h2_backend_conn_t *
backend_conn_create(h2_proxy_t *proxy)
{
    if (proxy->conn_count >= proxy->config.max_connections) return NULL;

    h2_backend_conn_t *conn = calloc(1, sizeof(*conn));
    if (!conn) return NULL;

    /* Create socket */
    int family = proxy->config.backend_addr.ss_family;
    conn->fd = socket(family, SOCK_STREAM, 0);
    if (conn->fd < 0) {
        free(conn);
        return NULL;
    }

    if (set_nonblocking(conn->fd) < 0 || set_tcp_nodelay(conn->fd) < 0) {
        close(conn->fd);
        free(conn);
        return NULL;
    }

    memcpy(&conn->addr, &proxy->config.backend_addr, sizeof(proxy->config.backend_addr));
    conn->addrlen = proxy->config.backend_addrlen;
    conn->last_active = (uint64_t)time(NULL);

    /* Connect to backend */
    int ret = connect(conn->fd, (struct sockaddr *)&conn->addr, conn->addrlen);
    if (ret < 0 && errno != EINPROGRESS) {
        h2_log(proxy, LOG_ERROR, "h2_proxy: connect failed: %s", strerror(errno));
        close(conn->fd);
        free(conn);
        return NULL;
    }

    /* Initialize nghttp2 session */
    nghttp2_session_callbacks *callbacks;
    nghttp2_session_callbacks_new(&callbacks);

    /* We'll set callbacks for data handling later */
    nghttp2_session_callbacks_set_send_callback(callbacks, NULL);               /* TODO */
    nghttp2_session_callbacks_set_on_frame_recv_callback(callbacks, NULL);      /* TODO */
    nghttp2_session_callbacks_set_on_data_chunk_recv_callback(callbacks, NULL); /* TODO */
    nghttp2_session_callbacks_set_on_stream_close_callback(callbacks, NULL);    /* TODO */

    if (nghttp2_session_client_new(&conn->session, callbacks, conn) != 0) {
        nghttp2_session_callbacks_del(callbacks);
        close(conn->fd);
        free(conn);
        return NULL;
    }

    nghttp2_session_callbacks_del(callbacks);

    /* Send initial connection preface and SETTINGS */
    nghttp2_settings_entry iv[1] = {
        {NGHTTP2_SETTINGS_MAX_CONCURRENT_STREAMS, proxy->config.max_streams_per_conn}};
    nghttp2_submit_settings(conn->session, NGHTTP2_FLAG_NONE, iv, 1);

    conn->is_connected = (ret == 0);

    /* Register fd with event loop */
    if (proxy->callbacks.register_fd) {
        proxy->callbacks.register_fd(conn->fd, conn, proxy->callbacks.user_ctx);
    }

    /* Add to pool */
    conn->next = proxy->conn_pool;
    proxy->conn_pool = conn;
    proxy->conn_count++;
    proxy->stats.active_connections++;

    h2_log(proxy, LOG_DEBUG, "h2_proxy: created backend connection fd=%d", conn->fd);

    return conn;
}

static void
backend_conn_destroy(h2_proxy_t *proxy, h2_backend_conn_t *conn)
{
    if (!conn) return;

    h2_log(proxy, LOG_DEBUG, "h2_proxy: destroying backend connection fd=%d", conn->fd);

    /* Unregister from event loop */
    if (proxy->callbacks.unregister_fd) {
        proxy->callbacks.unregister_fd(conn->fd, proxy->callbacks.user_ctx);
    }

    /* Close nghttp2 session */
    if (conn->session) {
        nghttp2_session_del(conn->session);
    }

    /* Close socket */
    if (conn->fd >= 0) {
        close(conn->fd);
    }

    /* Remove from pool */
    h2_backend_conn_t **pp = &proxy->conn_pool;
    while (*pp) {
        if (*pp == conn) {
            *pp = conn->next;
            break;
        }
        pp = &(*pp)->next;
    }

    proxy->conn_count--;
    proxy->stats.active_connections--;

    free(conn);
}

static h2_backend_conn_t *
backend_conn_get_available(h2_proxy_t *proxy)
{
    /* Try to find an existing connection with capacity */
    for (h2_backend_conn_t *conn = proxy->conn_pool; conn; conn = conn->next) {
        if (conn->is_connected &&
            conn->stream_count < (int)proxy->config.max_streams_per_conn) {
            return conn;
        }
    }

    /* Create new connection if under limit */
    return backend_conn_create(proxy);
}

/* ─── Stream management ─── */

static h2_proxy_stream_t *
stream_create(h2_proxy_t *proxy, xqc_h3_request_t *h3_request, void *h3_user_data)
{
    h2_proxy_stream_t *stream = calloc(1, sizeof(*stream));
    if (!stream) return NULL;

    stream->proxy = proxy;
    stream->h3_request = h3_request;
    stream->h3_user_data = h3_user_data;
    stream->backend_stream_id = -1;

    /* Add to active streams list */
    stream->next = proxy->streams;
    proxy->streams = stream;
    proxy->stream_count++;
    proxy->stats.active_streams++;

    return stream;
}

static void
stream_destroy(h2_proxy_stream_t *stream)
{
    if (!stream || stream->closed) return;

    stream->closed = 1;

    h2_proxy_t *proxy = stream->proxy;

    /* Remove from active streams */
    h2_proxy_stream_t **pp = &proxy->streams;
    while (*pp) {
        if (*pp == stream) {
            *pp = stream->next;
            break;
        }
        pp = &(*pp)->next;
    }

    /* Free buffers */
    if (stream->recv_buf) {
        free(stream->recv_buf);
    }

    /* Update backend connection stream count */
    if (stream->backend_conn) {
        stream->backend_conn->stream_count--;
    }

    proxy->stream_count--;
    proxy->stats.active_streams--;

    free(stream);
}

/* ─── nghttp2 callbacks ─── */

static ssize_t
send_callback(nghttp2_session *session, const uint8_t *data, size_t length, int flags,
              void *user_data)
{
    (void)session;
    (void)flags;

    h2_backend_conn_t *conn = (h2_backend_conn_t *)user_data;
    ssize_t sent = send(conn->fd, data, length, 0);

    if (sent < 0) {
        if (errno == EAGAIN || errno == EWOULDBLOCK) {
            return NGHTTP2_ERR_WOULDBLOCK;
        }
        return NGHTTP2_ERR_CALLBACK_FAILURE;
    }

    return sent;
}

static int
on_frame_recv_callback(nghttp2_session *session, const nghttp2_frame *frame,
                       void *user_data)
{
    (void)session;
    (void)user_data;

    /* Handle different frame types as needed */
    switch (frame->hd.type) {
    case NGHTTP2_HEADERS:
        /* Response headers received - forward to H3 client */
        break;
    case NGHTTP2_DATA:
        /* Data frame - handled by on_data_chunk_recv_callback */
        break;
    default: break;
    }

    return 0;
}

static int
on_data_chunk_recv_callback(nghttp2_session *session, uint8_t flags, int32_t stream_id,
                            const uint8_t *data, size_t len, void *user_data)
{
    (void)session;
    (void)flags;
    (void)stream_id;
    (void)data;
    (void)len;
    (void)user_data;

    /* TODO: Forward data to H3 client stream */

    return 0;
}

static int
on_stream_close_callback(nghttp2_session *session, int32_t stream_id, uint32_t error_code,
                         void *user_data)
{
    (void)session;
    (void)stream_id;
    (void)error_code;
    (void)user_data;

    /* TODO: Close corresponding H3 stream */

    return 0;
}

/* ─── Public API ─── */

h2_proxy_t *
h2_proxy_create(const h2_proxy_config_t *config, const h2_proxy_callbacks_t *callbacks)
{
    if (!config || !callbacks) return NULL;

    h2_proxy_t *proxy = calloc(1, sizeof(*proxy));
    if (!proxy) return NULL;

    memcpy(&proxy->config, config, sizeof(*config));
    memcpy(&proxy->callbacks, callbacks, sizeof(*callbacks));

    return proxy;
}

void
h2_proxy_destroy(h2_proxy_t *proxy)
{
    if (!proxy) return;

    /* Destroy all streams */
    while (proxy->streams) {
        stream_destroy(proxy->streams);
    }

    /* Destroy all backend connections */
    while (proxy->conn_pool) {
        backend_conn_destroy(proxy, proxy->conn_pool);
    }

    free(proxy);
}

int
h2_proxy_handle_request(h2_proxy_t *proxy, xqc_h3_request_t *h3_request,
                        void *h3_stream_user_data)
{
    if (!proxy || !h3_request) return -1;

    h2_log(proxy, LOG_INFO, "h2_proxy: handling HTTP/3 request");

    /* Create stream context */
    h2_proxy_stream_t *stream = stream_create(proxy, h3_request, h3_stream_user_data);
    if (!stream) {
        h2_log(proxy, LOG_ERROR, "h2_proxy: failed to create stream");
        return -1;
    }

    /* Get or create backend connection */
    h2_backend_conn_t *conn = backend_conn_get_available(proxy);
    if (!conn) {
        h2_log(proxy, LOG_ERROR, "h2_proxy: no backend connection available");
        stream_destroy(stream);
        return -1;
    }

    stream->backend_conn = conn;
    conn->stream_count++;

    /* TODO: Parse H3 headers and submit to H2 backend */
    /* This requires integration with xquic's H3 header API */

    proxy->stats.total_requests++;

    return 0;
}

int
h2_proxy_on_h3_body(h2_proxy_stream_t *stream, const uint8_t *data, size_t len, int fin)
{
    if (!stream || stream->closed) return -1;

    /* TODO: Forward body data to H2 backend stream */
    (void)data;
    (void)len;

    if (fin) {
        stream->h3_eof_received = 1;
        /* Submit H2 DATA frame with END_STREAM flag */
    }

    return 0;
}

void
h2_proxy_on_h3_close(h2_proxy_stream_t *stream)
{
    if (!stream) return;

    h2_log(stream->proxy, LOG_DEBUG, "h2_proxy: H3 stream closed");

    /* Close backend stream if exists */
    if (stream->backend_conn && stream->backend_stream_id >= 0) {
        nghttp2_submit_rst_stream(stream->backend_conn->session, NGHTTP2_FLAG_NONE,
                                  stream->backend_stream_id, NGHTTP2_NO_ERROR);
    }

    stream_destroy(stream);
}

void
h2_proxy_on_backend_ready(h2_proxy_t *proxy, int fd, void *fd_ctx, int readable,
                          int writable)
{
    if (!proxy) return;

    h2_backend_conn_t *conn = (h2_backend_conn_t *)fd_ctx;
    if (!conn || conn->fd != fd) return;

    conn->last_active = (uint64_t)time(NULL);

    if (writable && !conn->is_connected) {
        /* Connection established */
        int error = 0;
        socklen_t len = sizeof(error);
        if (getsockopt(fd, SOL_SOCKET, SO_ERROR, &error, &len) < 0 || error != 0) {
            h2_log(proxy, LOG_ERROR, "h2_proxy: backend connection failed: %s",
                   strerror(error ? error : errno));
            backend_conn_destroy(proxy, conn);
            return;
        }
        conn->is_connected = 1;
        h2_log(proxy, LOG_INFO, "h2_proxy: backend connection established");
    }

    if (writable && conn->session) {
        /* Send pending data */
        int ret = nghttp2_session_send(conn->session);
        if (ret < 0) {
            h2_log(proxy, LOG_ERROR, "h2_proxy: nghttp2_session_send failed: %s",
                   nghttp2_strerror(ret));
            backend_conn_destroy(proxy, conn);
            return;
        }
    }

    if (readable && conn->session) {
        /* Receive data */
        uint8_t buf[16384];
        ssize_t nread = recv(fd, buf, sizeof(buf), 0);

        if (nread < 0) {
            if (errno != EAGAIN && errno != EWOULDBLOCK) {
                h2_log(proxy, LOG_ERROR, "h2_proxy: recv error: %s", strerror(errno));
                backend_conn_destroy(proxy, conn);
            }
            return;
        }

        if (nread == 0) {
            h2_log(proxy, LOG_INFO, "h2_proxy: backend connection closed");
            backend_conn_destroy(proxy, conn);
            return;
        }

        proxy->stats.bytes_received += (uint64_t)nread;

        ssize_t ret = nghttp2_session_mem_recv(conn->session, buf, (size_t)nread);
        if (ret < 0) {
            h2_log(proxy, LOG_ERROR, "h2_proxy: nghttp2_session_mem_recv failed: %s",
                   nghttp2_strerror((int)ret));
            backend_conn_destroy(proxy, conn);
            return;
        }
    }
}

void
h2_proxy_tick(h2_proxy_t *proxy, uint64_t now_sec)
{
    if (!proxy) return;

    uint64_t cutoff = now_sec - proxy->config.conn_timeout_sec;

    /* Clean up idle connections */
    h2_backend_conn_t **pp = &proxy->conn_pool;
    while (*pp) {
        h2_backend_conn_t *conn = *pp;
        if (conn->stream_count == 0 && conn->last_active < cutoff) {
            backend_conn_destroy(proxy, conn);
            /* pp already updated by backend_conn_destroy */
        } else {
            pp = &conn->next;
        }
    }
}

void
h2_proxy_get_stats(h2_proxy_t *proxy, h2_proxy_stats_t *stats)
{
    if (!proxy || !stats) return;
    memcpy(stats, &proxy->stats, sizeof(*stats));
}
