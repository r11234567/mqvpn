// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 mp0rta and mqvpn contributors

#include "h2_proxy.h"

#include <arpa/inet.h>
#include <errno.h>
#include <fcntl.h>
#include <limits.h>
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

#define H2_DEFAULT_MAX_CONNECTIONS 16u
#define H2_DEFAULT_MAX_STREAMS     100u
#define H2_DEFAULT_TIMEOUT_SEC     60u
#define H2_DEFAULT_MAX_BUFFER      (1024u * 1024u)
#define H2_MAX_RESPONSE_HEADERS    256u
#define H2_MAX_HEADER_BYTES        (32u * 1024u)

/* Proxy Protocol v2 constants */
#define PROXY_PROTOCOL_V2_SIG       "\x0D\x0A\x0D\x0A\x00\x0D\x0A\x51\x55\x49\x54\x0A"
#define PROXY_PROTOCOL_V2_SIG_LEN   12
#define PROXY_PROTOCOL_V2_VERSION   0x20
#define PROXY_PROTOCOL_V2_CMD_PROXY 0x01
#define PROXY_PROTOCOL_V2_AF_INET   0x11
#define PROXY_PROTOCOL_V2_AF_INET6  0x21

typedef struct h2_backend_conn_s h2_backend_conn_t;

struct h2_proxy_stream_s {
    h2_proxy_t *proxy;
    xqc_h3_request_t *h3_request;
    void *h3_user_data;
    h2_backend_conn_t *backend_conn;
    int32_t backend_stream_id;

    struct sockaddr_storage client_addr;
    socklen_t client_addrlen;

    uint8_t *request_body;
    size_t request_body_len;
    size_t request_body_off;
    int request_eof;

    uint8_t *response_body;
    size_t response_body_len;
    size_t response_body_off;
    int response_eof;
    int response_fin_sent;

    xqc_http_header_t *response_headers;
    size_t response_header_count;
    size_t response_header_cap;
    size_t response_header_bytes;
    int response_headers_pending;
    int response_headers_are_trailers;
    int response_headers_informational;
    int response_status_seen;
    int response_started;
    int closed;
    struct h2_proxy_stream_s *next;
};

struct h2_backend_conn_s {
    h2_proxy_t *proxy;
    int fd;
    nghttp2_session *session;
    uint64_t last_active;
    uint32_t stream_count;
    int connected;
    int proxy_protocol_sent;
    uint8_t proxy_protocol_buf[128];
    size_t proxy_protocol_len;
    size_t proxy_protocol_off;
    struct h2_backend_conn_s *next;
};

struct h2_proxy_s {
    h2_proxy_config_t config;
    h2_proxy_callbacks_t callbacks;
    h2_backend_conn_t *connections;
    size_t connection_count;
    h2_proxy_stream_t *streams;
    h2_proxy_stats_t stats;
};

static void backend_destroy(h2_backend_conn_t *conn);
static int stream_flush_response(h2_proxy_stream_t *stream);
static int is_hop_by_hop(const uint8_t *name, size_t len, const uint8_t *value,
                         size_t value_len);
static void proxy_log(h2_proxy_t *proxy, int level, const char *fmt, ...);
static uint64_t wall_time_sec(void);
static ssize_t build_proxy_protocol_v2(uint8_t *buf, size_t buf_len,
                                       const struct sockaddr *client_addr,
                                       socklen_t client_addrlen,
                                       const struct sockaddr *server_addr,
                                       socklen_t server_addrlen);
static int prepare_proxy_protocol(h2_backend_conn_t *conn,
                                  const struct sockaddr *client_addr,
                                  socklen_t client_addrlen);
static int flush_proxy_protocol(h2_backend_conn_t *conn);

