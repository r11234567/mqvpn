// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 mp0rta and mqvpn contributors

#include "sni_router.h"

#ifdef X509_NAME
/* Windows CryptoAPI collides with BoringSSL's X509_NAME typedef. */
#  undef X509_NAME
#endif

#include <limits.h>
#include <openssl/aead.h>
#include <openssl/aes.h>
#include <openssl/evp.h>
#include <openssl/hkdf.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

#ifdef _WIN32
#  define SNI_INVALID_SOCKET INVALID_SOCKET
#else
#  include <arpa/inet.h>
#  include <errno.h>
#  include <fcntl.h>
#  include <unistd.h>
#  define SNI_INVALID_SOCKET (-1)
#endif

#define QUIC_V1                     0x00000001u
#define QUIC_V2                     0x6b3343cfu
#define SNI_DEFAULT_MAX_CONNS       1024u
#define SNI_DEFAULT_TIMEOUT_SEC     60u
#define SNI_DEFAULT_CH_BYTES        (64u * 1024u)
#define SNI_DEFAULT_PENDING_PACKETS 8u
#define QUIC_AEAD_TAG_LEN           16u
#define QUIC_HP_SAMPLE_LEN          16u
#define QUIC_RETRY_TAG_LEN          16u

static const uint8_t QUIC_V1_INITIAL_SALT[20] = {0x38, 0x76, 0x2c, 0xf7, 0xf5, 0x59, 0x34,
                                                 0xb3, 0x4d, 0x17, 0x9a, 0xe6, 0xa4, 0xc8,
                                                 0x0c, 0xad, 0xcc, 0xbb, 0x7f, 0x0a};
static const uint8_t QUIC_V2_INITIAL_SALT[20] = {0x0d, 0xed, 0xe3, 0xde, 0xf7, 0x00, 0xa6,
                                                 0xdb, 0x81, 0x93, 0x81, 0xbe, 0x6e, 0x26,
                                                 0x9d, 0xcb, 0xf9, 0xbd, 0x2e, 0xd9};
static const uint8_t QUIC_V1_RETRY_KEY[16] = {0xbe, 0x0c, 0x69, 0x0b, 0x9f, 0x66,
                                              0x57, 0x5a, 0x1d, 0x76, 0x6b, 0x54,
                                              0xe3, 0x68, 0xc8, 0x4e};
static const uint8_t QUIC_V1_RETRY_NONCE[12] = {0x46, 0x15, 0x99, 0xd3, 0x5d, 0x63,
                                                0x2b, 0xf2, 0x23, 0x98, 0x25, 0xbb};
static const uint8_t QUIC_V2_RETRY_KEY[16] = {0x8f, 0xb4, 0xb0, 0x1b, 0x56, 0xac,
                                              0x48, 0xe2, 0x60, 0xfb, 0xcb, 0xce,
                                              0xad, 0x7c, 0xcc, 0x92};
static const uint8_t QUIC_V2_RETRY_NONCE[12] = {0xd8, 0x69, 0x69, 0xbc, 0x2d, 0x7c,
                                                0x6d, 0x99, 0x90, 0xef, 0xb0, 0x4a};

typedef struct pending_packet_s {
    uint8_t *data;
    size_t len;
    struct pending_packet_s *next;
} pending_packet_t;

typedef struct sni_connection_state_s {
    struct sockaddr_storage peer_addr;
    socklen_t peer_addrlen;
    uint8_t initial_dcid[20];
    size_t initial_dcid_len;
    uint8_t initial_scid[20];
    size_t initial_scid_len;
    uint8_t retry_scid[20];
    size_t retry_scid_len;
    uint32_t version;
    int have_retry_scid;
    uint64_t largest_initial_pn;
    int have_largest_initial_pn;
    uint64_t last_seen;
    sni_route_result_t decision;

    uint8_t *crypto_data;
    size_t crypto_cap;
    uint8_t *crypto_present;
    size_t crypto_present_cap;
    size_t crypto_contiguous;

    pending_packet_t *pending_head;
    pending_packet_t *pending_tail;
    uint32_t pending_count;
    sni_router_accept_fn accept_fn;
    void *accept_ctx;

    sni_socket_t fallback_fd;
    struct sni_connection_state_s *next;
} sni_connection_state_t;

struct sni_router_s {
    sni_router_config_t config;
    sni_router_callbacks_t callbacks;
    char **allowed_snis;
    sni_connection_state_t **conn_table;
    size_t table_size;
    size_t conn_count;
};

typedef struct {
    uint32_t version;
    uint8_t first_byte;
    uint8_t dcid[20];
    size_t dcid_len;
    uint8_t scid[20];
    size_t scid_len;
    size_t pn_offset;
    size_t packet_end;
} initial_header_t;

typedef struct {
    uint32_t version;
    uint8_t dcid[20];
    size_t dcid_len;
    uint8_t scid[20];
    size_t scid_len;
} retry_header_t;

static int deliver_pending(sni_router_t *router, sni_connection_state_t *conn,
                           sni_router_accept_fn accept_fn, void *accept_ctx);

static uint64_t
wall_time_sec(void)
{
    time_t now = time(NULL);
    return now < 0 ? 0 : (uint64_t)now;
}

static int
ascii_tolower(int c)
{
    return c >= 'A' && c <= 'Z' ? c + ('a' - 'A') : c;
}

static int
ascii_case_equal(const char *a, const char *b)
{
    while (*a && *b) {
        if (ascii_tolower((unsigned char)*a) != ascii_tolower((unsigned char)*b))
            return 0;
        a++;
        b++;
    }
    return *a == *b;
}

