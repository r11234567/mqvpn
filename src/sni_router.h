// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 mp0rta and mqvpn contributors

#ifndef MQVPN_SNI_ROUTER_H
#define MQVPN_SNI_ROUTER_H

#include <stddef.h>
#include <stdint.h>

#ifdef _WIN32
#  include <winsock2.h>
#  include <ws2tcpip.h>
typedef SOCKET sni_socket_t;
#else
#  include <sys/socket.h>
typedef int sni_socket_t;
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef enum {
    SNI_ROUTE_ACCEPT = 0,
    SNI_ROUTE_FALLBACK = 1,
    SNI_ROUTE_PENDING = 2,
    SNI_ROUTE_ERROR = -1
} sni_route_result_t;

typedef struct sni_router_s sni_router_t;

typedef int (*sni_router_accept_fn)(const uint8_t *pkt, size_t len,
                                    const struct sockaddr *peer, socklen_t peer_len,
                                    void *user_ctx);

typedef struct {
    void (*register_fd)(sni_socket_t fd, void *fd_ctx, void *user_ctx);
    void (*unregister_fd)(sni_socket_t fd, void *user_ctx);
    int (*send_client)(const uint8_t *pkt, size_t len, const struct sockaddr *peer,
                       socklen_t peer_len, void *user_ctx);
    void *user_ctx;
} sni_router_callbacks_t;

typedef struct {
    const char *const *allowed_snis;
    size_t n_allowed_snis;
    struct sockaddr_storage fallback_addr;
    socklen_t fallback_addrlen;
    uint32_t max_tracked_conns;
    uint32_t conn_timeout_sec;
    uint32_t max_client_hello_bytes;
    uint32_t max_pending_packets;
} sni_router_config_t;

sni_router_t *sni_router_create(const sni_router_config_t *config,
                                const sni_router_callbacks_t *callbacks);
void sni_router_destroy(sni_router_t *router);

/* Classify a client datagram and deliver it exactly once. Initial packets are
 * buffered until enough CRYPTO data is available to parse ClientHello. */
sni_route_result_t sni_router_process(sni_router_t *router, const uint8_t *pkt,
                                      size_t len, const struct sockaddr *peer,
                                      socklen_t peer_len, sni_router_accept_fn accept_fn,
                                      void *accept_ctx);

int sni_router_owns_fd(const sni_router_t *router, sni_socket_t fd, void *fd_ctx);
void sni_router_on_fd_readable(sni_router_t *router, sni_socket_t fd, void *fd_ctx);
void sni_router_cleanup(sni_router_t *router, uint64_t now_sec);
int sni_router_match(const sni_router_t *router, const char *sni);

/* Testable RFC boundary: decrypt one QUIC v1/v2 client Initial packet. */
int sni_router_decrypt_initial(const uint8_t *pkt, size_t len, uint8_t *plaintext,
                               size_t plaintext_cap, size_t *plaintext_len,
                               uint64_t *packet_number);

#ifdef __cplusplus
}
#endif

#endif
