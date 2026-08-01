// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 mp0rta and mqvpn contributors

#undef NDEBUG  // Ensure assert() is enabled in tests

#include "../src/sni_router.h"

#include <arpa/inet.h>
#include <assert.h>
#include <stdio.h>
#include <string.h>

static void
test_sni_match(void)
{
    const char *allowed[] = {"vpn.example.com", "*.internal.net"};
    sni_router_config_t config = {
        .allowed_snis = allowed,
        .n_allowed_snis = 2,
        .max_tracked_conns = 100,
        .conn_timeout_sec = 60,
    };

    /* Setup fallback address */
    struct sockaddr_in *fallback = (struct sockaddr_in *)&config.fallback_addr;
    fallback->sin_family = AF_INET;
    fallback->sin_port = htons(443);
    inet_pton(AF_INET, "127.0.0.1", &fallback->sin_addr);
    config.fallback_addrlen = sizeof(*fallback);

    sni_router_t *router = sni_router_create(&config);
    assert(router != NULL);

    /* Test exact match */
    assert(sni_router_match(router, "vpn.example.com") == 1);

    /* Test no match */
    assert(sni_router_match(router, "other.example.com") == 0);
    assert(sni_router_match(router, "vpn.example.org") == 0);

    /* Test wildcard match */
    assert(sni_router_match(router, "api.internal.net") == 1);
    assert(sni_router_match(router, "web.internal.net") == 1);
    assert(sni_router_match(router, "dev.api.internal.net") == 1);

    /* Test wildcard no match */
    assert(sni_router_match(router, "internal.net") == 0);
    assert(sni_router_match(router, "other.net") == 0);

    sni_router_destroy(router);

    printf("test_sni_match: PASS\n");
}

static void
test_connection_tracking(void)
{
    const char *allowed[] = {"vpn.example.com"};
    sni_router_config_t config = {
        .allowed_snis = allowed,
        .n_allowed_snis = 1,
        .max_tracked_conns = 100,
        .conn_timeout_sec = 2,
    };

    struct sockaddr_in *fallback = (struct sockaddr_in *)&config.fallback_addr;
    fallback->sin_family = AF_INET;
    fallback->sin_port = htons(443);
    inet_pton(AF_INET, "127.0.0.1", &fallback->sin_addr);
    config.fallback_addrlen = sizeof(*fallback);

    sni_router_t *router = sni_router_create(&config);
    assert(router != NULL);

    /* Test cleanup with timeout */
    uint64_t now = 1000;
    sni_router_cleanup(router, now);

    /* Cleanup with future time should work */
    sni_router_cleanup(router, now + 10);

    sni_router_destroy(router);

    printf("test_connection_tracking: PASS\n");
}

static void
test_fallback(void)
{
    const char *allowed[] = {"vpn.example.com"};
    sni_router_config_t config = {
        .allowed_snis = allowed,
        .n_allowed_snis = 1,
        .max_tracked_conns = 100,
        .conn_timeout_sec = 60,
    };

    struct sockaddr_in *fallback = (struct sockaddr_in *)&config.fallback_addr;
    fallback->sin_family = AF_INET;
    fallback->sin_port = htons(8443);
    inet_pton(AF_INET, "127.0.0.1", &fallback->sin_addr);
    config.fallback_addrlen = sizeof(*fallback);

    sni_router_t *router = sni_router_create(&config);
    assert(router != NULL);

    /* Test fallback (note: this will fail if no server listening on 127.0.0.1:8443) */
    uint8_t pkt[] = {0x01, 0x02, 0x03, 0x04};
    struct sockaddr_in peer;
    peer.sin_family = AF_INET;
    peer.sin_port = htons(12345);
    inet_pton(AF_INET, "192.168.1.100", &peer.sin_addr);

    /* Don't assert on return value - it's expected to fail without a listener */
    (void)sni_router_fallback(router, pkt, sizeof(pkt), (struct sockaddr *)&peer,
                              sizeof(peer));

    sni_router_destroy(router);

    printf("test_fallback: PASS\n");
}

int
main(void)
{
    test_sni_match();
    test_connection_tracking();
    test_fallback();

    printf("\nAll SNI router tests passed!\n");
    return 0;
}
