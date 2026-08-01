// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 mp0rta and mqvpn contributors

#undef NDEBUG // Ensure assert() is enabled in tests

#include "../src/h2_proxy.h"

#include <arpa/inet.h>
#include <assert.h>
#include <stdio.h>
#include <string.h>

static void
test_log_callback(int level, const char *msg, void *user_ctx)
{
    (void)level;
    (void)user_ctx;
    /* Suppress logs during tests */
    (void)msg;
}

static int
test_register_fd(int fd, void *fd_ctx, void *user_ctx)
{
    (void)fd;
    (void)fd_ctx;
    (void)user_ctx;
    return 0;
}

static void
test_unregister_fd(int fd, void *user_ctx)
{
    (void)fd;
    (void)user_ctx;
}

static void
test_h2_proxy_create_destroy(void)
{
    h2_proxy_config_t config = {0};

    /* Setup backend address */
    struct sockaddr_in *backend = (struct sockaddr_in *)&config.backend_addr;
    backend->sin_family = AF_INET;
    backend->sin_port = htons(8080);
    inet_pton(AF_INET, "127.0.0.1", &backend->sin_addr);
    config.backend_addrlen = sizeof(*backend);

    config.max_connections = 10;
    config.max_streams_per_conn = 100;
    config.conn_timeout_sec = 60;
    config.backend_tls = 0;
    config.enable_connection_reuse = 1;
    strncpy(config.path_prefix, "/", sizeof(config.path_prefix));

    h2_proxy_callbacks_t callbacks = {
        .log = test_log_callback,
        .register_fd = test_register_fd,
        .unregister_fd = test_unregister_fd,
        .user_ctx = NULL,
    };

    h2_proxy_t *proxy = h2_proxy_create(&config, &callbacks);
    assert(proxy != NULL);

    /* Test stats */
    h2_proxy_stats_t stats;
    h2_proxy_get_stats(proxy, &stats);
    assert(stats.total_requests == 0);
    assert(stats.active_streams == 0);
    assert(stats.active_connections == 0);

    h2_proxy_destroy(proxy);

    printf("test_h2_proxy_create_destroy: PASS\n");
}

static void
test_h2_proxy_tick(void)
{
    h2_proxy_config_t config = {0};

    struct sockaddr_in *backend = (struct sockaddr_in *)&config.backend_addr;
    backend->sin_family = AF_INET;
    backend->sin_port = htons(8080);
    inet_pton(AF_INET, "127.0.0.1", &backend->sin_addr);
    config.backend_addrlen = sizeof(*backend);

    config.max_connections = 10;
    config.max_streams_per_conn = 100;
    config.conn_timeout_sec = 1;

    h2_proxy_callbacks_t callbacks = {
        .log = test_log_callback,
        .register_fd = test_register_fd,
        .unregister_fd = test_unregister_fd,
        .user_ctx = NULL,
    };

    h2_proxy_t *proxy = h2_proxy_create(&config, &callbacks);
    assert(proxy != NULL);

    /* Test tick function */
    h2_proxy_tick(proxy, 1000);
    h2_proxy_tick(proxy, 2000);

    h2_proxy_destroy(proxy);

    printf("test_h2_proxy_tick: PASS\n");
}

int
main(void)
{
    test_h2_proxy_create_destroy();
    test_h2_proxy_tick();

    printf("\nAll HTTP/2 proxy tests passed!\n");
    return 0;
}