static ssize_t
build_proxy_protocol_v2(uint8_t *buf, size_t buf_len, const struct sockaddr *client_addr,
                        socklen_t client_addrlen, const struct sockaddr *server_addr,
                        socklen_t server_addrlen)
{
    /* Proxy Protocol v2 format:
     *   12 bytes: signature
     *   1 byte:   version (4 bits) + command (4 bits)
     *   1 byte:   address family (4 bits) + protocol (4 bits)
     *   2 bytes:  address length (big-endian)
     *   N bytes:  address data
     */
    if (!buf || !client_addr || !server_addr || buf_len < 16) return -1;

    const struct sockaddr_in *c4 = NULL, *s4 = NULL;
    const struct sockaddr_in6 *c6 = NULL, *s6 = NULL;
    uint8_t af_proto = 0;
    uint16_t addr_len = 0;
    size_t offset = 0;

    /* Signature */
    memcpy(buf, PROXY_PROTOCOL_V2_SIG, PROXY_PROTOCOL_V2_SIG_LEN);
    offset = PROXY_PROTOCOL_V2_SIG_LEN;

    /* Version + Command: v2 + PROXY */
    buf[offset++] = PROXY_PROTOCOL_V2_VERSION | PROXY_PROTOCOL_V2_CMD_PROXY;

    /* Determine address family */
    if (client_addr->sa_family == AF_INET && server_addr->sa_family == AF_INET &&
        client_addrlen >= sizeof(struct sockaddr_in) &&
        server_addrlen >= sizeof(struct sockaddr_in)) {
        c4 = (const struct sockaddr_in *)client_addr;
        s4 = (const struct sockaddr_in *)server_addr;
        af_proto = PROXY_PROTOCOL_V2_AF_INET; /* AF_INET + STREAM */
        addr_len = 12;                        /* 4 + 4 + 2 + 2 */
    } else if (client_addr->sa_family == AF_INET6 && server_addr->sa_family == AF_INET6 &&
               client_addrlen >= sizeof(struct sockaddr_in6) &&
               server_addrlen >= sizeof(struct sockaddr_in6)) {
        c6 = (const struct sockaddr_in6 *)client_addr;
        s6 = (const struct sockaddr_in6 *)server_addr;
        af_proto = PROXY_PROTOCOL_V2_AF_INET6; /* AF_INET6 + STREAM */
        addr_len = 36;                         /* 16 + 16 + 2 + 2 */
    } else {
        /* Address family mismatch or unsupported */
        return -1;
    }

    if (buf_len < (size_t)(offset + 2 + addr_len)) return -1;

    buf[offset++] = af_proto;

    /* Address length (big-endian) */
    buf[offset++] = (addr_len >> 8) & 0xFF;
    buf[offset++] = addr_len & 0xFF;

    /* Address data */
    if (c4 && s4) {
        /* IPv4: src_addr (4) + dst_addr (4) + src_port (2) + dst_port (2) */
        memcpy(buf + offset, &c4->sin_addr.s_addr, 4);
        offset += 4;
        memcpy(buf + offset, &s4->sin_addr.s_addr, 4);
        offset += 4;
        memcpy(buf + offset, &c4->sin_port, 2);
        offset += 2;
        memcpy(buf + offset, &s4->sin_port, 2);
        offset += 2;
    } else if (c6 && s6) {
        /* IPv6: src_addr (16) + dst_addr (16) + src_port (2) + dst_port (2) */
        memcpy(buf + offset, &c6->sin6_addr, 16);
        offset += 16;
        memcpy(buf + offset, &s6->sin6_addr, 16);
        offset += 16;
        memcpy(buf + offset, &c6->sin6_port, 2);
        offset += 2;
        memcpy(buf + offset, &s6->sin6_port, 2);
        offset += 2;
    }

    return (ssize_t)offset;
}

static int
prepare_proxy_protocol(h2_backend_conn_t *conn, const struct sockaddr *client_addr,
                       socklen_t client_addrlen)
{
    struct sockaddr_storage local_addr;
    struct sockaddr_in mapped_client;
    const struct sockaddr *source_addr = client_addr;
    socklen_t source_addrlen = client_addrlen;
    socklen_t local_addrlen = sizeof(local_addr);
    if (!conn || !client_addr ||
        getsockname(conn->fd, (struct sockaddr *)&local_addr, &local_addrlen) != 0)
        return -1;

    /* A dual-stack QUIC listener commonly reports an IPv4 peer as an
     * IPv4-mapped IPv6 sockaddr, while the h2c backend socket is AF_INET.
     * PROXY v2 requires one address family for both endpoints; normalize that
     * representation so the real IPv4 client address is preserved. */
    if (client_addr->sa_family == AF_INET6 &&
        local_addr.ss_family == AF_INET) {
        const struct sockaddr_in6 *client6 = (const struct sockaddr_in6 *)client_addr;
        static const uint8_t v4_prefix[12] = {0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xff, 0xff};
        if (memcmp(&client6->sin6_addr, v4_prefix, sizeof(v4_prefix)) == 0) {
            memset(&mapped_client, 0, sizeof(mapped_client));
            mapped_client.sin_family = AF_INET;
            memcpy(&mapped_client.sin_addr, &client6->sin6_addr.s6_addr[12], 4);
            mapped_client.sin_port = client6->sin6_port;
            source_addr = (const struct sockaddr *)&mapped_client;
            source_addrlen = sizeof(mapped_client);
        }
    }
    ssize_t length = build_proxy_protocol_v2(
        conn->proxy_protocol_buf, sizeof(conn->proxy_protocol_buf), source_addr,
        source_addrlen, (const struct sockaddr *)&local_addr, local_addrlen);
    if (length <= 0) return -1;
    conn->proxy_protocol_len = (size_t)length;
    conn->proxy_protocol_off = 0;
    return 0;
}

