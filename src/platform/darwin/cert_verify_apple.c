// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 mp0rta and mqvpn contributors

#include "cert_verify_apple.h"

#include <CoreFoundation/CoreFoundation.h>
#include <Security/Security.h>
#include <limits.h>

int
mqvpn_apple_cert_verify(const uint8_t *const certs[], const size_t cert_len[],
                        size_t n_certs, const char *hostname, void *ctx)
{
    (void)ctx;
    if (!certs || !cert_len || n_certs == 0 || !hostname || hostname[0] == '\0')
        return -1;

    int result = -1;
    CFMutableArrayRef chain = CFArrayCreateMutable(kCFAllocatorDefault, (CFIndex)n_certs,
                                                   &kCFTypeArrayCallBacks);
    CFStringRef server_name = NULL;
    SecPolicyRef policy = NULL;
    SecTrustRef trust = NULL;
    if (!chain) goto cleanup;

    for (size_t i = 0; i < n_certs; i++) {
        if (!certs[i] || cert_len[i] == 0 || cert_len[i] > (size_t)LONG_MAX) goto cleanup;
        CFDataRef der = CFDataCreate(kCFAllocatorDefault, certs[i], (CFIndex)cert_len[i]);
        if (!der) goto cleanup;
        SecCertificateRef cert = SecCertificateCreateWithData(kCFAllocatorDefault, der);
        CFRelease(der);
        if (!cert) goto cleanup;
        CFArrayAppendValue(chain, cert);
        CFRelease(cert);
    }

    server_name =
        CFStringCreateWithCString(kCFAllocatorDefault, hostname, kCFStringEncodingUTF8);
    if (!server_name) goto cleanup;
    policy = SecPolicyCreateSSL(true, server_name);
    if (!policy || SecTrustCreateWithCertificates(chain, policy, &trust) != errSecSuccess)
        goto cleanup;
    if (SecTrustEvaluateWithError(trust, NULL)) result = 0;

cleanup:
    if (trust) CFRelease(trust);
    if (policy) CFRelease(policy);
    if (server_name) CFRelease(server_name);
    if (chain) CFRelease(chain);
    return result;
}

int
mqvpn_apple_configure_cert_verifier(mqvpn_config_t *config)
{
    return mqvpn_config_set_cert_verifier(config, mqvpn_apple_cert_verify, NULL);
}
