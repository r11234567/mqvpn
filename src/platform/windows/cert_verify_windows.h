// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 mp0rta and mqvpn contributors

#ifndef MQVPN_CERT_VERIFY_WINDOWS_H
#define MQVPN_CERT_VERIFY_WINDOWS_H

#include <stddef.h>
#include <stdint.h>

int mqvpn_windows_cert_verify(const uint8_t *const certs[], const size_t cert_len[],
                              size_t n_certs, const char *hostname, void *ctx);

#endif
