// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 mp0rta and mqvpn contributors

#undef NDEBUG

#include "../src/sni_router.h"

#include <arpa/inet.h>
#include <assert.h>
#include <openssl/aead.h>
#include <openssl/aes.h>
#include <stdio.h>
#include <string.h>
#include <sys/time.h>
#include <unistd.h>

static const char RFC9001_CLIENT_INITIAL[] =
    "c000000001088394c8f03e5157080000449e7b9aec34d1b1c98dd7689fb8ec11d242b123dc9bd8bab936"
    "b47d92ec356c"
    "0bab7df5976d27cd449f63300099f3991c260ec4c60d17b31f8429157bb35a1282a643a8d2262cad6750"
    "0cadb8e7378c"
    "8eb7539ec4d4905fed1bee1fc8aafba17c750e2c7ace01e6005f80fcb7df621230c83711b39343fa028c"
    "ea7f7fb5ff89"
    "eac2308249a02252155e2347b63d58c5457afd84d05dfffdb20392844ae812154682e9cf012f9021a6f0"
    "be17ddd0c208"
    "4dce25ff9b06cde535d0f920a2db1bf362c23e596d11a4f5a6cf3948838a3aec4e15daf8500a6ef69ec4"
    "e3feb6b1d98e"
    "610ac8b7ec3faf6ad760b7bad1db4ba3485e8a94dc250ae3fdb41ed15fb6a8e5eba0fc3dd60bc8e30c5c"
    "4287e53805db"
    "059ae0648db2f64264ed5e39be2e20d82df566da8dd5998ccabdae053060ae6c7b4378e846d29f37ed7b"
    "4ea9ec5d82e7"
    "961b7f25a9323851f681d582363aa5f89937f5a67258bf63ad6f1a0b1d96dbd4faddfcefc5266ba66117"
    "22395c906556"
    "be52afe3f565636ad1b17d508b73d8743eeb524be22b3dcbc2c7468d54119c7468449a13d8e3b95811a1"
    "98f3491de3e7"
    "fe942b330407abf82a4ed7c1b311663ac69890f4157015853d91e923037c227a33cdd5ec281ca3f79c44"
    "546b9d90ca00"
    "f064c99e3dd97911d39fe9c5d0b23a229a234cb36186c4819e8b9c5927726632291d6a418211cc2962e2"
    "0fe47feb3edf"
    "330f2c603a9d48c0fcb5699dbfe5896425c5bac4aee82e57a85aaf4e2513e4f05796b07ba2ee47d80506"
    "f8d2c25e50fd"
    "14de71e6c418559302f939b0e1abd576f279c4b2e0feb85c1f28ff18f58891ffef132eef2fa09346aee3"
    "3c28eb130ff2"
    "8f5b766953334113211996d20011a198e3fc433f9f2541010ae17c1bf202580f6047472fb36857fe843b"
    "19f5984009dd"
    "c324044e847a4f4a0ab34f719595de37252d6235365e9b84392b061085349d73203a4a13e96f5432ec0f"
    "d4a1ee65accd"
    "d5e3904df54c1da510b0ff20dcc0c77fcb2c0e0eb605cb0504db87632cf3d8b4dae6e705769d1de35427"
    "0123cb11450e"
    "fc60ac47683d7b8d0f811365565fd98c4c8eb936bcab8d069fc33bd801b03adea2e1fbc5aa463d08ca19"
    "896d2bf59a07"
    "1b851e6c239052172f296bfb5e72404790a2181014f3b94a4e97d117b438130368cc39dbb2d198065ae3"
    "986547926cd2"
    "162f40a29f0c3c8745c0f50fba3852e566d44575c29d39a03f0cda721984b6f440591f355e12d439ff15"
    "0aab7613499d"
    "bd49adabc8676eef023b15b65bfc5ca06948109f23f350db82123535eb8a7433bdabcb909271a6ecbcb5"
    "8b936a88cd4e"
    "8f2e6ff5800175f113253d8fa9ca8885c2f552e657dc603f252e1a8e308f76f0be79e2fb8f5d5fbbe2e3"
    "0ecadd220723"
    "c8c0aea8078cdfcb3868263ff8f0940054da48781893a7e49ad5aff4af300cd804a6b6279ab3ff3afb64"
    "491c85194aab"
    "760d58a606654f9f4400e8b38591356fbf6425aca26dc85244259ff2b19c41b9f96f3ca9ec1dde434da7"
    "d2d392b905dd"
    "f3d1f9af93d1af5950bd493f5aa731b4056df31bd267b6b90a079831aaf579be0a39013137aac6d404f5"
    "18cfd4684064"
    "7e78bfe706ca4cf5e9c5453e9f7cfd2b8b4c8d169a44e55c88d4a9a7f9474241e221af44860018ab0856"
    "972e194cd934";