static int
valid_dns_name(const char *name)
{
    size_t total = strlen(name);
    if (total == 0 || total > 253 || name[total - 1] == '.') return 0;
    size_t label_len = 0;
    for (size_t i = 0; i < total; i++) {
        unsigned char c = (unsigned char)name[i];
        if (c == '.') {
            if (label_len == 0 || label_len > 63 || name[i - 1] == '-') return 0;
            label_len = 0;
            continue;
        }
        if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
              (c >= '0' && c <= '9') || c == '-'))
            return 0;
        if (label_len == 0 && c == '-') return 0;
        label_len++;
    }
    return label_len > 0 && label_len <= 63 && name[total - 1] != '-';
}

static int
valid_sni_pattern(const char *pattern)
{
    if (!pattern || !pattern[0]) return 0;
    if (pattern[0] == '*' && pattern[1] == '.') return valid_dns_name(pattern + 2);
    return strchr(pattern, '*') == NULL && valid_dns_name(pattern);
}

static uint32_t
hash_bytes(const uint8_t *data, size_t len)
{
    uint32_t h = 2166136261u;
    for (size_t i = 0; i < len; i++) {
        h ^= data[i];
        h *= 16777619u;
    }
    return h;
}

static uint32_t
hash_peer(const struct sockaddr *peer)
{
    if (peer->sa_family == AF_INET) {
        const struct sockaddr_in *p = (const struct sockaddr_in *)peer;
        uint32_t h = hash_bytes((const uint8_t *)&p->sin_addr, sizeof(p->sin_addr));
        return (h ^ p->sin_port) * 16777619u;
    }
    const struct sockaddr_in6 *p = (const struct sockaddr_in6 *)peer;
    uint32_t h = hash_bytes((const uint8_t *)&p->sin6_addr, sizeof(p->sin6_addr));
    return (h ^ p->sin6_port) * 16777619u;
}

static int
addr_len_valid(int family, socklen_t len)
{
    if (family == AF_INET) return len >= (socklen_t)sizeof(struct sockaddr_in);
    if (family == AF_INET6) return len >= (socklen_t)sizeof(struct sockaddr_in6);
    return 0;
}

static int
addr_equal(const struct sockaddr *a, const struct sockaddr *b)
{
    if (a->sa_family != b->sa_family) return 0;
    if (a->sa_family == AF_INET) {
        const struct sockaddr_in *a4 = (const struct sockaddr_in *)a;
        const struct sockaddr_in *b4 = (const struct sockaddr_in *)b;
        return a4->sin_port == b4->sin_port && a4->sin_addr.s_addr == b4->sin_addr.s_addr;
    }
    if (a->sa_family == AF_INET6) {
        const struct sockaddr_in6 *a6 = (const struct sockaddr_in6 *)a;
        const struct sockaddr_in6 *b6 = (const struct sockaddr_in6 *)b;
        return a6->sin6_port == b6->sin6_port &&
               memcmp(&a6->sin6_addr, &b6->sin6_addr, sizeof(a6->sin6_addr)) == 0;
    }
    return 0;
}

static size_t
decode_varint(const uint8_t *data, size_t len, uint64_t *out)
{
    if (!data || !out || len == 0) return 0;
    size_t n = (size_t)1u << (data[0] >> 6);
    if (n > len) return 0;
    uint64_t value = data[0] & 0x3fu;
    for (size_t i = 1; i < n; i++)
        value = (value << 8) | data[i];
    *out = value;
    return n;
}

static int
parse_initial_header(const uint8_t *pkt, size_t len, initial_header_t *out)
{
    if (!pkt || !out || len < 7 || (pkt[0] & 0xc0u) != 0xc0u) return -1;

    uint32_t version = ((uint32_t)pkt[1] << 24) | ((uint32_t)pkt[2] << 16) |
                       ((uint32_t)pkt[3] << 8) | pkt[4];
    if (version != QUIC_V1 && version != QUIC_V2) return 1;

    unsigned packet_type = (pkt[0] >> 4) & 0x03u;
    if ((version == QUIC_V1 && packet_type != 0) ||
        (version == QUIC_V2 && packet_type != 1)) {
        return 1;
    }

    size_t pos = 5;
    size_t dcid_len = pkt[pos++];
    if (dcid_len > sizeof(out->dcid) || dcid_len > len - pos) return -1;
    pos += dcid_len;

    if (pos >= len) return -1;
    size_t scid_len = pkt[pos++];
    if (scid_len > sizeof(out->scid) || scid_len > len - pos) return -1;
    pos += scid_len;

    uint64_t token_len = 0;
    size_t n = decode_varint(pkt + pos, len - pos, &token_len);
    if (n == 0 || token_len > len - pos - n) return -1;
    pos += n + (size_t)token_len;

    uint64_t protected_len = 0;
    n = decode_varint(pkt + pos, len - pos, &protected_len);
    if (n == 0) return -1;
    pos += n;
    if (protected_len > len - pos || protected_len < 1 + QUIC_AEAD_TAG_LEN) return -1;

    memset(out, 0, sizeof(*out));
    out->version = version;
    out->first_byte = pkt[0];
    out->dcid_len = dcid_len;
    memcpy(out->dcid, pkt + 6, dcid_len);
    out->scid_len = scid_len;
    size_t scid_offset = 6 + dcid_len + 1;
    memcpy(out->scid, pkt + scid_offset, scid_len);
    out->pn_offset = pos;
    out->packet_end = pos + (size_t)protected_len;
    return 0;
}

