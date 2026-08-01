// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 mp0rta and mqvpn contributors

/*
 * sni_router.h — SNI-based routing for QUIC Initial packets
 *
 * Inspects QUIC Initial packets, extracts SNI from TLS ClientHello,
 * and routes traffic based on configured SNI patterns.
 */

#ifndef MQVPN_SNI_ROUTER_H
#define MQVPN_SNI_ROUTER_H

#include <stddef.h>
#include <stdint.h>
#include <sys/socket.h>

#ifdef __cplusplus
extern "C" {
#endif

/* SNI inspection result codes */
typedef enum {
    SNI_ROUTE_ACCEPT = 0,   /* SNI matches, continue to xquic processing */
    SNI_ROUTE_FALLBACK = 1, /* SNI doesn't match, fallback to upstream */
    SNI_ROUTE_PENDING = 2,  /* Need more packets (fragmented ClientHello) */
    SNI_ROUTE_ERROR = -1    /* Parse error, drop packet */
} sni_route_result_t;

/* Forward declarations */
typedef struct sni_router_s sni_router_t;
typedef struct sni_connection_state_s sni_connection_state_t;

/* Configuration for SNI router */
typedef struct {
    /* Expected SNI patterns (exact match or wildcard) */
    const char **allowed_snis;
    size_t n_allowed_snis;

    /* Fallback upstream address */
    struct sockaddr_storage fallback_addr;
    socklen_t fallback_addrlen;

    /* Maximum connections to track */
    uint32_t max_tracked_conns;

    /* Connection timeout (seconds) */
    uint32_t conn_timeout_sec;
} sni_router_config_t;

/*
 * Create a new SNI router instance.
 * Returns NULL on allocation failure.
 */
sni_router_t *sni_router_create(const sni_router_config_t *config);

/*
 * Destroy SNI router and free all resources.
 */
void sni_router_destroy(sni_router_t *router);

/*
 * Inspect incoming packet and determine routing decision.
 *
 * For QUIC Initial packets, this will:
 * 1. Decrypt the Initial packet using Initial Secrets
 * 2. Extract SNI from TLS ClientHello in CRYPTO frame
 * 3. Check if SNI matches allowed patterns
 * 4. Track connection state for subsequent packets
 *
 * For non-Initial packets, uses connection tracking to determine route.
 *
 * Parameters:
 *   router      - SNI router instance
 *   pkt         - Incoming packet data
 *   len         - Packet length
 *   peer        - Peer address
 *   peer_len    - Peer address length
 *   sni_out     - Output buffer for extracted SNI (can be NULL)
 *   sni_cap     - Size of sni_out buffer
 *
 * Returns:
 *   SNI_ROUTE_ACCEPT    - Continue processing with xquic
 *   SNI_ROUTE_FALLBACK  - Forward to fallback upstream
 *   SNI_ROUTE_PENDING   - Need more packets (unlikely for well-formed Initial)
 *   SNI_ROUTE_ERROR     - Drop packet
 */
sni_route_result_t sni_router_inspect(sni_router_t *router, const uint8_t *pkt,
                                      size_t len, const struct sockaddr *peer,
                                      socklen_t peer_len, char *sni_out, size_t sni_cap);

/*
 * Forward packet to fallback upstream.
 *
 * This is a simple UDP forwarding mechanism. For production use with
 * source IP preservation, consider using:
 * - PROXY protocol v2 (if upstream supports it)
 * - IP-in-IP tunneling
 * - eBPF/XDP-based transparent forwarding
 *
 * Returns 0 on success, -1 on error.
 */
int sni_router_fallback(sni_router_t *router, const uint8_t *pkt, size_t len,
                        const struct sockaddr *peer, socklen_t peer_len);

/*
 * Periodic maintenance - clean up expired connection tracking entries.
 * Should be called periodically (e.g., every second).
 */
void sni_router_cleanup(sni_router_t *router, uint64_t now_sec);

/*
 * Check if SNI matches any of the allowed patterns.
 * Supports exact match and wildcard (*.example.com).
 *
 * Returns 1 if matched, 0 otherwise.
 */
int sni_router_match(const sni_router_t *router, const char *sni);

#ifdef __cplusplus
}
#endif

#endif /* MQVPN_SNI_ROUTER_H */