static int
flush_proxy_protocol(h2_backend_conn_t *conn)
{
    while (conn->proxy_protocol_off < conn->proxy_protocol_len) {
        ssize_t sent =
            send(conn->fd, conn->proxy_protocol_buf + conn->proxy_protocol_off,
                 conn->proxy_protocol_len - conn->proxy_protocol_off, MSG_NOSIGNAL);
        if (sent > 0) {
            conn->proxy_protocol_off += (size_t)sent;
            conn->proxy->stats.bytes_sent += (uint64_t)sent;
            conn->last_active = wall_time_sec();
            continue;
        }
        if (sent < 0 && (errno == EAGAIN || errno == EWOULDBLOCK)) return 0;
        return -1;
    }
    if (conn->proxy_protocol_len > 0 && !conn->proxy_protocol_sent) {
        conn->proxy_protocol_sent = 1;
        proxy_log(conn->proxy, 1, "[H2Proxy] Sent Proxy Protocol v2 (%zu bytes)",
                  conn->proxy_protocol_len);
    }
    return 0;
}

static void
proxy_log(h2_proxy_t *proxy, int level, const char *fmt, ...)
{
    if (!proxy || !proxy->callbacks.log) return;
    char message[512];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(message, sizeof(message), fmt, ap);
    va_end(ap);
    proxy->callbacks.log(level, message, proxy->callbacks.user_ctx);
}

static uint64_t
wall_time_sec(void)
{
    time_t now = time(NULL);
    return now < 0 ? 0 : (uint64_t)now;
}

static int
set_nonblocking(int fd)
{
    int flags = fcntl(fd, F_GETFL, 0);
    return flags >= 0 && fcntl(fd, F_SETFL, flags | O_NONBLOCK) == 0 ? 0 : -1;
}

static int
buffer_append(uint8_t **buffer, size_t *length, size_t *offset, const uint8_t *data,
              size_t data_len, size_t limit)
{
    if (data_len == 0) return 0;
    if (!data || *length < *offset) return -1;
    size_t buffered = *length - *offset;
    if (buffered > limit || data_len > limit - buffered) return -1;
    if (*offset > 0) {
        memmove(*buffer, *buffer + *offset, *length - *offset);
        *length -= *offset;
        *offset = 0;
    }
    if (*length > SIZE_MAX - data_len) return -1;
    uint8_t *grown = realloc(*buffer, *length + data_len);
    if (!grown) return -1;
    memcpy(grown + *length, data, data_len);
    *buffer = grown;
    *length += data_len;
    return 0;
}

static void
response_headers_clear(h2_proxy_stream_t *stream)
{
    for (size_t i = 0; i < stream->response_header_count; i++) {
        free(stream->response_headers[i].name.iov_base);
        free(stream->response_headers[i].value.iov_base);
    }
    free(stream->response_headers);
    stream->response_headers = NULL;
    stream->response_header_count = 0;
    stream->response_header_cap = 0;
    stream->response_header_bytes = 0;
    stream->response_headers_pending = 0;
}

static int
response_header_append(h2_proxy_stream_t *stream, const uint8_t *name, size_t name_len,
                       const uint8_t *value, size_t value_len)
{
    if (name_len > H2_MAX_HEADER_BYTES || value_len > H2_MAX_HEADER_BYTES ||
        name_len > H2_MAX_HEADER_BYTES - value_len ||
        stream->response_header_bytes > H2_MAX_HEADER_BYTES - name_len - value_len)
        return -1;
    if (stream->response_header_count == stream->response_header_cap) {
        size_t cap = stream->response_header_cap ? stream->response_header_cap * 2 : 8;
        if (cap > H2_MAX_RESPONSE_HEADERS) return -1;
        xqc_http_header_t *headers =
            realloc(stream->response_headers, cap * sizeof(*stream->response_headers));
        if (!headers) return -1;
        memset(headers + stream->response_header_cap, 0,
               (cap - stream->response_header_cap) * sizeof(*headers));
        stream->response_headers = headers;
        stream->response_header_cap = cap;
    }
    xqc_http_header_t *header = &stream->response_headers[stream->response_header_count];
    header->name.iov_base = malloc(name_len ? name_len : 1);
    header->value.iov_base = malloc(value_len ? value_len : 1);
    if (!header->name.iov_base || !header->value.iov_base) {
        free(header->name.iov_base);
        free(header->value.iov_base);
        memset(header, 0, sizeof(*header));
        return -1;
    }
    memcpy(header->name.iov_base, name, name_len);
    memcpy(header->value.iov_base, value, value_len);
    header->name.iov_len = name_len;
    header->value.iov_len = value_len;
    stream->response_header_count++;
    stream->response_header_bytes += name_len + value_len;
    return 0;
}

static void
stream_detach_backend(h2_proxy_stream_t *stream)
{
    if (!stream->backend_conn) return;
    if (stream->backend_conn->stream_count > 0) stream->backend_conn->stream_count--;
    stream->backend_conn = NULL;
    stream->backend_stream_id = -1;
}

static h2_proxy_stream_t *
stream_create(h2_proxy_t *proxy, xqc_h3_request_t *request, void *h3_user_data)
{
    h2_proxy_stream_t *stream = calloc(1, sizeof(*stream));
    if (!stream) return NULL;
    stream->proxy = proxy;
    stream->h3_request = request;
    stream->h3_user_data = h3_user_data;
    stream->backend_stream_id = -1;
    stream->next = proxy->streams;
    proxy->streams = stream;
    proxy->stats.active_streams++;
    return stream;
}