static int
parse_retry_header(const uint8_t *pkt, size_t len, retry_header_t *out)
{
    if (!pkt || !out || len < 5 + 1 + 1 + QUIC_RETRY_TAG_LEN ||
        (pkt[0] & 0xc0u) != 0xc0u) {
        return -1;
    }

    uint32_t version = ((uint32_t)pkt[1] << 24) | ((uint32_t)pkt[2] << 16) |
                       ((uint32_t)pkt[3] << 8) | pkt[4];
    if (version != QUIC_V1 && version != QUIC_V2) return -1;
    unsigned packet_type = (pkt[0] >> 4) & 0x03u;
    if ((version == QUIC_V1 && packet_type != 3) ||
        (version == QUIC_V2 && packet_type != 0)) {
        return -1;
    }

    size_t pos = 5;
    size_t dcid_len = pkt[pos++];
    if (dcid_len > sizeof(out->dcid) || dcid_len > len - pos) return -1;
    const uint8_t *dcid = pkt + pos;
    pos += dcid_len;
    if (pos >= len) return -1;

    size_t scid_len = pkt[pos++];
    if (scid_len > sizeof(out->scid) || scid_len > len - pos ||
        len - pos - scid_len < QUIC_RETRY_TAG_LEN) {
        return -1;
    }

    memset(out, 0, sizeof(*out));
    out->version = version;
    out->dcid_len = dcid_len;
    memcpy(out->dcid, dcid, dcid_len);
    out->scid_len = scid_len;
    memcpy(out->scid, pkt + pos, scid_len);
    return 0;
}

static int
constant_time_equal(const uint8_t *a, const uint8_t *b, size_t len)
{
    uint8_t different = 0;
    for (size_t i = 0; i < len; i++)
        different |= a[i] ^ b[i];
    return different == 0;
}

static int
validate_retry(const sni_connection_state_t *conn, const uint8_t *pkt, size_t len,
               retry_header_t *retry)
{
    if (parse_retry_header(pkt, len, retry) != 0 || retry->version != conn->version ||
        retry->dcid_len != conn->initial_scid_len ||
        memcmp(retry->dcid, conn->initial_scid, retry->dcid_len) != 0) {
        return -1;
    }

    const uint8_t *key;
    const uint8_t *nonce;
    if (retry->version == QUIC_V2) {
        key = QUIC_V2_RETRY_KEY;
        nonce = QUIC_V2_RETRY_NONCE;
    } else {
        key = QUIC_V1_RETRY_KEY;
        nonce = QUIC_V1_RETRY_NONCE;
    }
    size_t retry_without_tag_len = len - QUIC_RETRY_TAG_LEN;
    if (retry_without_tag_len > SIZE_MAX - 1 - conn->initial_dcid_len) return -1;
    size_t pseudo_len = 1 + conn->initial_dcid_len + retry_without_tag_len;
    uint8_t *pseudo = malloc(pseudo_len);
    if (!pseudo) return -1;
    pseudo[0] = (uint8_t)conn->initial_dcid_len;
    memcpy(pseudo + 1, conn->initial_dcid, conn->initial_dcid_len);
    memcpy(pseudo + 1 + conn->initial_dcid_len, pkt, retry_without_tag_len);

    uint8_t calculated[QUIC_RETRY_TAG_LEN];
    size_t calculated_len = 0;
    static const uint8_t empty = 0;
    const EVP_AEAD *algorithm = EVP_aead_aes_128_gcm();
    EVP_AEAD_CTX *aead = EVP_AEAD_CTX_new(algorithm, key, 16, QUIC_RETRY_TAG_LEN);
    int ok = 0;
    if (aead) {
        ok = EVP_AEAD_CTX_seal(aead, calculated, &calculated_len, sizeof(calculated),
                               nonce, 12, &empty, 0, pseudo, pseudo_len) == 1;
        EVP_AEAD_CTX_free(aead);
    }
    free(pseudo);
    if (!ok || calculated_len != QUIC_RETRY_TAG_LEN) return -1;
    return constant_time_equal(calculated, pkt + retry_without_tag_len,
                               QUIC_RETRY_TAG_LEN)
               ? 0
               : -1;
}

static int
hkdf_expand_label(uint8_t *out, size_t out_len, const uint8_t *secret, size_t secret_len,
                  const char *label)
{
    static const char prefix[] = "tls13 ";
    size_t label_len = strlen(label);
    size_t full_len = sizeof(prefix) - 1 + label_len;
    if (out_len > UINT16_MAX || full_len > UINT8_MAX) return -1;

    uint8_t info[2 + 1 + sizeof(prefix) - 1 + 32 + 1];
    if (label_len > 32) return -1;
    size_t pos = 0;
    info[pos++] = (uint8_t)(out_len >> 8);
    info[pos++] = (uint8_t)out_len;
    info[pos++] = (uint8_t)full_len;
    memcpy(info + pos, prefix, sizeof(prefix) - 1);
    pos += sizeof(prefix) - 1;
    memcpy(info + pos, label, label_len);
    pos += label_len;
    info[pos++] = 0;

    return HKDF_expand(out, out_len, EVP_sha256(), secret, secret_len, info, pos) == 1
               ? 0
               : -1;
}

static int
derive_client_keys(uint32_t version, const uint8_t *dcid, size_t dcid_len,
                   uint8_t key[16], uint8_t iv[12], uint8_t hp[16])
{
    const uint8_t *salt =
        version == QUIC_V2 ? QUIC_V2_INITIAL_SALT : QUIC_V1_INITIAL_SALT;
    const char *key_label = version == QUIC_V2 ? "quicv2 key" : "quic key";
    const char *iv_label = version == QUIC_V2 ? "quicv2 iv" : "quic iv";
    const char *hp_label = version == QUIC_V2 ? "quicv2 hp" : "quic hp";
    uint8_t initial_secret[32];
    uint8_t client_secret[32];
    size_t initial_secret_len = 0;

    if (HKDF_extract(initial_secret, &initial_secret_len, EVP_sha256(), dcid, dcid_len,
                     salt, 20) != 1 ||
        initial_secret_len != sizeof(initial_secret) ||
        hkdf_expand_label(client_secret, sizeof(client_secret), initial_secret,
                          sizeof(initial_secret), "client in") != 0 ||
        hkdf_expand_label(key, 16, client_secret, sizeof(client_secret), key_label) !=
            0 ||
        hkdf_expand_label(iv, 12, client_secret, sizeof(client_secret), iv_label) != 0 ||
        hkdf_expand_label(hp, 16, client_secret, sizeof(client_secret), hp_label) != 0) {
        return -1;
    }
    return 0;
}

