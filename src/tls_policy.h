// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 mp0rta and mqvpn contributors

#ifndef MQVPN_TLS_POLICY_H
#define MQVPN_TLS_POLICY_H

/* One policy for both endpoints. BoringSSL chooses its own TLS 1.3 cipher
 * preference order; xquic's BoringSSL adapter ignores the ciphers string.
 * The groups string is applied and retains classical-peer compatibility. */
#define MQVPN_TLS_CIPHERS \
    "TLS_AES_256_GCM_SHA384:TLS_AES_128_GCM_SHA256:TLS_CHACHA20_POLY1305_SHA256"
#define MQVPN_TLS_GROUPS "X25519MLKEM768:X25519:P-256:P-384:P-521"

#endif