static const char RFC9369_CLIENT_INITIAL[] =
    "d76b3343cf088394c8f03e5157080000449ea0c95e82ffe67b6abcdb4298b485dd04de806071bf03dcee"
    "bfa162e75d6c"
    "96058bdbfb127cdfcbf903388e99ad049f9a3dd4425ae4d0992cfff18ecf0fdb5a842d09747052f17ac2"
    "053d21f57c5d"
    "250f2c4f0e0202b70785b7946e992e58a59ac52dea6774d4f03b55545243cf1a12834e3f249a78d395e0"
    "d18f4d766004"
    "f1a2674802a747eaa901c3f10cda5500cb9122faa9f1df66c392079a1b40f0de1c6054196a11cbea40af"
    "b6ef5253cd68"
    "18f6625efce3b6def6ba7e4b37a40f7732e093daa7d52190935b8da58976ff3312ae50b187c1433c0f02"
    "8edcc4c2838b"
    "6a9bfc226ca4b4530e7a4ccee1bfa2a3d396ae5a3fb512384b2fdd851f784a65e03f2c4fbe11a53c7777"
    "c023462239dd"
    "6f7521a3f6c7d5dd3ec9b3f233773d4b46d23cc375eb198c63301c21801f6520bcfb7966fc49b393f006"
    "1d974a2706df"
    "8c4a9449f11d7f3d2dcbb90c6b877045636e7c0c0fe4eb0f697545460c806910d2c355f1d253bc9d2452"
    "aaa549e27a1f"
    "ac7cf4ed77f322e8fa894b6a83810a34b361901751a6f5eb65a0326e07de7c1216ccce2d0193f958bb38"
    "50a833f7ae43"
    "2b65bc5a53975c155aa4bcb4f7b2c4e54df16efaf6ddea94e2c50b4cd1dfe06017e0e9d02900cffe1935"
    "e0491d77ffb4"
    "fdf85290fdd893d577b1131a610ef6a5c32b2ee0293617a37cbb08b847741c3b8017c25ca9052ca1079d"
    "8b78aebd4787"
    "6d330a30f6a8c6d61dd1ab5589329de714d19d61370f8149748c72f132f0fc99f34d766c6938597040d8"
    "f9e2bb522ff9"
    "9c63a344d6a2ae8aa8e51b7b90a4a806105fcbca31506c446151adfeceb51b91abfe43960977c87471cf"
    "9ad4074d30e1"
    "0d6a7f03c63bd5d4317f68ff325ba3bd80bf4dc8b52a0ba031758022eb025cdd770b44d6d6cf0670f4e9"
    "90b22347a7db"
    "848265e3e5eb72dfe8299ad7481a408322cac55786e52f633b2fb6b614eaed18d703dd84045a274ae8bf"
    "a73379661388"
    "d6991fe39b0d93debb41700b41f90a15c4d526250235ddcd6776fc77bc97e7a417ebcb31600d01e57f32"
    "162a8560cacc"
    "7e27a096d37a1a86952ec71bd89a3e9a30a2a26162984d7740f81193e8238e61f6b5b984d4d3dfa033c1"
    "bb7e4f0037fe"
    "bf406d91c0dccf32acf423cfa1e7071010d3f270121b493ce85054ef58bada42310138fe081adb04e2bd"
    "901f2f13458b"
    "3d6758158197107c14ebb193230cd1157380aa79cae1374a7c1e5bbcb80ee23e06ebfde206bfb0fcbc0e"
    "dc4ebec30966"
    "1bdd908d532eb0c6adc38b7ca7331dce8dfce39ab71e7c32d318d136b6100671a1ae6a6600e3899f31f0"
    "eed19e3417d1"
    "34b90c9058f8632c798d4490da4987307cba922d61c39805d072b589bd52fdf1e86215c2d54e6670e073"
    "83a27bbffb5a"
    "ddf47d66aa85a0c6f9f32e59d85a44dd5d3b22dc2be80919b490437ae4f36a0ae55edf1d0b5cb4e9a3ec"
    "abee93dfc6e3"
    "8d209d0fa6536d27a5d6fbb17641cde27525d61093f1b28072d111b2b4ae5f89d5974ee12e5cf7d5da4d"
    "6a31123041f3"
    "3e61407e76cffcdcfd7e19ba58cf4b536f4c4938ae79324dc402894b44faf8afbab35282ab659d13c93f"
    "70412e85cb19"
    "9a37ddec600545473cfb5a05e08d0b209973b2172b4d21fb69745a262ccde96ba18b2faa745b6fe189cf"
    "772a9f84cbfc";