static uint64_t
decode_packet_number(uint64_t largest, int have_largest, uint64_t truncated,
                     unsigned pn_nbits)
{
    if (!have_largest) return truncated;
    const uint64_t max_packet_number = (UINT64_C(1) << 62) - 1;
    uint64_t expected = largest + 1;
    uint64_t pn_window = UINT64_C(1) << pn_nbits;
    uint64_t pn_half_window = pn_window / 2;
    uint64_t pn_mask = pn_window - 1;
    uint64_t candidate = (expected & ~pn_mask) | truncated;
    if (candidate + pn_half_window <= expected &&
        candidate < max_packet_number - pn_window) {
        candidate += pn_window;
    } else if (candidate > expected && candidate - expected > pn_half_window &&
               candidate >= pn_window) {
        candidate -= pn_window;
    }
    return candidate;
}

static int
decrypt_initial_with_state(const uint8_t *pkt, size_t len, uint8_t *plaintext,
                           size_t plaintext_cap, size_t *plaintext_len,
                           uint64_t *packet_number, uint64_t largest_pn, int have_largest)
{
    initial_header_t h;
    int parsed = parse_initial_header(pkt, len, &h);
    if (parsed != 0) return parsed;
    if (h.pn_offset > h.packet_end || h.pn_offset + 4 + QUIC_HP_SAMPLE_LEN > h.packet_end)
        return -1;

    uint8_t key[16], iv[12], hp[16], mask[16];
    if (derive_client_keys(h.version, h.dcid, h.dcid_len, key, iv, hp) != 0) return -1;

    AES_KEY aes;
    if (AES_set_encrypt_key(hp, 128, &aes) != 0) return -1;
    AES_encrypt(pkt + h.pn_offset + 4, mask, &aes);

    uint8_t first = pkt[0] ^ (mask[0] & 0x0fu);
    size_t pn_len = (first & 0x03u) + 1;
    if (h.pn_offset + pn_len > h.packet_end) return -1;
    size_t ad_len = h.pn_offset + pn_len;
    uint8_t *ad = malloc(ad_len);
    if (!ad) return -1;
    memcpy(ad, pkt, ad_len);
    ad[0] = first;

    uint64_t truncated = 0;
    for (size_t i = 0; i < pn_len; i++) {
        ad[h.pn_offset + i] ^= mask[i + 1];
        truncated = (truncated << 8) | ad[h.pn_offset + i];
    }
    uint64_t pn =
        decode_packet_number(largest_pn, have_largest, truncated, (unsigned)(pn_len * 8));

    uint8_t nonce[12];
    memcpy(nonce, iv, sizeof(nonce));
    for (size_t i = 0; i < 8; i++)
        nonce[sizeof(nonce) - 1 - i] ^= (uint8_t)(pn >> (8 * i));

    size_t ciphertext_len = h.packet_end - ad_len;
    if (ciphertext_len < QUIC_AEAD_TAG_LEN ||
        ciphertext_len - QUIC_AEAD_TAG_LEN > plaintext_cap) {
        free(ad);
        return -1;
    }

    EVP_AEAD_CTX *aead =
        EVP_AEAD_CTX_new(EVP_aead_aes_128_gcm(), key, sizeof(key), QUIC_AEAD_TAG_LEN);
    if (!aead) {
        free(ad);
        return -1;
    }
    size_t out_len = 0;
    int ok = EVP_AEAD_CTX_open(aead, plaintext, &out_len, plaintext_cap, nonce,
                               sizeof(nonce), pkt + ad_len, ciphertext_len, ad, ad_len);
    EVP_AEAD_CTX_free(aead);
    free(ad);
    if (ok != 1) return -1;

    *plaintext_len = out_len;
    *packet_number = pn;
    return 0;
}

int
sni_router_decrypt_initial(const uint8_t *pkt, size_t len, uint8_t *plaintext,
                           size_t plaintext_cap, size_t *plaintext_len,
                           uint64_t *packet_number)
{
    if (!pkt || !plaintext || !plaintext_len || !packet_number) return -1;
    return decrypt_initial_with_state(pkt, len, plaintext, plaintext_cap, plaintext_len,
                                      packet_number, 0, 0);
}

static int
crypto_mark_range(sni_router_t *router, sni_connection_state_t *conn, uint64_t offset,
                  const uint8_t *data, uint64_t data_len)
{
    size_t limit = router->config.max_client_hello_bytes;
    if (offset > limit || data_len > limit - (size_t)offset) return -1;
    size_t end = (size_t)offset + (size_t)data_len;
    if (end > conn->crypto_cap) {
        size_t cap = conn->crypto_cap ? conn->crypto_cap : 1024;
        while (cap < end && cap < limit / 2)
            cap *= 2;
        if (cap < end) cap = end;
        uint8_t *new_data = realloc(conn->crypto_data, cap);
        if (!new_data) return -1;
        conn->crypto_data = new_data;
        conn->crypto_cap = cap;
    }
    if (!conn->crypto_present) {
        conn->crypto_present_cap = (limit + 7) / 8;
        conn->crypto_present = calloc(1, conn->crypto_present_cap);
        if (!conn->crypto_present) return -1;
    }

    memcpy(conn->crypto_data + (size_t)offset, data, (size_t)data_len);
    for (size_t i = (size_t)offset; i < end; i++)
        conn->crypto_present[i / 8] |= (uint8_t)(1u << (i % 8));
    while (conn->crypto_contiguous < limit &&
           (conn->crypto_present[conn->crypto_contiguous / 8] &
            (uint8_t)(1u << (conn->crypto_contiguous % 8)))) {
        conn->crypto_contiguous++;
    }
    return 0;
}