static void
stream_destroy(h2_proxy_stream_t *stream)
{
    if (!stream || stream->closed) return;
    stream->closed = 1;
    h2_proxy_t *proxy = stream->proxy;
    if (stream->backend_conn && stream->backend_stream_id >= 0)
        (void)nghttp2_session_set_stream_user_data(stream->backend_conn->session,
                                                   stream->backend_stream_id, NULL);
    h2_proxy_stream_t **link = &proxy->streams;
    while (*link && *link != stream)
        link = &(*link)->next;
    if (*link) *link = stream->next;
    stream_detach_backend(stream);
    response_headers_clear(stream);
    free(stream->request_body);
    free(stream->response_body);
    if (proxy->stats.active_streams > 0) proxy->stats.active_streams--;
    free(stream);
}

static h2_proxy_stream_t *
stream_from_id(nghttp2_session *session, int32_t stream_id)
{
    return nghttp2_session_get_stream_user_data(session, stream_id);
}

static void
backend_update_interest(h2_backend_conn_t *conn)
{
    h2_proxy_t *proxy = conn->proxy;
    if (!proxy->callbacks.register_fd) return;
    int want_write = !conn->connected ||
                     (conn->proxy_protocol_len > conn->proxy_protocol_off) ||
                     nghttp2_session_want_write(conn->session);
    proxy->callbacks.register_fd(conn->fd, 1, want_write, conn,
                                 proxy->callbacks.user_ctx);
}

static ssize_t
send_callback(nghttp2_session *session, const uint8_t *data, size_t length, int flags,
              void *user_data)
{
    (void)session;
    (void)flags;
    h2_backend_conn_t *conn = user_data;
    ssize_t sent = send(conn->fd, data, length, MSG_NOSIGNAL);
    if (sent < 0) {
        if (errno == EAGAIN || errno == EWOULDBLOCK) return NGHTTP2_ERR_WOULDBLOCK;
        return NGHTTP2_ERR_CALLBACK_FAILURE;
    }
    conn->proxy->stats.bytes_sent += (uint64_t)sent;
    conn->last_active = wall_time_sec();
    return sent;
}

static ssize_t
request_body_read_callback(nghttp2_session *session, int32_t stream_id, uint8_t *buf,
                           size_t length, uint32_t *data_flags,
                           nghttp2_data_source *source, void *user_data)
{
    (void)session;
    (void)stream_id;
    (void)user_data;
    h2_proxy_stream_t *stream = source->ptr;
    size_t available = stream->request_body_len - stream->request_body_off;
    if (available == 0) {
        if (!stream->request_eof) return NGHTTP2_ERR_DEFERRED;
        *data_flags |= NGHTTP2_DATA_FLAG_EOF;
        return 0;
    }
    if (length > available) length = available;
    memcpy(buf, stream->request_body + stream->request_body_off, length);
    stream->request_body_off += length;
    if (stream->request_body_off == stream->request_body_len) {
        stream->request_body_off = stream->request_body_len = 0;
        if (stream->request_eof) *data_flags |= NGHTTP2_DATA_FLAG_EOF;
    }
    return (ssize_t)length;
}

static int
on_begin_headers_callback(nghttp2_session *session, const nghttp2_frame *frame,
                          void *user_data)
{
    (void)user_data;
    if (frame->hd.type != NGHTTP2_HEADERS) return 0;
    h2_proxy_stream_t *stream = stream_from_id(session, frame->hd.stream_id);
    if (!stream) return 0;
    if (stream->response_headers_pending) return NGHTTP2_ERR_CALLBACK_FAILURE;
    response_headers_clear(stream);
    stream->response_headers_are_trailers = stream->response_started;
    stream->response_headers_informational = 0;
    stream->response_status_seen = 0;
    return 0;
}

static int
on_header_callback(nghttp2_session *session, const nghttp2_frame *frame,
                   const uint8_t *name, size_t name_len, const uint8_t *value,
                   size_t value_len, uint8_t flags, void *user_data)
{
    (void)flags;
    (void)user_data;
    if (frame->hd.type != NGHTTP2_HEADERS) return 0;
    h2_proxy_stream_t *stream = stream_from_id(session, frame->hd.stream_id);
    if (!stream) return 0;
    if (is_hop_by_hop(name, name_len, value, value_len)) return 0;
    if (name_len > 0 && name[0] == ':') {
        if (stream->response_headers_are_trailers || stream->response_status_seen ||
            name_len != 7 || memcmp(name, ":status", 7) != 0 || value_len != 3 ||
            value[0] < '1' || value[0] > '5' || value[1] < '0' || value[1] > '9' ||
            value[2] < '0' || value[2] > '9')
            return NGHTTP2_ERR_CALLBACK_FAILURE;
        unsigned status = (unsigned)(value[0] - '0') * 100u +
                          (unsigned)(value[1] - '0') * 10u + (unsigned)(value[2] - '0');
        if (status == 101) return NGHTTP2_ERR_CALLBACK_FAILURE;
        stream->response_status_seen = 1;
        stream->response_headers_informational = status < 200;
    }
    return response_header_append(stream, name, name_len, value, value_len) == 0
               ? 0
               : NGHTTP2_ERR_CALLBACK_FAILURE;
}