typedef struct {
    int accepted;
    int registered;
    int unregistered;
    int sent_client;
    sni_socket_t registered_fd;
    void *registered_ctx;
} test_ctx_t;

static unsigned
hex_nibble(char c)
{
    if (c >= '0' && c <= '9') return (unsigned)(c - '0');
    if (c >= 'a' && c <= 'f') return (unsigned)(c - 'a' + 10);
    assert(0);
    return 0;
}

static size_t
encode_varint(uint8_t *out, uint64_t value)
{
    if (value < 64) {
        out[0] = (uint8_t)value;
        return 1;
    }
    assert(value < 16384);
    out[0] = (uint8_t)(0x40u | (value >> 8));
    out[1] = (uint8_t)value;
    return 2;
}

static size_t
protect_rfc9001_initial(uint8_t *packet, size_t cap, uint8_t packet_number,
                        const uint8_t *plaintext, size_t plaintext_len)
{
    static const uint8_t dcid[] = {0x83, 0x94, 0xc8, 0xf0, 0x3e, 0x51, 0x57, 0x08};
    static const uint8_t key[] = {0x1f, 0x36, 0x96, 0x13, 0xdd, 0x76, 0xd5, 0x46,
                                  0x77, 0x30, 0xef, 0xcb, 0xe3, 0xb1, 0xa2, 0x2d};
    static const uint8_t iv[] = {0xfa, 0x04, 0x4b, 0x2f, 0x42, 0xa3,
                                 0xfd, 0x3b, 0x46, 0xfb, 0x25, 0x5c};
    static const uint8_t hp[] = {0x9f, 0x50, 0x44, 0x9e, 0x04, 0xa0, 0xe8, 0x10,
                                 0x28, 0x3a, 0x1e, 0x99, 0x33, 0xad, 0xed, 0xd2};
    size_t pos = 0;
    assert(cap >= plaintext_len + 64);
    packet[pos++] = 0xc0;
    packet[pos++] = 0;
    packet[pos++] = 0;
    packet[pos++] = 0;
    packet[pos++] = 1;
    packet[pos++] = sizeof(dcid);
    memcpy(packet + pos, dcid, sizeof(dcid));
    pos += sizeof(dcid);
    packet[pos++] = 0;
    packet[pos++] = 0;
    pos += encode_varint(packet + pos, 1 + plaintext_len + 16);
    size_t pn_offset = pos;
    packet[pos++] = packet_number;

    uint8_t nonce[sizeof(iv)];
    memcpy(nonce, iv, sizeof(nonce));
    nonce[sizeof(nonce) - 1] ^= packet_number;
    EVP_AEAD_CTX *aead = EVP_AEAD_CTX_new(EVP_aead_aes_128_gcm(), key, sizeof(key), 16);
    assert(aead != NULL);
    size_t ciphertext_len = 0;
    assert(EVP_AEAD_CTX_seal(aead, packet + pos, &ciphertext_len, cap - pos, nonce,
                             sizeof(nonce), plaintext, plaintext_len, packet, pos) == 1);
    EVP_AEAD_CTX_free(aead);
    size_t packet_len = pos + ciphertext_len;
    assert(pn_offset + 4 + 16 <= packet_len);

    AES_KEY aes;
    uint8_t mask[16];
    assert(AES_set_encrypt_key(hp, 128, &aes) == 0);
    AES_encrypt(packet + pn_offset + 4, mask, &aes);
    packet[0] ^= mask[0] & 0x0f;
    packet[pn_offset] ^= mask[1];
    return packet_len;
}