static int
skip_ack_frame(const uint8_t *data, size_t len, size_t *pos, int ecn)
{
    uint64_t range_count = 0, ignored = 0;
    size_t n = decode_varint(data + *pos, len - *pos, &ignored);
    if (!n) return -1;
    *pos += n;
    n = decode_varint(data + *pos, len - *pos, &ignored);
    if (!n) return -1;
    *pos += n;
    n = decode_varint(data + *pos, len - *pos, &range_count);
    if (!n) return -1;
    *pos += n;
    n = decode_varint(data + *pos, len - *pos, &ignored);
    if (!n) return -1;
    *pos += n;
    for (uint64_t i = 0; i < range_count; i++) {
        for (int field = 0; field < 2; field++) {
            n = decode_varint(data + *pos, len - *pos, &ignored);
            if (!n) return -1;
            *pos += n;
        }
    }
    if (ecn) {
        for (int field = 0; field < 3; field++) {
            n = decode_varint(data + *pos, len - *pos, &ignored);
            if (!n) return -1;
            *pos += n;
        }
    }
    return 0;
}

static int
collect_crypto_frames(sni_router_t *router, sni_connection_state_t *conn,
                      const uint8_t *plaintext, size_t len)
{
    size_t pos = 0;
    while (pos < len) {
        uint64_t frame_type = 0;
        size_t n = decode_varint(plaintext + pos, len - pos, &frame_type);
        if (!n) return -1;
        pos += n;
        if (frame_type == 0x00 || frame_type == 0x01) continue;
        if (frame_type == 0x02 || frame_type == 0x03) {
            if (skip_ack_frame(plaintext, len, &pos, frame_type == 0x03) != 0) return -1;
            continue;
        }
        if (frame_type == 0x06) {
            uint64_t offset = 0, crypto_len = 0;
            n = decode_varint(plaintext + pos, len - pos, &offset);
            if (!n) return -1;
            pos += n;
            n = decode_varint(plaintext + pos, len - pos, &crypto_len);
            if (!n || crypto_len > len - pos - n) return -1;
            pos += n;
            if (crypto_mark_range(router, conn, offset, plaintext + pos, crypto_len) != 0)
                return -1;
            pos += (size_t)crypto_len;
            continue;
        }
        if (frame_type == 0x1c || frame_type == 0x1d) return 0;
        return -1;
    }
    return 0;
}

static int
extract_sni(const uint8_t *data, size_t len, char *out, size_t out_cap)
{
    if (len < 4 || data[0] != 0x01) return -1;
    size_t message_len = ((size_t)data[1] << 16) | ((size_t)data[2] << 8) | data[3];
    if (message_len > len - 4 || message_len < 38) return -1;
    size_t end = 4 + message_len;
    size_t pos = 4 + 2 + 32;
    if (pos >= end) return -1;
    size_t session_len = data[pos++];
    if (session_len > end - pos) return -1;
    pos += session_len;
    if (end - pos < 2) return -1;
    size_t cipher_len = ((size_t)data[pos] << 8) | data[pos + 1];
    pos += 2;
    if (cipher_len > end - pos) return -1;
    pos += cipher_len;
    if (pos >= end) return -1;
    size_t compression_len = data[pos++];
    if (compression_len > end - pos) return -1;
    pos += compression_len;
    if (end - pos < 2) return -1;
    size_t extensions_len = ((size_t)data[pos] << 8) | data[pos + 1];
    pos += 2;
    if (extensions_len != end - pos) return -1;

    while (end - pos >= 4) {
        uint16_t type = (uint16_t)(((uint16_t)data[pos] << 8) | data[pos + 1]);
        size_t ext_len = ((size_t)data[pos + 2] << 8) | data[pos + 3];
        pos += 4;
        if (ext_len > end - pos) return -1;
        if (type == 0x0000) {
            size_t ext_end = pos + ext_len;
            if (ext_len < 2) return -1;
            size_t list_len = ((size_t)data[pos] << 8) | data[pos + 1];
            pos += 2;
            if (list_len != ext_end - pos) return -1;
            while (ext_end - pos >= 3) {
                uint8_t name_type = data[pos++];
                size_t name_len = ((size_t)data[pos] << 8) | data[pos + 1];
                pos += 2;
                if (name_len > ext_end - pos) return -1;
                if (name_type == 0) {
                    if (name_len == 0 || name_len >= out_cap ||
                        memchr(data + pos, 0, name_len) != NULL)
                        return -1;
                    for (size_t i = 0; i < name_len; i++) {
                        unsigned char c = data[pos + i];
                        if (c <= 0x20 || c >= 0x7f) return -1;
                        out[i] = (char)ascii_tolower(c);
                    }
                    out[name_len] = '\0';
                    return 0;
                }
                pos += name_len;
            }
            return -1;
        }
        pos += ext_len;
    }
    return -1;
}

static int
client_hello_status(const sni_connection_state_t *conn, char *sni, size_t sni_cap)
{
    if (conn->crypto_contiguous < 4) return 0;
    const uint8_t *data = conn->crypto_data;
    size_t message_len = ((size_t)data[1] << 16) | ((size_t)data[2] << 8) | data[3];
    if (message_len > SIZE_MAX - 4 || 4 + message_len > conn->crypto_contiguous) return 0;
    return extract_sni(data, 4 + message_len, sni, sni_cap) == 0 ? 1 : -1;
}

static void
pending_free(sni_connection_state_t *conn)
{
    pending_packet_t *p = conn->pending_head;
    while (p) {
        pending_packet_t *next = p->next;
        free(p->data);
        free(p);
        p = next;
    }
    conn->pending_head = conn->pending_tail = NULL;
    conn->pending_count = 0;
}

static int
pending_append(sni_router_t *router, sni_connection_state_t *conn, const uint8_t *pkt,
               size_t len)
{
    if (conn->pending_count >= router->config.max_pending_packets) return -1;
    pending_packet_t *p = calloc(1, sizeof(*p));
    if (!p) return -1;
    p->data = malloc(len);
    if (!p->data) {
        free(p);
        return -1;
    }
    memcpy(p->data, pkt, len);
    p->len = len;
    if (conn->pending_tail)
        conn->pending_tail->next = p;
    else
        conn->pending_head = p;
    conn->pending_tail = p;
    conn->pending_count++;
    return 0;
}

