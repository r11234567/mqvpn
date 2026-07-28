// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 mp0rta and mqvpn contributors

#include "cert_verify.h"

#include <stdio.h>
#include <string.h>

#include <openssl/x509.h>
#include <openssl/x509_vfy.h>

#ifdef _WIN32
#  include <windows.h>
#  include <wincrypt.h>
#else
#  include <arpa/inet.h>
#endif

static void
set_error(char *error, size_t error_len, const char *message)
{
    if (error != NULL && error_len > 0) {
        snprintf(error, error_len, "%s", message);
    }
}

#ifndef _WIN32

static int
verify_posix(const unsigned char *certs[], const size_t cert_len[], size_t certs_len,
             const char *hostname, char *error, size_t error_len)
{
    X509 *leaf = NULL;
    STACK_OF(X509) *untrusted = NULL;
    X509_STORE *store = NULL;
    X509_STORE_CTX *ctx = NULL;
    int result = -1;

    if (hostname == NULL || hostname[0] == '\0' || certs_len == 0 || certs[0] == NULL ||
        cert_len[0] == 0) {
        set_error(error, error_len, "missing certificate or hostname");
        goto cleanup;
    }

    const unsigned char *p = certs[0];
    leaf = d2i_X509(NULL, &p, cert_len[0]);
    if (leaf == NULL || p != certs[0] + cert_len[0]) {
        set_error(error, error_len, "invalid leaf certificate");
        goto cleanup;
    }

    untrusted = sk_X509_new_null();
    if (untrusted == NULL) {
        set_error(error, error_len, "cannot allocate certificate chain");
        goto cleanup;
    }
    for (size_t i = 1; i < certs_len; ++i) {
        if (certs[i] == NULL || cert_len[i] == 0) {
            set_error(error, error_len, "invalid intermediate certificate");
            goto cleanup;
        }
        p = certs[i];
        X509 *intermediate = d2i_X509(NULL, &p, cert_len[i]);
        if (intermediate == NULL || p != certs[i] + cert_len[i] ||
            sk_X509_push(untrusted, intermediate) == 0) {
            X509_free(intermediate);
            set_error(error, error_len, "invalid intermediate certificate");
            goto cleanup;
        }
    }

    store = X509_STORE_new();
    ctx = X509_STORE_CTX_new();
    if (store == NULL || ctx == NULL || X509_STORE_set_default_paths(store) != 1) {
        set_error(error, error_len, "cannot load system trust store");
        goto cleanup;
    }
    if (X509_STORE_CTX_init(ctx, store, leaf, untrusted) != 1) {
        set_error(error, error_len, "cannot initialize certificate verifier");
        goto cleanup;
    }

    X509_VERIFY_PARAM *param = X509_STORE_CTX_get0_param(ctx);
    if (param == NULL) {
        set_error(error, error_len, "cannot configure certificate hostname");
        goto cleanup;
    }
    X509_VERIFY_PARAM_set_hostflags(param, X509_CHECK_FLAG_NEVER_CHECK_SUBJECT);
    if (X509_VERIFY_PARAM_set1_host(param, hostname, strlen(hostname)) != 1) {
        set_error(error, error_len, "cannot configure certificate hostname");
        goto cleanup;
    }
    if (X509_verify_cert(ctx) != 1) {
        long verify_error = X509_STORE_CTX_get_error(ctx);
        if (error != NULL && error_len > 0) {
            snprintf(error, error_len, "certificate verify failed: %s",
                     X509_verify_cert_error_string(verify_error));
        }
        goto cleanup;
    }
    result = 0;

cleanup:
    X509_STORE_CTX_free(ctx);
    X509_STORE_free(store);
    if (untrusted != NULL) {
        while (sk_X509_num(untrusted) > 0) {
            X509_free(sk_X509_pop(untrusted));
        }
        sk_X509_free(untrusted);
    }
    X509_free(leaf);
    return result;
}

