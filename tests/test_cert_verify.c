// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 mp0rta and mqvpn contributors

/*
 * test_cert_verify.c — Tests for cert_verify.c
 *
 * Covers the two things a live handshake cannot reach in CI: which names a
 * certificate is allowed to speak for -- including IP literals, which the
 * dNSName-only check rejected no matter what the server presented -- and the
 * platform trust hook Android installs: its contract, its error propagation,
 * and the fact that identity is enforced with or without it.
 *
 * Certificates are minted here rather than checked in. A fixture with real
 * validity dates expires, and the axis under test is the SAN set, which is
 * cheaper to vary in code than in files.
 */

/* Checks are macros that print and exit(1), not assert(), so they hold in the
 * Release build ctest also runs, where NDEBUG would no-op them. */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include <openssl/asn1.h>
#include <openssl/evp.h>
#include <openssl/mem.h>
#include <openssl/nid.h>
#include <openssl/x509.h>

#include "cert_verify.h"

/* ── Test infrastructure ── */

static int g_tests_run = 0;
static int g_tests_passed = 0;

#define TEST(name)                 \
    static void test_##name(void); \
    static void run_##name(void)   \
    {                              \
        g_tests_run++;             \
        printf("  %-52s ", #name); \
        test_##name();             \
        g_tests_passed++;          \
        printf("PASS\n");          \
    }                              \
    static void test_##name(void)

#define ASSERT_TRUE(cond, what)                                          \
    do {                                                                 \
        if (!(cond)) {                                                   \
            printf("FAIL\n    %s:%d: %s\n", __FILE__, __LINE__, (what)); \
            exit(1);                                                     \
        }                                                                \
    } while (0)

#define ASSERT_EQ(a, b, what)                                                          \
    do {                                                                               \
        long long _a = (long long)(a), _b = (long long)(b);                            \
        if (_a != _b) {                                                                \
            printf("FAIL\n    %s:%d: %s (%lld != %lld)\n", __FILE__, __LINE__, (what), \
                   _a, _b);                                                            \
            exit(1);                                                                   \
        }                                                                              \
    } while (0)

#define ASSERT_CONTAINS(haystack, needle)                                         \
    do {                                                                          \
        if (strstr((haystack), (needle)) == NULL) {                               \
            printf("FAIL\n    %s:%d: \"%s\" does not contain \"%s\"\n", __FILE__, \
                   __LINE__, (haystack), (needle));                               \
            exit(1);                                                              \
        }                                                                         \
    } while (0)

/* ── Certificate minting ── */

/* One DER-encoded certificate plus the X509 it came from, both owned here. */
typedef struct {
    X509 *cert;
    unsigned char *der;
    size_t len;
} test_cert_t;

static EVP_PKEY *g_key;

static EVP_PKEY *
make_key(void)
{
    EVP_PKEY_CTX *ctx = EVP_PKEY_CTX_new_id(EVP_PKEY_EC, NULL);
    EVP_PKEY *key = NULL;
    if (ctx == NULL) return NULL;
    if (EVP_PKEY_keygen_init(ctx) > 0 &&
        EVP_PKEY_CTX_set_ec_paramgen_curve_nid(ctx, NID_X9_62_prime256v1) > 0) {
        if (EVP_PKEY_keygen(ctx, &key) <= 0) key = NULL;
    }
    EVP_PKEY_CTX_free(ctx);
    return key;
}

/*
 * Self-signed leaf carrying the given subjectAltName value in OpenSSL config
 * syntax ("DNS:vpn.example.com", "IP:192.0.2.10,IP:2001:db8::1"). A NULL san
 * omits the extension entirely, which is how the commonName-only case is
 * built.
 */
static void
make_cert(test_cert_t *out, const char *common_name, const char *san)
{
    X509 *cert = X509_new();
    ASSERT_TRUE(cert != NULL, "X509_new failed");

    X509_set_version(cert, 2); /* v3 */
    ASSERT_TRUE(ASN1_INTEGER_set(X509_get_serialNumber(cert), 1) == 1, "serial number");
    ASSERT_TRUE(X509_gmtime_adj(X509_getm_notBefore(cert), -3600) != NULL, "notBefore");
    ASSERT_TRUE(X509_gmtime_adj(X509_getm_notAfter(cert), 3600) != NULL, "notAfter");
    ASSERT_TRUE(X509_set_pubkey(cert, g_key) == 1, "set_pubkey");

    X509_NAME *name = X509_get_subject_name(cert);
    ASSERT_TRUE(X509_NAME_add_entry_by_txt(name, "CN", MBSTRING_ASC,
                                           (const unsigned char *)common_name, -1, -1,
                                           0) == 1,
                "subject CN");
    ASSERT_TRUE(X509_set_issuer_name(cert, name) == 1, "issuer name");

    if (san != NULL) {
        X509_EXTENSION *ext = X509V3_EXT_nconf_nid(NULL, NULL, NID_subject_alt_name, san);
        ASSERT_TRUE(ext != NULL, "subjectAltName encode");
        ASSERT_TRUE(X509_add_ext(cert, ext, -1) == 1, "subjectAltName add");
        X509_EXTENSION_free(ext);
    }

    ASSERT_TRUE(X509_sign(cert, g_key, EVP_sha256()) > 0, "self-sign");

    unsigned char *der = NULL;
    int len = i2d_X509(cert, &der);
    ASSERT_TRUE(len > 0 && der != NULL, "i2d_X509");

    out->cert = cert;
    out->der = der;
    out->len = (size_t)len;
}

static void
free_cert(test_cert_t *c)
{
    OPENSSL_free(c->der);
    X509_free(c->cert);
    c->der = NULL;
    c->cert = NULL;
    c->len = 0;
}

static test_cert_t g_dns; /* SAN: DNS:vpn.example.com */
static test_cert_t g_ip;  /* SAN: IP:192.0.2.10, IP:2001:db8::1 */
static test_cert_t g_cn;  /* no SAN; CN=cn.example.com */

/* ── Trust hook stub ── */

static int g_hook_calls;
static size_t g_hook_chain_len;
static int g_hook_refuses;
static int g_hook_explains;

static int
stub_trust(const unsigned char *certs[], const size_t cert_len[], size_t certs_len,
           char *error, size_t error_len, void *ctx)
{
    (void)certs;
    (void)cert_len;
    (void)ctx;
    g_hook_calls++;
    g_hook_chain_len = certs_len;
    if (g_hook_refuses) {
        if (g_hook_explains) snprintf(error, error_len, "stub says untrusted");
        return -1;
    }
    return 0;
}

/* Install the stub and reset what it recorded. refuses=0 means "trusted". */
static void
hook_install(int refuses)
{
    g_hook_calls = 0;
    g_hook_chain_len = 0;
    g_hook_refuses = refuses;
    g_hook_explains = 1;
    mqvpn_set_cert_trust_check(stub_trust, NULL);
}

/* ── Helpers ── */

static char g_err[256];

static int
verify(const test_cert_t *leaf, const char *hostname)
{
    const unsigned char *certs[1] = {leaf->der};
    const size_t lens[1] = {leaf->len};
    g_err[0] = '\0';
    return mqvpn_verify_cert_chain(certs, lens, 1, hostname, g_err, sizeof(g_err));
}

/* ── Identity: DNS names ── */

TEST(dns_san_exact_match_verifies)
{
    hook_install(0);
    ASSERT_EQ(verify(&g_dns, "vpn.example.com"), 0, g_err);
    ASSERT_EQ(g_hook_calls, 1, "trust hook should be consulted exactly once");
}

TEST(wrong_dns_name_is_rejected_before_the_hook)
{
    hook_install(0);
    ASSERT_EQ(verify(&g_dns, "evil.example.com"), -1, "wrong name must be rejected");
    ASSERT_CONTAINS(g_err, "hostname mismatch");
    /* Identity is checked first: a certificate that could never be accepted
     * does not cost a JNI upcall on Android. */
    ASSERT_EQ(g_hook_calls, 0, "trust hook must not run for a name mismatch");
}

TEST(common_name_is_not_an_identity)
{
    hook_install(0);
    ASSERT_EQ(verify(&g_cn, "cn.example.com"), -1,
              "commonName must not stand in for a SAN");
    ASSERT_EQ(g_hook_calls, 0, "trust hook must not run for a name mismatch");
}

/* ── Identity: IP literals ──
 *
 * The regression these pin: an address routed through the dNSName comparison
 * never reaches the iPAddress SANs, so a server presenting exactly the right
 * IP SAN was rejected and no bare-IP deployment could verify at all. */

TEST(matching_ipv4_san_verifies)
{
    hook_install(0);
    ASSERT_EQ(verify(&g_ip, "192.0.2.10"), 0, g_err);
    ASSERT_EQ(g_hook_calls, 1, "trust hook should be consulted");
}

TEST(matching_ipv6_san_verifies)
{
    hook_install(0);
    ASSERT_EQ(verify(&g_ip, "2001:db8::1"), 0, g_err);
}

TEST(ipv6_is_compared_as_bytes_not_text)
{
    hook_install(0);
    ASSERT_EQ(verify(&g_ip, "2001:0db8:0000:0000:0000:0000:0000:0001"), 0, g_err);
}

TEST(non_matching_ip_is_rejected)
{
    hook_install(0);
    ASSERT_EQ(verify(&g_ip, "192.0.2.11"), -1, "wrong address must be rejected");
    ASSERT_CONTAINS(g_err, "IP address SAN");
    ASSERT_EQ(g_hook_calls, 0, "trust hook must not run for a name mismatch");
}

TEST(ip_literal_does_not_match_a_dns_only_cert)
{
    hook_install(0);
    ASSERT_EQ(verify(&g_dns, "192.0.2.10"), -1, "DNS-only cert must not match an IP");
    ASSERT_CONTAINS(g_err, "IP address SAN");
}

TEST(dns_name_does_not_match_an_ip_only_cert)
{
    hook_install(0);
    ASSERT_EQ(verify(&g_ip, "vpn.example.com"), -1, "IP-only cert must not match a name");
    ASSERT_CONTAINS(g_err, "hostname mismatch");
}

/* ── Trust hook contract ── */

TEST(untrusted_chain_fails_despite_a_matching_name)
{
    hook_install(1);
    ASSERT_EQ(verify(&g_dns, "vpn.example.com"), -1, "untrusted chain must fail");
    ASSERT_EQ(strcmp(g_err, "stub says untrusted"), 0, "hook error not propagated");
}

TEST(silent_refusal_still_produces_an_error_string)
{
    hook_install(1);
    g_hook_explains = 0;
    ASSERT_EQ(verify(&g_dns, "vpn.example.com"), -1, "silent refusal must still fail");
    ASSERT_TRUE(g_err[0] != '\0', "a refusal must leave something in the log");
}

TEST(whole_chain_reaches_the_hook)
{
    /* The platform needs the peer's intermediates to build a path, so they
     * must be forwarded, not just the leaf. (g_ip stands in for an
     * intermediate here; the hook does not inspect the contents.) */
    hook_install(0);
    const unsigned char *certs[2] = {g_dns.der, g_ip.der};
    const size_t lens[2] = {g_dns.len, g_ip.len};
    g_err[0] = '\0';
    ASSERT_EQ(
        mqvpn_verify_cert_chain(certs, lens, 2, "vpn.example.com", g_err, sizeof(g_err)),
        0, g_err);
    ASSERT_EQ(g_hook_chain_len, 2, "hook must see the full chain");
}

/* ── Malformed and missing input ── */

TEST(missing_hostname_fails_closed)
{
    hook_install(0);
    ASSERT_EQ(verify(&g_dns, ""), -1, "empty hostname must fail");
    ASSERT_EQ(verify(&g_dns, NULL), -1, "NULL hostname must fail");
    ASSERT_EQ(g_hook_calls, 0, "trust hook must not run without a hostname");
}

TEST(empty_chain_fails_closed)
{
    hook_install(0);
    const unsigned char *certs[1] = {NULL};
    const size_t lens[1] = {0};
    g_err[0] = '\0';
    ASSERT_EQ(
        mqvpn_verify_cert_chain(certs, lens, 0, "vpn.example.com", g_err, sizeof(g_err)),
        -1, "empty chain must fail");
    ASSERT_EQ(g_hook_calls, 0, "trust hook must not run without a certificate");
}

TEST(der_with_trailing_bytes_is_rejected)
{
    hook_install(0);
    unsigned char padded[8192];
    ASSERT_TRUE(g_dns.len + 1 <= sizeof(padded), "test certificate unexpectedly large");
    memcpy(padded, g_dns.der, g_dns.len);
    padded[g_dns.len] = 0x00;

    const unsigned char *certs[1] = {padded};
    const size_t lens[1] = {g_dns.len + 1};
    g_err[0] = '\0';
    ASSERT_EQ(
        mqvpn_verify_cert_chain(certs, lens, 1, "vpn.example.com", g_err, sizeof(g_err)),
        -1, "trailing bytes must be rejected");
    ASSERT_CONTAINS(g_err, "invalid leaf certificate");
}

/* ── Built-in path (no hook) ── */

TEST(clearing_the_hook_restores_the_builtin_verifier)
{
    /* A self-signed leaf has no path to a trust anchor on any machine, so this
     * is deterministic wherever CI runs it -- and it proves the hook is
     * genuinely optional rather than load-bearing. */
    mqvpn_set_cert_trust_check(NULL, NULL);
    ASSERT_EQ(verify(&g_dns, "vpn.example.com"), -1,
              "self-signed leaf must not verify against the system store");
}

TEST(builtin_verifier_enforces_identity_too)
{
    mqvpn_set_cert_trust_check(NULL, NULL);
    ASSERT_EQ(verify(&g_dns, "evil.example.com"), -1, "wrong name must be rejected");
    /* Reported as a name failure, not swallowed by the chain walk. */
    ASSERT_CONTAINS(g_err, "hostname mismatch");
}

int
main(void)
{
    g_key = make_key();
    ASSERT_TRUE(g_key != NULL, "EC key generation failed");

    make_cert(&g_dns, "test leaf", "DNS:vpn.example.com");
    make_cert(&g_ip, "test leaf", "IP:192.0.2.10,IP:2001:db8::1");
    make_cert(&g_cn, "cn.example.com", NULL);

    printf("test_cert_verify\n");

    run_dns_san_exact_match_verifies();
    run_wrong_dns_name_is_rejected_before_the_hook();
    run_common_name_is_not_an_identity();

    run_matching_ipv4_san_verifies();
    run_matching_ipv6_san_verifies();
    run_ipv6_is_compared_as_bytes_not_text();
    run_non_matching_ip_is_rejected();
    run_ip_literal_does_not_match_a_dns_only_cert();
    run_dns_name_does_not_match_an_ip_only_cert();

    run_untrusted_chain_fails_despite_a_matching_name();
    run_silent_refusal_still_produces_an_error_string();
    run_whole_chain_reaches_the_hook();

    run_missing_hostname_fails_closed();
    run_empty_chain_fails_closed();
    run_der_with_trailing_bytes_is_rejected();

    run_clearing_the_hook_restores_the_builtin_verifier();
    run_builtin_verifier_enforces_identity_too();

    free_cert(&g_dns);
    free_cert(&g_ip);
    free_cert(&g_cn);
    EVP_PKEY_free(g_key);

    printf("%d/%d passed\n", g_tests_passed, g_tests_run);
    return g_tests_passed == g_tests_run ? 0 : 1;
}
