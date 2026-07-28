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
                            size_t certs_len, const char *hostname,
                            char *error, size_t error_len);

#endif