#else

static int
verify_windows(const unsigned char *certs[], const size_t cert_len[], size_t certs_len,
               const char *hostname, char *error, size_t error_len)
{
    HCERTSTORE intermediates = NULL;
    PCCERT_CONTEXT leaf = NULL;
    PCCERT_CHAIN_CONTEXT chain = NULL;
    WCHAR hostname_w[256];
    int result = -1;

    if (hostname == NULL || hostname[0] == '\0' || certs_len == 0 || certs[0] == NULL ||
        cert_len[0] == 0 || MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, hostname, -1,
                                                hostname_w, ARRAYSIZE(hostname_w)) == 0) {
        set_error(error, error_len, "missing or invalid certificate hostname");
        goto cleanup;
    }

    leaf = CertCreateCertificateContext(X509_ASN_ENCODING | PKCS_7_ASN_ENCODING,
                                        certs[0], (DWORD)cert_len[0]);
    if (leaf == NULL) {
        set_error(error, error_len, "invalid leaf certificate");
        goto cleanup;
    }
    intermediates = CertOpenStore(CERT_STORE_PROV_MEMORY, 0, 0,
                                  CERT_STORE_CREATE_NEW_FLAG, NULL);
    if (intermediates == NULL) {
        set_error(error, error_len, "cannot allocate intermediate store");
        goto cleanup;
    }
    for (size_t i = 1; i < certs_len; ++i) {
        PCCERT_CONTEXT intermediate = CertCreateCertificateContext(
            X509_ASN_ENCODING | PKCS_7_ASN_ENCODING, certs[i], (DWORD)cert_len[i]);
        if (intermediate == NULL ||
            !CertAddCertificateContextToStore(intermediates, intermediate,
                                              CERT_STORE_ADD_REPLACE_EXISTING, NULL)) {
            if (intermediate != NULL) CertFreeCertificateContext(intermediate);
            set_error(error, error_len, "invalid intermediate certificate");
            goto cleanup;
        }
        CertFreeCertificateContext(intermediate);
    }

    CERT_CHAIN_PARA chain_para = {0};
    chain_para.cbSize = sizeof(chain_para);
    if (!CertGetCertificateChain(NULL, leaf, NULL, intermediates, &chain_para, 0, NULL,
                                 &chain)) {
        set_error(error, error_len, "Windows certificate chain building failed");
        goto cleanup;
    }

    HTTPSPolicyCallbackData https_policy = {0};
    https_policy.cbStruct = sizeof(https_policy);
    https_policy.dwAuthType = AUTHTYPE_SERVER;
    https_policy.pwszServerName = hostname_w;
    CERT_CHAIN_POLICY_PARA policy_para = {0};
    policy_para.cbSize = sizeof(policy_para);
    policy_para.pvExtraPolicyPara = &https_policy;
    CERT_CHAIN_POLICY_STATUS policy_status = {0};
    policy_status.cbSize = sizeof(policy_status);
    if (!CertVerifyCertificateChainPolicy(CERT_CHAIN_POLICY_SSL, chain, &policy_para,
                                          &policy_status) || policy_status.dwError != 0) {
        set_error(error, error_len, "Windows certificate policy verification failed");
        goto cleanup;
    }
    result = 0;

cleanup:
    if (chain != NULL) CertFreeCertificateChain(chain);
    if (intermediates != NULL) CertCloseStore(intermediates, 0);
    if (leaf != NULL) CertFreeCertificateContext(leaf);
    return result;
}

#endif

int
mqvpn_verify_cert_chain(const unsigned char *certs[], const size_t cert_len[], size_t certs_len,
                        const char *hostname, char *error, size_t error_len)
{
#ifdef _WIN32
    return verify_windows(certs, cert_len, certs_len, hostname, error, error_len);
#else
    return verify_posix(certs, cert_len, certs_len, hostname, error, error_len);
#endif
}