static size_t
build_crypto_initial(uint8_t *packet, size_t cap, uint8_t packet_number,
                     uint64_t crypto_offset, const uint8_t *crypto, size_t crypto_len)
{
    uint8_t plaintext[512];
    size_t pos = 0;
    plaintext[pos++] = 0x06;
    pos += encode_varint(plaintext + pos, crypto_offset);
    pos += encode_varint(plaintext + pos, crypto_len);
    memcpy(plaintext + pos, crypto, crypto_len);
    pos += crypto_len;
    memset(plaintext + pos, 0, 24);
    pos += 24;
    return protect_rfc9001_initial(packet, cap, packet_number, plaintext, pos);
}

static size_t
decode_hex(const char *hex, uint8_t *out, size_t cap)
{
    size_t chars = strlen(hex);
    assert((chars & 1u) == 0 && chars / 2 <= cap);
    for (size_t i = 0; i < chars / 2; i++)
        out[i] = (uint8_t)((hex_nibble(hex[2 * i]) << 4) | hex_nibble(hex[2 * i + 1]));
    return chars / 2;
}

static void
register_fd(sni_socket_t fd, void *fd_ctx, void *user_ctx)
{
    (void)fd;
    test_ctx_t *ctx = user_ctx;
    ctx->registered++;
    ctx->registered_fd = fd;
    ctx->registered_ctx = fd_ctx;
}

static void
unregister_fd(sni_socket_t fd, void *user_ctx)
{
    (void)fd;
    ((test_ctx_t *)user_ctx)->unregistered++;
}

static int
send_client(const uint8_t *pkt, size_t len, const struct sockaddr *peer,
            socklen_t peer_len, void *user_ctx)
{
    (void)pkt;
    (void)len;
    (void)peer;
    (void)peer_len;
    ((test_ctx_t *)user_ctx)->sent_client++;
    return 0;
}

static int
accept_packet(const uint8_t *pkt, size_t len, const struct sockaddr *peer,
              socklen_t peer_len, void *user_ctx)
{
    (void)pkt;
    (void)len;
    (void)peer;
    (void)peer_len;
    ((test_ctx_t *)user_ctx)->accepted++;
    return 0;
}

static sni_router_t *
create_router_at(const char *const *allowed, size_t allowed_count, test_ctx_t *ctx,
                 uint16_t fallback_port)
{
    struct sockaddr_in fallback = {0};
    fallback.sin_family = AF_INET;
    fallback.sin_port = fallback_port;
    assert(inet_pton(AF_INET, "127.0.0.1", &fallback.sin_addr) == 1);

    sni_router_config_t config = {0};
    config.allowed_snis = allowed;
    config.n_allowed_snis = allowed_count;
    memcpy(&config.fallback_addr, &fallback, sizeof(fallback));
    config.fallback_addrlen = sizeof(fallback);

    sni_router_callbacks_t callbacks = {
        .register_fd = register_fd,
        .unregister_fd = unregister_fd,
        .send_client = send_client,
        .user_ctx = ctx,
    };
    return sni_router_create(&config, &callbacks);
}