static int
on_data_chunk_recv_callback(nghttp2_session *session, uint8_t flags, int32_t stream_id,
                            const uint8_t *data, size_t len, void *user_data)
{
    (void)flags;
    (void)user_data;
    h2_proxy_stream_t *stream = stream_from_id(session, stream_id);
    if (!stream) return 0;
    if (buffer_append(&stream->response_body, &stream->response_body_len,
                      &stream->response_body_off, data, len,
                      stream->proxy->config.max_buffered_body) != 0)
        return NGHTTP2_ERR_CALLBACK_FAILURE;
    return stream_flush_response(stream) == 0 ? 0 : NGHTTP2_ERR_CALLBACK_FAILURE;
}

static int
on_frame_recv_callback(nghttp2_session *session, const nghttp2_frame *frame,
                       void *user_data)
{
    (void)user_data;
    h2_proxy_stream_t *stream = stream_from_id(session, frame->hd.stream_id);
    if (!stream) return 0;
    if (frame->hd.type == NGHTTP2_HEADERS) {
        if ((!stream->response_headers_are_trailers && !stream->response_status_seen) ||
            (stream->response_headers_informational &&
             (frame->hd.flags & NGHTTP2_FLAG_END_STREAM)))
            return NGHTTP2_ERR_CALLBACK_FAILURE;
        stream->response_headers_pending = 1;
        if (frame->hd.flags & NGHTTP2_FLAG_END_STREAM) stream->response_eof = 1;
        if (stream_flush_response(stream) != 0) return NGHTTP2_ERR_CALLBACK_FAILURE;
    } else if (frame->hd.type == NGHTTP2_DATA &&
               (frame->hd.flags & NGHTTP2_FLAG_END_STREAM)) {
        stream->response_eof = 1;
        if (stream_flush_response(stream) != 0) return NGHTTP2_ERR_CALLBACK_FAILURE;
    }
    return 0;
}

static int
on_stream_close_callback(nghttp2_session *session, int32_t stream_id, uint32_t error_code,
                         void *user_data)
{
    (void)user_data;
    h2_proxy_stream_t *stream = stream_from_id(session, stream_id);
    if (!stream) return 0;
    if (error_code != NGHTTP2_NO_ERROR && !stream->response_fin_sent)
        xqc_h3_request_close(stream->h3_request);
    stream_detach_backend(stream);
    return 0;
}

static h2_backend_conn_t *
backend_create(h2_proxy_t *proxy)
{
    if (proxy->connection_count >= proxy->config.max_connections) return NULL;
    h2_backend_conn_t *conn = calloc(1, sizeof(*conn));
    if (!conn) return NULL;
    conn->proxy = proxy;
    conn->fd = -1;
    int family = proxy->config.backend_addr.ss_family;
    conn->fd = socket(family, SOCK_STREAM, 0);
    if (conn->fd < 0 || set_nonblocking(conn->fd) != 0) goto fail;
    int one = 1;
    if (setsockopt(conn->fd, IPPROTO_TCP, TCP_NODELAY, &one, sizeof(one)) != 0) goto fail;

    int connect_rc = connect(conn->fd, (struct sockaddr *)&proxy->config.backend_addr,
                             proxy->config.backend_addrlen);
    if (connect_rc != 0 && errno != EINPROGRESS) goto fail;
    conn->connected = connect_rc == 0;
    conn->last_active = wall_time_sec();

    nghttp2_session_callbacks *callbacks = NULL;
    if (nghttp2_session_callbacks_new(&callbacks) != 0) goto fail;
    nghttp2_session_callbacks_set_send_callback(callbacks, send_callback);
    nghttp2_session_callbacks_set_on_begin_headers_callback(callbacks,
                                                            on_begin_headers_callback);
    nghttp2_session_callbacks_set_on_header_callback(callbacks, on_header_callback);
    nghttp2_session_callbacks_set_on_frame_recv_callback(callbacks,
                                                         on_frame_recv_callback);
    nghttp2_session_callbacks_set_on_data_chunk_recv_callback(
        callbacks, on_data_chunk_recv_callback);
    nghttp2_session_callbacks_set_on_stream_close_callback(callbacks,
                                                           on_stream_close_callback);
    int session_rc = nghttp2_session_client_new(&conn->session, callbacks, conn);
    nghttp2_session_callbacks_del(callbacks);
    if (session_rc != 0) goto fail;

    nghttp2_settings_entry settings[] = {
        {NGHTTP2_SETTINGS_MAX_CONCURRENT_STREAMS, proxy->config.max_streams_per_conn},
        {NGHTTP2_SETTINGS_ENABLE_CONNECT_PROTOCOL, 1},
    };
    if (nghttp2_submit_settings(conn->session, NGHTTP2_FLAG_NONE, settings,
                                sizeof(settings) / sizeof(settings[0])) != 0)
        goto fail;

    conn->next = proxy->connections;
    proxy->connections = conn;
    proxy->connection_count++;
    proxy->stats.active_connections++;
    backend_update_interest(conn);
    return conn;

fail:
    if (conn->session) nghttp2_session_del(conn->session);
    if (conn->fd >= 0) close(conn->fd);
    free(conn);
    return NULL;
}

