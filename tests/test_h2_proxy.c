// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 mp0rta and mqvpn contributors

#undef NDEBUG

#include "../src/h2_proxy.h"

#include <arpa/inet.h>
#include <assert.h>
#include <nghttp2/nghttp2.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/time.h>
#include <unistd.h>

typedef struct {
    int fd;
    void *fd_ctx;
    int registered;
    int unregistered;
} test_ctx_t;

typedef struct {
    int headers;
    int status_200;
    uint8_t body[32];
    size_t body_len;
    int fin;
    int closed;
} mock_h3_t;

static mock_h3_t mock_h3;

ssize_t
xqc_h3_request_send_headers(xqc_h3_request_t *request, xqc_http_headers_t *headers,
                            uint8_t fin)
{
    (void)request;
    mock_h3.headers++;
    for (size_t i = 0; i < headers->count; i++) {
        xqc_http_header_t *header = &headers->headers[i];
        if (header->name.iov_len == 7 &&
            memcmp(header->name.iov_base, ":status", 7) == 0 &&
            header->value.iov_len == 3 && memcmp(header->value.iov_base, "200", 3) == 0)
            mock_h3.status_200 = 1;
    }
    if (fin) mock_h3.fin = 1;
    return (ssize_t)headers->count;
}

ssize_t
xqc_h3_request_send_body(xqc_h3_request_t *request, unsigned char *data, size_t data_size,
                         uint8_t fin)
{
    (void)request;
    assert(data_size <= sizeof(mock_h3.body) - mock_h3.body_len);
    memcpy(mock_h3.body + mock_h3.body_len, data, data_size);
    mock_h3.body_len += data_size;
    if (fin) mock_h3.fin = 1;
    return (ssize_t)data_size;
}

ssize_t
xqc_h3_request_finish(xqc_h3_request_t *request)
{
    (void)request;
    mock_h3.fin = 1;
    return 0;
}

xqc_int_t
xqc_h3_request_close(xqc_h3_request_t *request)
{
    (void)request;
    mock_h3.closed++;
    return XQC_OK;
}

typedef struct {
    int fd;
    const uint8_t *body;
    size_t body_len;
    size_t body_off;
    uint8_t request_body[32];
    size_t request_body_len;
} h2_server_ctx_t;

static ssize_t
h2_server_send(nghttp2_session *session, const uint8_t *data, size_t length, int flags,
               void *user_data)
{
    (void)session;
    (void)flags;
    h2_server_ctx_t *ctx = user_data;
    return send(ctx->fd, data, length, 0);
}

static ssize_t
h2_server_read_body(nghttp2_session *session, int32_t stream_id, uint8_t *buffer,
                    size_t length, uint32_t *data_flags, nghttp2_data_source *source,
                    void *user_data)
{
    (void)session;
    (void)stream_id;
    (void)user_data;
    h2_server_ctx_t *ctx = source->ptr;
    size_t remaining = ctx->body_len - ctx->body_off;
    if (length > remaining) length = remaining;
    memcpy(buffer, ctx->body + ctx->body_off, length);
    ctx->body_off += length;
    if (ctx->body_off == ctx->body_len) *data_flags |= NGHTTP2_DATA_FLAG_EOF;
    return (ssize_t)length;
}

static int
h2_server_on_data(nghttp2_session *session, uint8_t flags, int32_t stream_id,
                  const uint8_t *data, size_t len, void *user_data)
{
    (void)session;
    (void)flags;
    (void)stream_id;
    h2_server_ctx_t *ctx = user_data;
    assert(len <= sizeof(ctx->request_body) - ctx->request_body_len);
    memcpy(ctx->request_body + ctx->request_body_len, data, len);
    ctx->request_body_len += len;
    return 0;
}

static void
test_log(int level, const char *message, void *user_ctx)
{
    (void)level;
    (void)message;
    (void)user_ctx;
}

static void
test_register_fd(int fd, int want_read, int want_write, void *fd_ctx, void *user_ctx)
{
    (void)want_read;
    (void)want_write;
    test_ctx_t *ctx = user_ctx;
    ctx->fd = fd;
    ctx->fd_ctx = fd_ctx;
    ctx->registered++;
}