static sni_router_t *
create_router(const char *const *allowed, size_t allowed_count, test_ctx_t *ctx)
{
    return create_router_at(allowed, allowed_count, ctx, htons(443));
}

static struct sockaddr_in
test_peer(void)
{
    struct sockaddr_in peer = {0};
    peer.sin_family = AF_INET;
    peer.sin_port = htons(12345);
    assert(inet_pton(AF_INET, "192.0.2.1", &peer.sin_addr) == 1);
    return peer;
}

static void
test_rfc9001_vector(void)
{
    uint8_t packet[1200], plaintext[1200];
    size_t packet_len = decode_hex(RFC9001_CLIENT_INITIAL, packet, sizeof(packet));
    size_t plaintext_len = 0;
    uint64_t packet_number = 0;
    static const uint8_t expected_prefix[] = {0x06, 0x00, 0x40, 0xf1, 0x01, 0x00,
                                              0x00, 0xed, 0x03, 0x03, 0xeb, 0xf8};

    assert(packet_len == 1200);
    assert(sni_router_decrypt_initial(packet, packet_len, plaintext, sizeof(plaintext),
                                      &plaintext_len, &packet_number) == 0);
    assert(packet_number == 2);
    assert(plaintext_len == 1162);
    assert(memcmp(plaintext, expected_prefix, sizeof(expected_prefix)) == 0);

    for (size_t len = 0; len < packet_len; len++)
        assert(sni_router_decrypt_initial(packet, len, plaintext, sizeof(plaintext),
                                          &plaintext_len, &packet_number) != 0);
    packet[packet_len - 1] ^= 1;
    assert(sni_router_decrypt_initial(packet, packet_len, plaintext, sizeof(plaintext),
                                      &plaintext_len, &packet_number) != 0);
}

static void
test_rfc9369_vector(void)
{
    uint8_t packet[1200], plaintext[1200];
    size_t packet_len = decode_hex(RFC9369_CLIENT_INITIAL, packet, sizeof(packet));
    size_t plaintext_len = 0;
    uint64_t packet_number = 0;
    static const uint8_t expected_prefix[] = {0x06, 0x00, 0x40, 0xf1, 0x01, 0x00,
                                              0x00, 0xed, 0x03, 0x03, 0xeb, 0xf8};
    assert(packet_len == 1200);
    assert(sni_router_decrypt_initial(packet, packet_len, plaintext, sizeof(plaintext),
                                      &plaintext_len, &packet_number) == 0);
    assert(packet_number == 2);
    assert(plaintext_len == 1162);
    assert(memcmp(plaintext, expected_prefix, sizeof(expected_prefix)) == 0);
}

static void
test_sni_routing(void)
{
    uint8_t packet[1200];
    size_t packet_len = decode_hex(RFC9001_CLIENT_INITIAL, packet, sizeof(packet));
    struct sockaddr_in peer = test_peer();

    const char *accepted_snis[] = {"EXAMPLE.COM"};
    test_ctx_t accept_ctx = {0};
    sni_router_t *router = create_router(accepted_snis, 1, &accept_ctx);
    assert(router != NULL);
    assert(sni_router_process(router, packet, packet_len, (struct sockaddr *)&peer,
                              sizeof(peer), accept_packet,
                              &accept_ctx) == SNI_ROUTE_ACCEPT);
    assert(accept_ctx.accepted == 1);
    sni_router_destroy(router);

    const char *fallback_snis[] = {"vpn.example.com"};
    test_ctx_t fallback_ctx = {0};
    router = create_router(fallback_snis, 1, &fallback_ctx);
    assert(router != NULL);
    assert(sni_router_process(router, packet, packet_len, (struct sockaddr *)&peer,
                              sizeof(peer), accept_packet,
                              &fallback_ctx) == SNI_ROUTE_FALLBACK);
    assert(fallback_ctx.accepted == 0);
    assert(fallback_ctx.registered == 1);
    assert(sni_router_owns_fd(router, 123, &fallback_ctx) == 0);
    sni_router_destroy(router);
    assert(fallback_ctx.unregistered == 1);
}