static void
socket_close(sni_socket_t fd)
{
    if (fd == SNI_INVALID_SOCKET) return;
#ifdef _WIN32
    closesocket(fd);
#else
    close(fd);
#endif
}

static int
socket_set_nonblocking(sni_socket_t fd)
{
#ifdef _WIN32
    u_long enabled = 1;
    return ioctlsocket(fd, FIONBIO, &enabled) == 0 ? 0 : -1;
#else
    int flags = fcntl(fd, F_GETFL, 0);
    return flags >= 0 && fcntl(fd, F_SETFL, flags | O_NONBLOCK) == 0 ? 0 : -1;
#endif
}

static int
fallback_open(sni_router_t *router, sni_connection_state_t *conn)
{
    int family = router->config.fallback_addr.ss_family;
    sni_socket_t fd = socket(family, SOCK_DGRAM, 0);
    if (fd == SNI_INVALID_SOCKET) return -1;
    if (socket_set_nonblocking(fd) != 0 ||
        connect(fd, (struct sockaddr *)&router->config.fallback_addr,
                router->config.fallback_addrlen) != 0) {
#ifdef _WIN32
        if (WSAGetLastError() != WSAEWOULDBLOCK) {
            socket_close(fd);
            return -1;
        }
#else
        if (errno != EINPROGRESS) {
            socket_close(fd);
            return -1;
        }
#endif
    }
    conn->fallback_fd = fd;
    if (router->callbacks.register_fd)
        router->callbacks.register_fd(fd, conn, router->callbacks.user_ctx);
    return 0;
}

static int
fallback_send(sni_router_t *router, sni_connection_state_t *conn, const uint8_t *pkt,
              size_t len)
{
    if (conn->fallback_fd == SNI_INVALID_SOCKET && fallback_open(router, conn) != 0)
        return -1;
#ifdef _WIN32
    if (len > INT_MAX) return -1;
    int n = send(conn->fallback_fd, (const char *)pkt, (int)len, 0);
    return n == (int)len ? 0 : -1;
#else
    ssize_t n = send(conn->fallback_fd, pkt, len, 0);
    return n == (ssize_t)len ? 0 : -1;
#endif
}

static void
connection_free(sni_router_t *router, sni_connection_state_t *conn)
{
    if (conn->fallback_fd != SNI_INVALID_SOCKET) {
        if (router->callbacks.unregister_fd)
            router->callbacks.unregister_fd(conn->fallback_fd,
                                            router->callbacks.user_ctx);
        socket_close(conn->fallback_fd);
    }
    pending_free(conn);
    free(conn->crypto_data);
    free(conn->crypto_present);
    free(conn);
}

static sni_connection_state_t *
connection_lookup(const sni_router_t *router, const struct sockaddr *peer)
{
    size_t idx = hash_peer(peer) % router->table_size;
    for (sni_connection_state_t *c = router->conn_table[idx]; c; c = c->next)
        if (addr_equal((const struct sockaddr *)&c->peer_addr, peer)) return c;
    return NULL;
}

static void
connection_remove(sni_router_t *router, sni_connection_state_t *target)
{
    size_t idx =
        hash_peer((const struct sockaddr *)&target->peer_addr) % router->table_size;
    sni_connection_state_t **pp = &router->conn_table[idx];
    while (*pp && *pp != target)
        pp = &(*pp)->next;
    if (*pp) {
        *pp = target->next;
        router->conn_count--;
        connection_free(router, target);
    }
}

static void
evict_oldest(sni_router_t *router)
{
    sni_connection_state_t *oldest = NULL;
    for (size_t i = 0; i < router->table_size; i++)
        for (sni_connection_state_t *c = router->conn_table[i]; c; c = c->next)
            if (!oldest || c->last_seen < oldest->last_seen) oldest = c;
    if (oldest) {
        if (oldest->decision == SNI_ROUTE_PENDING && oldest->accept_fn) {
            oldest->decision = SNI_ROUTE_ACCEPT;
            (void)deliver_pending(router, oldest, oldest->accept_fn, oldest->accept_ctx);
        }
        connection_remove(router, oldest);
    }
}

static sni_connection_state_t *
connection_create(sni_router_t *router, const struct sockaddr *peer, socklen_t peer_len,
                  const initial_header_t *h)
{
    if (router->conn_count >= router->config.max_tracked_conns) evict_oldest(router);
    sni_connection_state_t *conn = calloc(1, sizeof(*conn));
    if (!conn) return NULL;
    memcpy(&conn->peer_addr, peer, peer_len);
    conn->peer_addrlen = peer_len;
    conn->decision = SNI_ROUTE_PENDING;
    conn->fallback_fd = SNI_INVALID_SOCKET;
    conn->last_seen = wall_time_sec();
    conn->initial_dcid_len = h->dcid_len;
    memcpy(conn->initial_dcid, h->dcid, h->dcid_len);
    conn->initial_scid_len = h->scid_len;
    memcpy(conn->initial_scid, h->scid, h->scid_len);
    conn->version = h->version;
    size_t idx = hash_peer(peer) % router->table_size;
    conn->next = router->conn_table[idx];
    router->conn_table[idx] = conn;
    router->conn_count++;
    return conn;
}

static int
deliver_pending(sni_router_t *router, sni_connection_state_t *conn,
                sni_router_accept_fn accept_fn, void *accept_ctx)
{
    int result = 0;
    for (pending_packet_t *p = conn->pending_head; p; p = p->next) {
        int rc = conn->decision == SNI_ROUTE_ACCEPT
                     ? accept_fn(p->data, p->len, (struct sockaddr *)&conn->peer_addr,
                                 conn->peer_addrlen, accept_ctx)
                     : fallback_send(router, conn, p->data, p->len);
        if (rc != 0) result = -1;
    }
    pending_free(conn);
    free(conn->crypto_data);
    conn->crypto_data = NULL;
    conn->crypto_cap = 0;
    free(conn->crypto_present);
    conn->crypto_present = NULL;
    conn->crypto_present_cap = 0;
    return result;
}

