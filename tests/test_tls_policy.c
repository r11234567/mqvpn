// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 mp0rta and mqvpn contributors

#undef NDEBUG
#include <assert.h>
#include <stdio.h>
#include <openssl/ssl.h>

#include "tls_policy.h"

static int
advance_handshake(SSL *ssl)
{
    int rc = SSL_do_handshake(ssl);
    if (rc == 1) return 1;
    int err = SSL_get_error(ssl, rc);
    assert(err == SSL_ERROR_WANT_READ || err == SSL_ERROR_WANT_WRITE);
    return 0;
}

static void
check_negotiation(const char *peer_groups, uint16_t expected_group)
{
    SSL_CTX *client_ctx = SSL_CTX_new(TLS_method());
    SSL_CTX *server_ctx = SSL_CTX_new(TLS_method());
    assert(client_ctx && server_ctx);
    assert(SSL_CTX_set_min_proto_version(client_ctx, TLS1_3_VERSION));
    assert(SSL_CTX_set_min_proto_version(server_ctx, TLS1_3_VERSION));
    assert(SSL_CTX_set1_groups_list(client_ctx, MQVPN_TLS_GROUPS));
    assert(SSL_CTX_set1_groups_list(server_ctx, peer_groups));
    assert(SSL_CTX_use_certificate_file(server_ctx, TEST_CERT_FILE, SSL_FILETYPE_PEM));
    assert(SSL_CTX_use_PrivateKey_file(server_ctx, TEST_KEY_FILE, SSL_FILETYPE_PEM));

    SSL *client = SSL_new(client_ctx);
    SSL *server = SSL_new(server_ctx);
    BIO *client_io = NULL, *server_io = NULL;
    assert(client && server);
    assert(BIO_new_bio_pair(&client_io, 0, &server_io, 0));
    SSL_set_bio(client, client_io, client_io);
    SSL_set_bio(server, server_io, server_io);
    SSL_set_connect_state(client);
    SSL_set_accept_state(server);
    int client_done = 0, server_done = 0;
    for (int i = 0; i < 100 && (!client_done || !server_done); i++) {
        if (!client_done) client_done = advance_handshake(client);
        if (!server_done) server_done = advance_handshake(server);
    }
    assert(client_done && server_done);
    assert(SSL_get_group_id(client) == expected_group);
    assert(SSL_get_group_id(server) == expected_group);
    printf("TLS 1.3: group=%s cipher=%s\n", SSL_get_group_name(expected_group),
           SSL_CIPHER_get_name(SSL_get_current_cipher(client)));
    SSL_free(client);
    SSL_free(server);
    SSL_CTX_free(client_ctx);
    SSL_CTX_free(server_ctx);
}

int
main(void)
{
    check_negotiation(MQVPN_TLS_GROUPS, SSL_GROUP_X25519_MLKEM768);
    check_negotiation("X25519", SSL_GROUP_X25519);
    puts("test_tls_policy: OK");
    return 0;
}