static h2_backend_conn_t *
backend_available(h2_proxy_t *proxy)
{
    for (h2_backend_conn_t *conn = proxy->connections; conn; conn = conn->next)
        if (conn->stream_count < proxy->config.max_streams_per_conn) return conn;
    return backend_create(proxy);
}

static void
send_bad_gateway(h2_proxy_stream_t *stream)
{
    if (stream->response_started || !stream->h3_request) return;
    xqc_http_header_t status = {
        .name = {.iov_base = (void *)":status", .iov_len = 7},
        .value = {.iov_base = (void *)"502", .iov_len = 3},
    };
    xqc_http_headers_t headers = {.headers = &status, .count = 1, .capacity = 1};
    if (xqc_h3_request_send_headers(stream->h3_request, &headers, 1) >= 0) {
        stream->response_started = 1;
        stream->response_fin_sent = 1;
    }
}

static void
backend_destroy(h2_backend_conn_t *conn)
{
    if (!conn) return;
    h2_proxy_t *proxy = conn->proxy;
    h2_backend_conn_t **link = &proxy->connections;
    while (*link && *link != conn)
        link = &(*link)->next;
    if (*link) *link = conn->next;

    for (h2_proxy_stream_t *stream = proxy->streams; stream; stream = stream->next) {
        if (stream->backend_conn != conn) continue;
        send_bad_gateway(stream);
        stream->backend_conn = NULL;
        stream->backend_stream_id = -1;
    }
    if (proxy->callbacks.unregister_fd)
        proxy->callbacks.unregister_fd(conn->fd, proxy->callbacks.user_ctx);
    nghttp2_session_del(conn->session);
    close(conn->fd);
    if (proxy->connection_count > 0) proxy->connection_count--;
    if (proxy->stats.active_connections > 0) proxy->stats.active_connections--;
    free(conn);
}

static void
backend_fail(h2_backend_conn_t *conn)
{
    conn->proxy->stats.backend_errors++;
    backend_destroy(conn);
}

static int
is_hop_by_hop(const uint8_t *name, size_t len, const uint8_t *value, size_t value_len)
{
    static const char *blocked[] = {"connection", "keep-alive", "proxy-connection",
                                    "transfer-encoding", "upgrade"};
    for (size_t i = 0; i < sizeof(blocked) / sizeof(blocked[0]); i++)
        if (strlen(blocked[i]) == len && memcmp(name, blocked[i], len) == 0) return 1;
    if (len == 2 && memcmp(name, "te", 2) == 0 &&
        !(value_len == 8 && memcmp(value, "trailers", 8) == 0))
        return 1;
    return 0;
}

static int
submit_request(h2_proxy_stream_t *stream, const xqc_http_headers_t *headers, int fin)
{
    if (!headers || headers->count == 0 || headers->count > 256) return -1;

    h2_backend_conn_t *conn = stream->backend_conn;
    h2_proxy_t *proxy = stream->proxy;

    if (proxy->config.backend_proxy_protocol && conn->proxy_protocol_len == 0 &&
        !conn->proxy_protocol_sent) {
        if (stream->client_addrlen == 0) {
            proxy_log(proxy, 0,
                      "h2_proxy: Proxy Protocol enabled but client address is unavailable");
            return -1;
        }
        if (prepare_proxy_protocol(conn, (const struct sockaddr *)&stream->client_addr,
                                   stream->client_addrlen) != 0) {
            proxy_log(proxy, 0,
                      "h2_proxy: failed to build Proxy Protocol v2 header");
            return -1;
        }
    }
    if (conn->connected && flush_proxy_protocol(conn) != 0) return -1;

    nghttp2_nv *nv = calloc(headers->count, sizeof(*nv));
    if (!nv) return -1;
    size_t count = 0;
    for (size_t i = 0; i < headers->count; i++) {
        const xqc_http_header_t *header = &headers->headers[i];
        if (!header->name.iov_base || !header->value.iov_base ||
            is_hop_by_hop(header->name.iov_base, header->name.iov_len,
                          header->value.iov_base, header->value.iov_len))
            continue;
        nv[count].name = header->name.iov_base;
        nv[count].namelen = header->name.iov_len;
        nv[count].value = header->value.iov_base;
        nv[count].valuelen = header->value.iov_len;
        nv[count].flags = NGHTTP2_NV_FLAG_NONE;
        count++;
    }
    nghttp2_data_provider provider;
    memset(&provider, 0, sizeof(provider));
    provider.source.ptr = stream;
    provider.read_callback = request_body_read_callback;
    stream->request_eof = fin != 0;
    int32_t stream_id = nghttp2_submit_request(stream->backend_conn->session, NULL, nv,
                                               count, fin ? NULL : &provider, stream);
    free(nv);
    if (stream_id < 0) return -1;
    stream->backend_stream_id = stream_id;
    return 0;
}