static sni_route_result_t
accept_pending_and_current(sni_router_t *router, sni_connection_state_t *conn,
                           const uint8_t *pkt, size_t len, const struct sockaddr *peer,
                           socklen_t peer_len, int current_is_pending,
                           sni_router_accept_fn accept_fn, void *accept_ctx)
{
    conn->decision = SNI_ROUTE_ACCEPT;
    int result = deliver_pending(router, conn, accept_fn, accept_ctx);
    if (!current_is_pending && accept_fn(pkt, len, peer, peer_len, accept_ctx) != 0)
        result = -1;
    return result == 0 ? SNI_ROUTE_ACCEPT : SNI_ROUTE_ERROR;
}

sni_router_t *
sni_router_create(const sni_router_config_t *config,
                  const sni_router_callbacks_t *callbacks)
{
    if (!config || !callbacks || !config->allowed_snis || config->n_allowed_snis == 0 ||
        !addr_len_valid(config->fallback_addr.ss_family, config->fallback_addrlen) ||
        !callbacks->send_client) {
        return NULL;
    }
    sni_router_t *router = calloc(1, sizeof(*router));
    if (!router) return NULL;
    router->config = *config;
    router->callbacks = *callbacks;
    if (router->config.max_tracked_conns == 0)
        router->config.max_tracked_conns = SNI_DEFAULT_MAX_CONNS;
    if (router->config.conn_timeout_sec == 0)
        router->config.conn_timeout_sec = SNI_DEFAULT_TIMEOUT_SEC;
    if (router->config.max_client_hello_bytes == 0)
        router->config.max_client_hello_bytes = SNI_DEFAULT_CH_BYTES;
    if (router->config.max_pending_packets == 0)
        router->config.max_pending_packets = SNI_DEFAULT_PENDING_PACKETS;
    if (router->config.max_tracked_conns > 65535 ||
        router->config.conn_timeout_sec > 86400 ||
        router->config.max_client_hello_bytes > 1024u * 1024u ||
        router->config.max_pending_packets > 64)
        goto fail;
    router->table_size = router->config.max_tracked_conns;
    router->conn_table = calloc(router->table_size, sizeof(*router->conn_table));
    router->allowed_snis = calloc(config->n_allowed_snis, sizeof(*router->allowed_snis));
    if (!router->conn_table || !router->allowed_snis) goto fail;
    for (size_t i = 0; i < config->n_allowed_snis; i++) {
        if (!valid_sni_pattern(config->allowed_snis[i])) goto fail;
        size_t n = strlen(config->allowed_snis[i]);
        router->allowed_snis[i] = malloc(n + 1);
        if (!router->allowed_snis[i]) goto fail;
        for (size_t j = 0; j < n; j++)
            router->allowed_snis[i][j] =
                (char)ascii_tolower((unsigned char)config->allowed_snis[i][j]);
        router->allowed_snis[i][n] = '\0';
    }
    router->config.allowed_snis = (const char *const *)router->allowed_snis;
    return router;

fail:
    sni_router_destroy(router);
    return NULL;
}

void
sni_router_destroy(sni_router_t *router)
{
    if (!router) return;
    if (router->conn_table) {
        for (size_t i = 0; i < router->table_size; i++) {
            sni_connection_state_t *c = router->conn_table[i];
            while (c) {
                sni_connection_state_t *next = c->next;
                connection_free(router, c);
                c = next;
            }
        }
    }
    if (router->allowed_snis)
        for (size_t i = 0; i < router->config.n_allowed_snis; i++)
            free(router->allowed_snis[i]);
    free(router->allowed_snis);
    free(router->conn_table);
    free(router);
}

int
sni_router_match(const sni_router_t *router, const char *sni)
{
    if (!router || !sni) return 0;
    for (size_t i = 0; i < router->config.n_allowed_snis; i++) {
        const char *pattern = router->allowed_snis[i];
        if (ascii_case_equal(pattern, sni)) return 1;
        if (pattern[0] == '*' && pattern[1] == '.') {
            const char *suffix = pattern + 1;
            size_t sni_len = strlen(sni), suffix_len = strlen(suffix);
            size_t prefix_len = sni_len > suffix_len ? sni_len - suffix_len : 0;
            if (prefix_len > 0 && memchr(sni, '.', prefix_len) == NULL &&
                ascii_case_equal(sni + prefix_len, suffix))
                return 1;
        }
    }
    return 0;
}

