// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 mp0rta and mqvpn contributors

#ifdef _WIN32

#  include "cert_verify_windows.h"

#  include <windows.h>
#  include <wincrypt.h>

int
mqvpn_windows_cert_verify(const uint8_t *const certs[], const size_t cert_len[],
                          size_t n_certs, const char *hostname, void *ctx)
{
    (void)ctx;
    if (!certs || !cert_len || n_certs == 0 || !certs[0] || cert_len[0] == 0 ||
        cert_len[0] > MAXDWORD || !hostname || hostname[0] == '\0')
        return -1;

    int result = -1;
    HCERTSTORE intermediates = NULL;
    PCCERT_CONTEXT leaf = NULL;
    PCCERT_CHAIN_CONTEXT chain = NULL;
    wchar_t *server_name = NULL;

    int name_len =
        MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, hostname, -1, NULL, 0);
    if (name_len <= 0) goto cleanup;
    server_name = HeapAlloc(GetProcessHeap(), 0, (size_t)name_len * sizeof(*server_name));
    if (!server_name || MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, hostname, -1,
                                            server_name, name_len) != name_len)
        goto cleanup;

    leaf = CertCreateCertificateContext(X509_ASN_ENCODING | PKCS_7_ASN_ENCODING, certs[0],
                                        (DWORD)cert_len[0]);
    if (!leaf) goto cleanup;
    intermediates =
        CertOpenStore(CERT_STORE_PROV_MEMORY, 0, 0, CERT_STORE_CREATE_NEW_FLAG, NULL);
    if (!intermediates) goto cleanup;

    for (size_t i = 1; i < n_certs; i++) {
        if (!certs[i] || cert_len[i] == 0 || cert_len[i] > MAXDWORD) goto cleanup;
        PCCERT_CONTEXT intermediate = CertCreateCertificateContext(
            X509_ASN_ENCODING | PKCS_7_ASN_ENCODING, certs[i], (DWORD)cert_len[i]);
        if (!intermediate) goto cleanup;
        BOOL added = CertAddCertificateContextToStore(
            intermediates, intermediate, CERT_STORE_ADD_REPLACE_EXISTING, NULL);
        CertFreeCertificateContext(intermediate);
        if (!added) goto cleanup;
    }

    CERT_CHAIN_PARA chain_para = {0};
    chain_para.cbSize = sizeof(chain_para);
    if (!CertGetCertificateChain(NULL, leaf, NULL, intermediates, &chain_para, 0, NULL,
                                 &chain))
        goto cleanup;

    SSL_EXTRA_CERT_CHAIN_POLICY_PARA ssl_policy = {0};
    ssl_policy.cbSize = sizeof(ssl_policy);
    ssl_policy.dwAuthType = AUTHTYPE_SERVER;
    ssl_policy.pwszServerName = server_name;
    CERT_CHAIN_POLICY_PARA policy_para = {0};
    policy_para.cbSize = sizeof(policy_para);
    policy_para.pvExtraPolicyPara = &ssl_policy;
    CERT_CHAIN_POLICY_STATUS policy_status = {0};
    policy_status.cbSize = sizeof(policy_status);
    if (CertVerifyCertificateChainPolicy(CERT_CHAIN_POLICY_SSL, chain, &policy_para,
                                         &policy_status) &&
        policy_status.dwError == 0)
        result = 0;

cleanup:
    if (chain) CertFreeCertificateChain(chain);
    if (intermediates) CertCloseStore(intermediates, 0);
    if (leaf) CertFreeCertificateContext(leaf);
    if (server_name) HeapFree(GetProcessHeap(), 0, server_name);
    return result;
}

#endif