static void
test_fail_open_delivers_once(void)
{
    uint8_t packet[1200];
    size_t packet_len = decode_hex(RFC9001_CLIENT_INITIAL, packet, sizeof(packet));
    packet[packet_len - 1] ^= 1;
    struct sockaddr_in peer = test_peer();
    const char *allowed[] = {"example.com"};
    test_ctx_t ctx = {0};
    sni_router_t *router = create_router(allowed, 1, &ctx);
    assert(router != NULL);
    assert(sni_router_process(router, packet, packet_len, (struct sockaddr *)&peer,
                              sizeof(peer), accept_packet, &ctx) == SNI_ROUTE_ACCEPT);
    assert(ctx.accepted == 1);
    sni_router_destroy(router);
}

static void
test_fallback_bidirectional(void)
{
    int backend = socket(AF_INET, SOCK_DGRAM, 0);
    assert(backend >= 0);
    struct sockaddr_in backend_addr = {0};
    backend_addr.sin_family = AF_INET;
    backend_addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    assert(bind(backend, (struct sockaddr *)&backend_addr, sizeof(backend_addr)) == 0);
    struct timeval timeout = {.tv_sec = 2};
    assert(setsockopt(backend, SOL_SOCKET, SO_RCVTIMEO, &timeout, sizeof(timeout)) == 0);
    socklen_t backend_addr_len = sizeof(backend_addr);
    assert(getsockname(backend, (struct sockaddr *)&backend_addr, &backend_addr_len) ==
           0);

    uint8_t packet[1200];
    size_t packet_len = decode_hex(RFC9001_CLIENT_INITIAL, packet, sizeof(packet));
    struct sockaddr_in peer = test_peer();
    const char *allowed[] = {"vpn.example.com"};
    test_ctx_t ctx = {0};
    sni_router_t *router = create_router_at(allowed, 1, &ctx, backend_addr.sin_port);
    assert(router != NULL);
    assert(sni_router_process(router, packet, packet_len, (struct sockaddr *)&peer,
                              sizeof(peer), accept_packet, &ctx) == SNI_ROUTE_FALLBACK);

    uint8_t received[1200];
    struct sockaddr_storage router_addr;
    socklen_t router_addr_len = sizeof(router_addr);
    ssize_t received_len = recvfrom(backend, received, sizeof(received), 0,
                                    (struct sockaddr *)&router_addr, &router_addr_len);
    assert(received_len == (ssize_t)packet_len);
    assert(memcmp(received, packet, packet_len) == 0);
    static const uint8_t reply[] = {1, 2, 3, 4};
    assert(sendto(backend, reply, sizeof(reply), 0, (struct sockaddr *)&router_addr,
                  router_addr_len) == (ssize_t)sizeof(reply));
    assert(sni_router_owns_fd(router, ctx.registered_fd, ctx.registered_ctx) == 1);
    sni_router_on_fd_readable(router, ctx.registered_fd, ctx.registered_ctx);
    assert(ctx.sent_client == 1);

    sni_router_destroy(router);
    close(backend);
}