static void
test_unregister_fd(int fd, void *user_ctx)
{
    test_ctx_t *ctx = user_ctx;
    assert(fd == ctx->fd);
    ctx->unregistered++;
}

static h2_proxy_config_t
proxy_config(const struct sockaddr_in *backend)
{
    h2_proxy_config_t config = {0};
    memcpy(&config.backend_addr, backend, sizeof(*backend));
    config.backend_addrlen = sizeof(*backend);
    config.max_connections = 4;
    config.max_streams_per_conn = 16;
    config.conn_timeout_sec = 10;
    config.max_buffered_body = 65536;
    return config;
}

static h2_proxy_callbacks_t
proxy_callbacks(test_ctx_t *ctx)
{
    h2_proxy_callbacks_t callbacks = {
        .log = test_log,
        .register_fd = test_register_fd,
        .unregister_fd = test_unregister_fd,
        .user_ctx = ctx,
    };
    return callbacks;
}

static int
create_listener(struct sockaddr_in *address)
{
    int fd = socket(AF_INET, SOCK_STREAM, 0);
    assert(fd >= 0);
    memset(address, 0, sizeof(*address));
    address->sin_family = AF_INET;
    address->sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    assert(bind(fd, (struct sockaddr *)address, sizeof(*address)) == 0);
    socklen_t address_len = sizeof(*address);
    assert(getsockname(fd, (struct sockaddr *)address, &address_len) == 0);
    assert(listen(fd, 1) == 0);
    return fd;
}

static void
test_create_validation(void)
{
    struct sockaddr_in backend = {0};
    backend.sin_family = AF_INET;
    backend.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    backend.sin_port = htons(8080);
    test_ctx_t ctx = {.fd = -1};
    h2_proxy_callbacks_t callbacks = proxy_callbacks(&ctx);
    h2_proxy_config_t config = proxy_config(&backend);

    h2_proxy_t *proxy = h2_proxy_create(&config, &callbacks);
    assert(proxy != NULL);
    memset(&mock_h3, 0, sizeof(mock_h3));
    h2_proxy_stats_t stats;
    h2_proxy_get_stats(proxy, &stats);
    assert(stats.total_requests == 0);
    assert(h2_proxy_owns_fd(proxy, 123, &ctx) == 0);
    h2_proxy_tick(proxy, 100);
    h2_proxy_destroy(proxy);

    config.backend_tls = 1;
    assert(h2_proxy_create(&config, &callbacks) == NULL);
}