static int
stream_flush_response(h2_proxy_stream_t *stream)
{
    if (!stream || stream->closed || stream->response_fin_sent) return 0;
    size_t body_available = stream->response_body_len - stream->response_body_off;

    if (stream->response_headers_pending && !stream->response_headers_are_trailers) {
        xqc_http_headers_t headers = {
            .headers = stream->response_headers,
            .count = stream->response_header_count,
            .capacity = stream->response_header_count,
        };
        int fin = stream->response_eof && body_available == 0;
        ssize_t sent = xqc_h3_request_send_headers(stream->h3_request, &headers, fin);
        if (sent == -XQC_EAGAIN) return 0;
        if (sent < 0) return -1;
        if (!stream->response_headers_informational) stream->response_started = 1;
        if (fin) stream->response_fin_sent = 1;
        response_headers_clear(stream);
    }

    body_available = stream->response_body_len - stream->response_body_off;
    while (body_available > 0) {
        int fin = stream->response_eof && !stream->response_headers_pending;
        ssize_t sent = xqc_h3_request_send_body(
            stream->h3_request, stream->response_body + stream->response_body_off,
            body_available, fin);
        if (sent == -XQC_EAGAIN) return 0;
        if (sent < 0) return -1;
        if (sent == 0) break;
        stream->response_body_off += (size_t)sent;
        body_available -= (size_t)sent;
        if (fin && body_available == 0) stream->response_fin_sent = 1;
    }
    if (stream->response_body_off == stream->response_body_len) {
        free(stream->response_body);
        stream->response_body = NULL;
        stream->response_body_off = stream->response_body_len = 0;
    }

    if (stream->response_headers_pending && stream->response_headers_are_trailers &&
        stream->response_body_len == 0) {
        xqc_http_headers_t headers = {
            .headers = stream->response_headers,
            .count = stream->response_header_count,
            .capacity = stream->response_header_count,
        };
        ssize_t sent = xqc_h3_request_send_headers(stream->h3_request, &headers,
                                                   stream->response_eof);
        if (sent == -XQC_EAGAIN) return 0;
        if (sent < 0) return -1;
        if (stream->response_eof) stream->response_fin_sent = 1;
        response_headers_clear(stream);
    }

    if (stream->response_eof && !stream->response_fin_sent &&
        !stream->response_headers_pending && stream->response_body_len == 0) {
        ssize_t sent = xqc_h3_request_finish(stream->h3_request);
        if (sent == -XQC_EAGAIN) return 0;
        if (sent < 0) return -1;
        stream->response_fin_sent = 1;
    }
    return 0;
}

h2_proxy_t *
h2_proxy_create(const h2_proxy_config_t *config, const h2_proxy_callbacks_t *callbacks)
{
    if (!config || !callbacks || !callbacks->register_fd || !callbacks->unregister_fd ||
        (config->backend_addr.ss_family != AF_INET &&
         config->backend_addr.ss_family != AF_INET6) ||
        config->backend_addrlen == 0)
        return NULL;
    if (config->backend_tls) return NULL;
    h2_proxy_t *proxy = calloc(1, sizeof(*proxy));
    if (!proxy) return NULL;
    proxy->config = *config;
    proxy->callbacks = *callbacks;
    if (proxy->config.max_connections == 0)
        proxy->config.max_connections = H2_DEFAULT_MAX_CONNECTIONS;
    if (proxy->config.max_streams_per_conn == 0)
        proxy->config.max_streams_per_conn = H2_DEFAULT_MAX_STREAMS;
    if (proxy->config.conn_timeout_sec == 0)
        proxy->config.conn_timeout_sec = H2_DEFAULT_TIMEOUT_SEC;
    if (proxy->config.max_buffered_body == 0)
        proxy->config.max_buffered_body = H2_DEFAULT_MAX_BUFFER;
    return proxy;
}

void
h2_proxy_destroy(h2_proxy_t *proxy)
{
    if (!proxy) return;
    while (proxy->streams)
        stream_destroy(proxy->streams);
    while (proxy->connections)
        backend_destroy(proxy->connections);
    free(proxy);
}

