// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 mp0rta and mqvpn contributors

#ifndef MQVPN_SERVER_SOCKET_POLICY_H
#define MQVPN_SERVER_SOCKET_POLICY_H

#include <netinet/in.h>

/* An explicit IPv6 wildcard is the single-socket dual-stack server mode.
 * Specific IPv6 binds remain IPv6-only so they do not unexpectedly claim the
 * corresponding IPv4 port. */
static inline int
mqvpn_linux_server_ipv6_v6only(const struct in6_addr *address)
{
    return IN6_IS_ADDR_UNSPECIFIED(address) ? 0 : 1;
}

#endif /* MQVPN_SERVER_SOCKET_POLICY_H */
