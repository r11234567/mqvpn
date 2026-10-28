// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 mp0rta and mqvpn contributors

#ifndef MQVPN_CERT_VERIFY_APPLE_H
#define MQVPN_CERT_VERIFY_APPLE_H

#include <stddef.h>
#include <stdint.h>

#include "libmqvpn.h"

int mqvpn_apple_cert_verify(const uint8_t *const certs[], const size_t cert_len[],
                            size_t n_certs, const char *hostname, void *ctx);
int mqvpn_apple_configure_cert_verifier(mqvpn_config_t *config);

#endif