h2_proxy_stream_t *
h2_proxy_handle_request(h2_proxy_t *proxy, xqc_h3_request_t *h3_request,
                        const xqc_http_headers_t *headers, int fin,
                        void *h3_stream_user_data, const struct sockaddr *client_addr,
                        socklen_t client_addrlen)
{
    if (!proxy || !h3_request || !headers) return NULL;
    h2_proxy_stream_t *stream = stream_create(proxy, h3_request, h3_stream_user_data);
    if (!stream) return NULL;

    /* Store client address for Proxy Protocol */
    if (client_addr && client_addrlen <= sizeof(stream->client_addr)) {
        memcpy(&stream->client_addr, client_addr, client_addrlen);
        stream->client_addrlen = client_addrlen;
    }

    h2_backend_conn_t *conn = backend_available(proxy);
    if (!conn) {
        proxy->stats.backend_errors++;
        proxy_log(proxy, 0, "h2_proxy: no backend connection available");
        goto fail;
    }
    stream->backend_conn = conn;
    conn->stream_count++;
    if (submit_request(stream, headers, fin) != 0) goto fail;
    proxy->stats.total_requests++;
    backend_update_interest(conn);
    return stream;

fail:
    send_bad_gateway(stream);
    stream_destroy(stream);
    return NULL;
}

int
h2_proxy_on_h3_body(h2_proxy_stream_t *stream, const uint8_t *data, size_t len, int fin)
{
    if (!stream || stream->closed) return -1;
    if (stream->response_fin_sent) return 0;
    if (!stream->backend_conn) return -1;
    if (buffer_append(&stream->request_body, &stream->request_body_len,
                      &stream->request_body_off, data, len,
                      stream->proxy->config.max_buffered_body) != 0)
        return -1;
    if (fin) stream->request_eof = 1;
    int rc = nghttp2_session_resume_data(stream->backend_conn->session,
                                         stream->backend_stream_id);
    if (rc != 0 && rc != NGHTTP2_ERR_INVALID_ARGUMENT) return -1;
    backend_update_interest(stream->backend_conn);
    return 0;
}

int
h2_proxy_on_h3_writable(h2_proxy_stream_t *stream)
{
    return stream_flush_response(stream);
}

void
h2_proxy_on_h3_close(h2_proxy_stream_t *stream)
{
    if (!stream || stream->closed) return;
    if (stream->backend_conn && stream->backend_stream_id >= 0) {
        h2_backend_conn_t *conn = stream->backend_conn;
        (void)nghttp2_session_set_stream_user_data(conn->session,
                                                   stream->backend_stream_id, NULL);
        nghttp2_submit_rst_stream(conn->session, NGHTTP2_FLAG_NONE,
                                  stream->backend_stream_id, NGHTTP2_CANCEL);
        backend_update_interest(conn);
    }
    stream_destroy(stream);
}

int
h2_proxy_owns_fd(const h2_proxy_t *proxy, int fd, const void *fd_ctx)
{
    if (!proxy || !fd_ctx) return 0;
    for (const h2_backend_conn_t *conn = proxy->connections; conn; conn = conn->next)
        if (conn == fd_ctx) return conn->fd == fd;
    return 0;
}

void
h2_proxy_on_backend_ready(h2_proxy_t *proxy, int fd, void *fd_ctx, int readable,
                          int writable)
{
    if (!h2_proxy_owns_fd(proxy, fd, fd_ctx)) return;
    h2_backend_conn_t *conn = fd_ctx;
    conn->last_active = wall_time_sec();
    if (writable && !conn->connected) {
        int error = 0;
        socklen_t error_len = sizeof(error);
        if (getsockopt(fd, SOL_SOCKET, SO_ERROR, &error, &error_len) != 0 || error != 0) {
            backend_fail(conn);
            return;
        }
        conn->connected = 1;
    }
    if (conn->connected && conn->proxy_protocol_len > conn->proxy_protocol_off) {
        if (flush_proxy_protocol(conn) != 0) {
            backend_fail(conn);
            return;
        }
        if (!conn->proxy_protocol_sent) {
            backend_update_interest(conn);
            return;
        }
    }
    if (readable) {
        uint8_t buffer[16384];
        for (;;) {
            ssize_t received = recv(fd, buffer, sizeof(buffer), 0);
            if (received < 0) {
                if (errno == EAGAIN || errno == EWOULDBLOCK) break;
                backend_fail(conn);
                return;
            }
            if (received == 0) {
                backend_fail(conn);
                return;
            }
            proxy->stats.bytes_received += (uint64_t)received;
            ssize_t consumed =
                nghttp2_session_mem_recv(conn->session, buffer, (size_t)received);
            if (consumed != received) {
                backend_fail(conn);
                return;
            }
        }
    }
    if (conn->connected && (writable || readable) &&
        nghttp2_session_send(conn->session) != 0) {
        backend_fail(conn);
        return;
    }
    backend_update_interest(conn);
}

void
h2_proxy_tick(h2_proxy_t *proxy, uint64_t now_sec)
{
    if (!proxy) return;
    uint64_t cutoff = now_sec > proxy->config.conn_timeout_sec
                          ? now_sec - proxy->config.conn_timeout_sec
                          : 0;
    h2_backend_conn_t *conn = proxy->connections;
    while (conn) {
        h2_backend_conn_t *next = conn->next;
        if (conn->stream_count == 0 && conn->last_active < cutoff) backend_destroy(conn);
        conn = next;
    }
}

void
h2_proxy_get_stats(const h2_proxy_t *proxy, h2_proxy_stats_t *stats)
{
    if (proxy && stats) *stats = proxy->stats;
}
