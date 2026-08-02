// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 mp0rta and mqvpn contributors

#undef NDEBUG

#include "platform/linux/server_socket_policy.h"

#include <arpa/inet.h>
#include <assert.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/time.h>
#include <unistd.h>

static void
send_byte(int family, uint16_t port, uint8_t value)
{
    int fd = socket(family, SOCK_DGRAM, 0);
    assert(fd >= 0);
    if (family == AF_INET) {
        struct sockaddr_in peer = {0};
        peer.sin_family = AF_INET;
        peer.sin_port = htons(port);
        peer.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
        assert(sendto(fd, &value, sizeof(value), 0, (struct sockaddr *)&peer,
                      sizeof(peer)) == (ssize_t)sizeof(value));
    } else {
        struct sockaddr_in6 peer = {0};
        peer.sin6_family = AF_INET6;
        peer.sin6_port = htons(port);
        peer.sin6_addr = in6addr_loopback;
        assert(sendto(fd, &value, sizeof(value), 0, (struct sockaddr *)&peer,
                      sizeof(peer)) == (ssize_t)sizeof(value));
    }
    close(fd);
}

int
main(void)
{
    assert(mqvpn_linux_server_ipv6_v6only(&in6addr_any) == 0);
    assert(mqvpn_linux_server_ipv6_v6only(&in6addr_loopback) == 1);

    int fd = socket(AF_INET6, SOCK_DGRAM, 0);
    assert(fd >= 0);
    int v6only = mqvpn_linux_server_ipv6_v6only(&in6addr_any);
    assert(setsockopt(fd, IPPROTO_IPV6, IPV6_V6ONLY, &v6only, sizeof(v6only)) == 0);

    struct sockaddr_in6 local = {0};
    local.sin6_family = AF_INET6;
    local.sin6_addr = in6addr_any;
    assert(bind(fd, (struct sockaddr *)&local, sizeof(local)) == 0);
    socklen_t local_len = sizeof(local);
    assert(getsockname(fd, (struct sockaddr *)&local, &local_len) == 0);

    struct timeval timeout = {.tv_sec = 2};
    assert(setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &timeout, sizeof(timeout)) == 0);

    send_byte(AF_INET, ntohs(local.sin6_port), 4);
    send_byte(AF_INET6, ntohs(local.sin6_port), 6);

    int seen_v4 = 0;
    int seen_v6 = 0;
    for (int i = 0; i < 2; i++) {
        uint8_t value = 0;
        struct sockaddr_storage peer;
        socklen_t peer_len = sizeof(peer);
        assert(recvfrom(fd, &value, sizeof(value), 0, (struct sockaddr *)&peer,
                        &peer_len) == (ssize_t)sizeof(value));
        seen_v4 |= value == 4;
        seen_v6 |= value == 6;
    }
    assert(seen_v4 && seen_v6);
    close(fd);

    puts("Linux dual-stack server socket test passed");
    return 0;
}