sni_route_result_t
sni_router_process(sni_router_t *router, const uint8_t *pkt, size_t len,
                   const struct sockaddr *peer, socklen_t peer_len,
                   sni_router_accept_fn accept_fn, void *accept_ctx)
{
    if (!router || !pkt || !len || !peer || !accept_fn ||
        !addr_len_valid(peer->sa_family, peer_len) ||
        peer_len > (socklen_t)sizeof(struct sockaddr_storage))
        return SNI_ROUTE_ERROR;

    sni_connection_state_t *conn = connection_lookup(router, peer);
    initial_header_t h;
    int parsed = parse_initial_header(pkt, len, &h);
    int retry_initial = 0;
    if (conn && parsed == 0) {
        retry_initial = conn->decision == SNI_ROUTE_FALLBACK &&
                        conn->have_retry_scid && conn->version == h.version &&
                        conn->retry_scid_len == h.dcid_len &&
                        memcmp(conn->retry_scid, h.dcid, h.dcid_len) == 0;
    }
    if (conn && parsed == 0 && !retry_initial &&
        (conn->initial_dcid_len != h.dcid_len ||
         memcmp(conn->initial_dcid, h.dcid, h.dcid_len) != 0)) {
        if (conn->decision == SNI_ROUTE_PENDING) {
            conn->decision = SNI_ROUTE_ACCEPT;
            (void)deliver_pending(router, conn, accept_fn, accept_ctx);
        }
        connection_remove(router, conn);
        conn = NULL;
    }
    if (conn && conn->decision == SNI_ROUTE_ACCEPT) {
        conn->last_seen = wall_time_sec();
        return accept_fn(pkt, len, peer, peer_len, accept_ctx) == 0 ? SNI_ROUTE_ACCEPT
                                                                    : SNI_ROUTE_ERROR;
    }
    if (conn && conn->decision == SNI_ROUTE_FALLBACK) {
        conn->last_seen = wall_time_sec();
        return fallback_send(router, conn, pkt, len) == 0 ? SNI_ROUTE_FALLBACK
                                                          : SNI_ROUTE_ERROR;
    }

    if (parsed != 0) {
        if (conn)
            return accept_pending_and_current(router, conn, pkt, len, peer, peer_len, 0,
                                              accept_fn, accept_ctx);
        return accept_fn(pkt, len, peer, peer_len, accept_ctx) == 0 ? SNI_ROUTE_ACCEPT
                                                                    : SNI_ROUTE_ERROR;
    }
    if (!conn) {
        conn = connection_create(router, peer, peer_len, &h);
        if (!conn) return SNI_ROUTE_ERROR;
    }
    conn->accept_fn = accept_fn;
    conn->accept_ctx = accept_ctx;
    conn->last_seen = wall_time_sec();
    if (pending_append(router, conn, pkt, len) != 0) {
        return accept_pending_and_current(router, conn, pkt, len, peer, peer_len, 0,
                                          accept_fn, accept_ctx);
    }

    uint8_t *plaintext = malloc(len);
    if (!plaintext)
        return accept_pending_and_current(router, conn, pkt, len, peer, peer_len, 1,
                                          accept_fn, accept_ctx);
    size_t plaintext_len = 0;
    uint64_t pn = 0;
    int decrypted = decrypt_initial_with_state(pkt, len, plaintext, len, &plaintext_len,
                                               &pn, conn->largest_initial_pn,
                                               conn->have_largest_initial_pn);
    if (decrypted != 0 ||
        collect_crypto_frames(router, conn, plaintext, plaintext_len) != 0) {
        free(plaintext);
        return accept_pending_and_current(router, conn, pkt, len, peer, peer_len, 1,
                                          accept_fn, accept_ctx);
    }
    free(plaintext);
    if (!conn->have_largest_initial_pn || pn > conn->largest_initial_pn) {
        conn->largest_initial_pn = pn;
        conn->have_largest_initial_pn = 1;
    }

    char sni[256];
    int hello = client_hello_status(conn, sni, sizeof(sni));
    if (hello == 0) return SNI_ROUTE_PENDING;
    conn->decision = hello > 0 && sni_router_match(router, sni) ? SNI_ROUTE_ACCEPT
                                                                : SNI_ROUTE_FALLBACK;
    sni_route_result_t decision = conn->decision;
    return deliver_pending(router, conn, accept_fn, accept_ctx) == 0 ? decision
                                                                     : SNI_ROUTE_ERROR;
}

int
sni_router_owns_fd(const sni_router_t *router, sni_socket_t fd, void *fd_ctx)
{
    if (!router || !fd_ctx) return 0;
    for (size_t i = 0; i < router->table_size; i++) {
        for (const sni_connection_state_t *conn = router->conn_table[i]; conn;
             conn = conn->next) {
            if (conn == fd_ctx) return conn->fallback_fd == fd;
        }
    }
    return 0;
}

void
sni_router_on_fd_readable(sni_router_t *router, sni_socket_t fd, void *fd_ctx)
{
    if (!sni_router_owns_fd(router, fd, fd_ctx)) return;
    sni_connection_state_t *conn = fd_ctx;
    uint8_t buf[65536];
    for (;;) {
#ifdef _WIN32
        int n = recv(fd, (char *)buf, (int)sizeof(buf), 0);
        if (n == SOCKET_ERROR) {
            if (WSAGetLastError() == WSAEWOULDBLOCK) break;
            connection_remove(router, conn);
            return;
        }
#else
        ssize_t n = recv(fd, buf, sizeof(buf), 0);
        if (n < 0) {
            if (errno == EAGAIN || errno == EWOULDBLOCK) break;
            connection_remove(router, conn);
            return;
        }
#endif
        if (n == 0) break;
        conn->last_seen = wall_time_sec();
        if (conn->decision == SNI_ROUTE_FALLBACK && !conn->have_retry_scid) {
            retry_header_t retry;
            if (validate_retry(conn, buf, (size_t)n, &retry) == 0) {
                memcpy(conn->retry_scid, retry.scid, retry.scid_len);
                conn->retry_scid_len = retry.scid_len;
                conn->have_retry_scid = 1;
            }
        }
        if (router->callbacks.send_client(
                buf, (size_t)n, (struct sockaddr *)&conn->peer_addr, conn->peer_addrlen,
                router->callbacks.user_ctx) != 0)
            break;
    }
}

void
sni_router_cleanup(sni_router_t *router, uint64_t now_sec)
{
    if (!router) return;
    uint64_t cutoff = now_sec > router->config.conn_timeout_sec
                          ? now_sec - router->config.conn_timeout_sec
                          : 0;
    for (size_t i = 0; i < router->table_size; i++) {
        sni_connection_state_t **pp = &router->conn_table[i];
        while (*pp) {
            sni_connection_state_t *c = *pp;
            if (c->last_seen < cutoff) {
                if (c->decision == SNI_ROUTE_PENDING && c->accept_fn) {
                    c->decision = SNI_ROUTE_ACCEPT;
                    (void)deliver_pending(router, c, c->accept_fn, c->accept_ctx);
                }
                *pp = c->next;
                router->conn_count--;
                connection_free(router, c);
            } else {
                pp = &c->next;
            }
        }
    }
}
