// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 mp0rta and mqvpn contributors

#ifndef MQVPN_CERT_VERIFY_H
#define MQVPN_CERT_VERIFY_H

#include <stddef.h>

/* Verify a peer certificate chain delivered by xquic.
 *
 * certs are DER-encoded X.509 certificates, with the leaf first. The
 * platform trust store is used and hostname verification is mandatory.
 * Returns 0 on success and -1 on failure. */
int mqvpn_verify_cert_chain(const unsigned char *certs[], const size_t cert_len[],
                            size_t certs_len, const char *hostname, char *error,
                            size_t error_len);

/* Chain-of-trust check supplied by the host platform.
 *
 * Returns 0 when the chain ends in a trust anchor the platform accepts and
 * non-zero otherwise, writing a reason into error. It receives the DER chain
 * and nothing else: the identity check stays in mqvpn_verify_cert_chain so
 * that every platform enforces the same rules about which names a certificate
 * may speak for. */
typedef int (*mqvpn_cert_trust_fn)(const unsigned char *certs[], const size_t cert_len[],
                                   size_t certs_len, char *error, size_t error_len,
                                   void *ctx);

/* Install fn as the chain-of-trust check in place of the built-in one.
 *
 * Android is why this exists. Its CA store is not at any path BoringSSL's
 * default locations name, so X509_STORE_set_default_paths loads nothing and
 * every chain looks untrusted; since Android 14 the store also lives in an
 * updatable APEX, and user-installed CAs are only reachable through the
 * framework. Asking the platform is the only answer that stays correct. Pass
 * NULL to restore the built-in check.
 *
 * Process-global and unsynchronized: call it once while the library is being
 * initialized and before any connection exists (JNI_OnLoad on Android). */
void mqvpn_set_cert_trust_check(mqvpn_cert_trust_fn fn, void *ctx);

#endif