static void
test_h3_headers_submit_h2_request(void)
{
    struct sockaddr_in backend;
    int listener = create_listener(&backend);
    test_ctx_t ctx = {.fd = -1};
    h2_proxy_callbacks_t callbacks = proxy_callbacks(&ctx);
    h2_proxy_config_t config = proxy_config(&backend);
    h2_proxy_t *proxy = h2_proxy_create(&config, &callbacks);
    assert(proxy != NULL);

    xqc_http_header_t fields[] = {
        {.name = {.iov_base = (void *)":method", .iov_len = 7},
         .value = {.iov_base = (void *)"POST", .iov_len = 4}},
        {.name = {.iov_base = (void *)":scheme", .iov_len = 7},
         .value = {.iov_base = (void *)"https", .iov_len = 5}},
        {.name = {.iov_base = (void *)":authority", .iov_len = 10},
         .value = {.iov_base = (void *)"example.com", .iov_len = 11}},
        {.name = {.iov_base = (void *)":path", .iov_len = 5},
         .value = {.iov_base = (void *)"/health", .iov_len = 7}},
        {.name = {.iov_base = (void *)"connection", .iov_len = 10},
         .value = {.iov_base = (void *)"close", .iov_len = 5}},
    };
    xqc_http_headers_t headers = {
        .headers = fields,
        .count = sizeof(fields) / sizeof(fields[0]),
        .capacity = sizeof(fields) / sizeof(fields[0]),
    };
    xqc_h3_request_t *fake_request = (xqc_h3_request_t *)(uintptr_t)1;
    h2_proxy_stream_t *stream =
        h2_proxy_handle_request(proxy, fake_request, &headers, 0, NULL);
    assert(stream != NULL);
    assert(h2_proxy_on_h3_body(stream, (const uint8_t *)"ping", 4, 1) == 0);
    assert(ctx.fd >= 0 && ctx.fd_ctx != NULL && ctx.registered > 0);
    assert(h2_proxy_owns_fd(proxy, ctx.fd, ctx.fd_ctx) == 1);

    int backend_fd = accept(listener, NULL, NULL);
    assert(backend_fd >= 0);
    struct timeval timeout = {.tv_sec = 2};
    assert(setsockopt(backend_fd, SOL_SOCKET, SO_RCVTIMEO, &timeout, sizeof(timeout)) ==
           0);
    h2_proxy_on_backend_ready(proxy, ctx.fd, ctx.fd_ctx, 0, 1);

    uint8_t wire[4096];
    ssize_t wire_len = recv(backend_fd, wire, sizeof(wire), 0);
    static const char preface[] = "PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n";
    assert(wire_len > (ssize_t)(sizeof(preface) - 1));
    assert(memcmp(wire, preface, sizeof(preface) - 1) == 0);

    h2_server_ctx_t server_ctx = {
        .fd = backend_fd,
        .body = (const uint8_t *)"ok",
        .body_len = 2,
    };
    nghttp2_session_callbacks *server_callbacks = NULL;
    assert(nghttp2_session_callbacks_new(&server_callbacks) == 0);
    nghttp2_session_callbacks_set_send_callback(server_callbacks, h2_server_send);
    nghttp2_session_callbacks_set_on_data_chunk_recv_callback(server_callbacks,
                                                              h2_server_on_data);
    nghttp2_session *server_session = NULL;
    assert(nghttp2_session_server_new(&server_session, server_callbacks, &server_ctx) ==
           0);
    nghttp2_session_callbacks_del(server_callbacks);
    assert(nghttp2_submit_settings(server_session, NGHTTP2_FLAG_NONE, NULL, 0) == 0);
    assert(nghttp2_session_mem_recv(server_session, wire, (size_t)wire_len) == wire_len);
    assert(server_ctx.request_body_len == 4 &&
           memcmp(server_ctx.request_body, "ping", 4) == 0);

    nghttp2_nv informational[] = {
        {(uint8_t *)":status", (uint8_t *)"103", 7, 3, NGHTTP2_NV_FLAG_NONE},
    };
    nghttp2_nv response_headers[] = {
        {(uint8_t *)":status", (uint8_t *)"200", 7, 3, NGHTTP2_NV_FLAG_NONE},
        {(uint8_t *)"content-type", (uint8_t *)"text/plain", 12, 10,
         NGHTTP2_NV_FLAG_NONE},
    };
    nghttp2_data_provider provider = {
        .source = {.ptr = &server_ctx},
        .read_callback = h2_server_read_body,
    };
    assert(nghttp2_submit_headers(
               server_session, NGHTTP2_FLAG_NONE, 1, NULL, informational,
               sizeof(informational) / sizeof(informational[0]), NULL) == 0);
    assert(nghttp2_submit_response(server_session, 1, response_headers,
                                   sizeof(response_headers) / sizeof(response_headers[0]),
                                   &provider) == 0);
    assert(nghttp2_session_send(server_session) == 0);
    h2_proxy_on_backend_ready(proxy, ctx.fd, ctx.fd_ctx, 1, 0);
    assert(mock_h3.headers == 2);
    assert(mock_h3.status_200 == 1);
    assert(mock_h3.body_len == 2 && memcmp(mock_h3.body, "ok", 2) == 0);
    assert(mock_h3.fin == 1);

    h2_proxy_stats_t stats;
    h2_proxy_get_stats(proxy, &stats);
    assert(stats.total_requests == 1);
    assert(stats.active_streams == 1);
    assert(stats.bytes_sent > sizeof(preface) - 1);

    h2_proxy_on_h3_close(stream);
    nghttp2_session_del(server_session);
    close(backend_fd);
    close(listener);
    h2_proxy_destroy(proxy);
    assert(ctx.unregistered == 1);
}