static void
test_fragmented_client_hello_out_of_order(void)
{
    uint8_t rfc_packet[1200], plaintext[1200];
    size_t rfc_len = decode_hex(RFC9001_CLIENT_INITIAL, rfc_packet, sizeof(rfc_packet));
    size_t plaintext_len = 0;
    uint64_t packet_number = 0;
    assert(sni_router_decrypt_initial(rfc_packet, rfc_len, plaintext, sizeof(plaintext),
                                      &plaintext_len, &packet_number) == 0);
    assert(plaintext[0] == 0x06 && plaintext[1] == 0 && plaintext[2] == 0x40 &&
           plaintext[3] == 0xf1);
    const uint8_t *client_hello = plaintext + 4;
    const size_t client_hello_len = 241;
    const size_t split = 120;

    uint8_t later[512], earlier[512];
    size_t later_len = build_crypto_initial(
        later, sizeof(later), 3, split, client_hello + split, client_hello_len - split);
    size_t earlier_len =
        build_crypto_initial(earlier, sizeof(earlier), 4, 0, client_hello, split);
    struct sockaddr_in peer = test_peer();
    const char *allowed[] = {"example.com"};
    test_ctx_t ctx = {0};
    sni_router_t *router = create_router(allowed, 1, &ctx);
    assert(router != NULL);
    assert(sni_router_process(router, later, later_len, (struct sockaddr *)&peer,
                              sizeof(peer), accept_packet, &ctx) == SNI_ROUTE_PENDING);
    assert(ctx.accepted == 0);
    assert(sni_router_process(router, earlier, earlier_len, (struct sockaddr *)&peer,
                              sizeof(peer), accept_packet, &ctx) == SNI_ROUTE_ACCEPT);
    assert(ctx.accepted == 2);
    sni_router_destroy(router);
}

static void
test_pending_timeout_fails_open(void)
{
    uint8_t rfc_packet[1200], plaintext[1200], fragment[512];
    size_t rfc_len = decode_hex(RFC9001_CLIENT_INITIAL, rfc_packet, sizeof(rfc_packet));
    size_t plaintext_len = 0;
    uint64_t packet_number = 0;
    assert(sni_router_decrypt_initial(rfc_packet, rfc_len, plaintext, sizeof(plaintext),
                                      &plaintext_len, &packet_number) == 0);
    size_t fragment_len = build_crypto_initial(fragment, sizeof(fragment), 5, 120,
                                               plaintext + 4 + 120, 241 - 120);
    struct sockaddr_in peer = test_peer();
    const char *allowed[] = {"example.com"};
    test_ctx_t ctx = {0};
    sni_router_t *router = create_router(allowed, 1, &ctx);
    assert(router != NULL);
    assert(sni_router_process(router, fragment, fragment_len, (struct sockaddr *)&peer,
                              sizeof(peer), accept_packet, &ctx) == SNI_ROUTE_PENDING);
    assert(ctx.accepted == 0);
    sni_router_cleanup(router, UINT64_MAX);
    assert(ctx.accepted == 1);
    sni_router_destroy(router);
}

static void
test_sni_patterns(void)
{
    const char *allowed[] = {"vpn.example.com", "*.internal.example"};
    test_ctx_t ctx = {0};
    sni_router_t *router = create_router(allowed, 2, &ctx);
    assert(router != NULL);
    assert(sni_router_match(router, "VPN.EXAMPLE.COM") == 1);
    assert(sni_router_match(router, "api.internal.example") == 1);
    assert(sni_router_match(router, "dev.api.internal.example") == 0);
    assert(sni_router_match(router, "internal.example") == 0);
    sni_router_destroy(router);

    const char *invalid[] = {"bad.*.example"};
    assert(create_router(invalid, 1, &ctx) == NULL);
}

int
main(void)
{
    test_rfc9001_vector();
    test_rfc9369_vector();
    test_sni_routing();
    test_fail_open_delivers_once();
    test_fallback_bidirectional();
    test_fragmented_client_hello_out_of_order();
    test_pending_timeout_fails_open();
    test_sni_patterns();
    puts("All SNI router tests passed");
    return 0;
}