static void
test_h3_close_detaches_nghttp2_user_data(void)
{
    struct sockaddr_in backend;
    int listener = create_listener(&backend);
    test_ctx_t ctx = {.fd = -1};
    h2_proxy_callbacks_t callbacks = proxy_callbacks(&ctx);
    h2_proxy_config_t config = proxy_config(&backend);
    h2_proxy_t *proxy = h2_proxy_create(&config, &callbacks);
    assert(proxy != NULL);
    memset(&mock_h3, 0, sizeof(mock_h3));

    xqc_http_header_t fields[] = {
        {.name = {.iov_base = (void *)":method", .iov_len = 7},
         .value = {.iov_base = (void *)"GET", .iov_len = 3}},
        {.name = {.iov_base = (void *)":scheme", .iov_len = 7},
         .value = {.iov_base = (void *)"https", .iov_len = 5}},
        {.name = {.iov_base = (void *)":authority", .iov_len = 10},
         .value = {.iov_base = (void *)"example.com", .iov_len = 11}},
        {.name = {.iov_base = (void *)":path", .iov_len = 5},
         .value = {.iov_base = (void *)"/cancel", .iov_len = 7}},
    };
    xqc_http_headers_t headers = {
        .headers = fields,
        .count = sizeof(fields) / sizeof(fields[0]),
        .capacity = sizeof(fields) / sizeof(fields[0]),
    };
    xqc_h3_request_t *fake_request = (xqc_h3_request_t *)(uintptr_t)2;
    h2_proxy_stream_t *stream =
        h2_proxy_handle_request(proxy, fake_request, &headers, 1, NULL);
    assert(stream != NULL);

    int backend_fd = accept(listener, NULL, NULL);
    assert(backend_fd >= 0);
    struct timeval timeout = {.tv_sec = 2};
    assert(setsockopt(backend_fd, SOL_SOCKET, SO_RCVTIMEO, &timeout, sizeof(timeout)) ==
           0);
    h2_proxy_on_backend_ready(proxy, ctx.fd, ctx.fd_ctx, 0, 1);
    uint8_t wire[4096];
    ssize_t wire_len = recv(backend_fd, wire, sizeof(wire), 0);
    assert(wire_len > 0);

    h2_proxy_on_h3_close(stream);
    h2_proxy_stats_t stats;
    h2_proxy_get_stats(proxy, &stats);
    assert(stats.active_streams == 0);

    h2_server_ctx_t server_ctx = {.fd = backend_fd};
    nghttp2_session_callbacks *server_callbacks = NULL;
    assert(nghttp2_session_callbacks_new(&server_callbacks) == 0);
    nghttp2_session_callbacks_set_send_callback(server_callbacks, h2_server_send);
    nghttp2_session *server_session = NULL;
    assert(nghttp2_session_server_new(&server_session, server_callbacks, &server_ctx) ==
           0);
    nghttp2_session_callbacks_del(server_callbacks);
    assert(nghttp2_submit_settings(server_session, NGHTTP2_FLAG_NONE, NULL, 0) == 0);
    assert(nghttp2_session_mem_recv(server_session, wire, (size_t)wire_len) == wire_len);
    nghttp2_nv response[] = {
        {(uint8_t *)":status", (uint8_t *)"204", 7, 3, NGHTTP2_NV_FLAG_NONE},
    };
    assert(nghttp2_submit_response(server_session, 1, response,
                                   sizeof(response) / sizeof(response[0]), NULL) == 0);
    assert(nghttp2_session_send(server_session) == 0);
    h2_proxy_on_backend_ready(proxy, ctx.fd, ctx.fd_ctx, 1, 0);
    assert(mock_h3.headers == 0 && mock_h3.body_len == 0);

    nghttp2_session_del(server_session);
    close(backend_fd);
    close(listener);
    h2_proxy_destroy(proxy);
    assert(ctx.unregistered == 1);
}

int
main(void)
{
    test_create_validation();
    test_h3_headers_submit_h2_request();
    test_h3_close_detaches_nghttp2_user_data();
    puts("All HTTP/2 proxy tests passed");
    return 0;
}
