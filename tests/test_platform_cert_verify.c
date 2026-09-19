// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 mp0rta and mqvpn contributors

#undef NDEBUG

#include <assert.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>

#ifdef _WIN32
#  include "cert_verify_windows.h"
#  define platform_cert_verify mqvpn_windows_cert_verify
#elif defined(__APPLE__)
#  include "cert_verify_apple.h"
#  define platform_cert_verify mqvpn_apple_cert_verify

/* cert_verify_apple.c also exposes the convenience config installer. This
 * isolated adapter test does not link the core library, so provide its only
 * dependency while testing the SecTrust callback itself. */
int
mqvpn_config_set_cert_verifier(mqvpn_config_t *cfg, mqvpn_cert_verify_fn fn, void *ctx)
{
    (void)cfg;
    (void)fn;
    (void)ctx;
    return MQVPN_OK;
}
#else
#  error "platform certificate verifier test requires Windows or Apple"
#endif

static uint8_t *
read_der(size_t *len)
{
    FILE *file = fopen(TEST_CERT_DER_FILE, "rb");
    assert(file != NULL);
    assert(fseek(file, 0, SEEK_END) == 0);
    long size = ftell(file);
    assert(size > 0);
    assert(fseek(file, 0, SEEK_SET) == 0);
    uint8_t *der = malloc((size_t)size);
    assert(der != NULL);
    assert(fread(der, 1, (size_t)size, file) == (size_t)size);
    assert(fclose(file) == 0);
    *len = (size_t)size;
    return der;
}

int
main(void)
{
    assert(platform_cert_verify(NULL, NULL, 0, "mqvpn-test", NULL) != 0);

    size_t der_len = 0;
    uint8_t *der = read_der(&der_len);
    const uint8_t *certs[] = {der};
    const size_t cert_len[] = {der_len};

    /* The fixture is self-signed and is not installed as a system root. Both
     * native adapters must reject it rather than falling back or accepting a
     * syntactically valid chain. */
    assert(platform_cert_verify(certs, cert_len, 1, "mqvpn-test", NULL) != 0);
    assert(platform_cert_verify(certs, cert_len, 1, "", NULL) != 0);

    const uint8_t malformed[] = {0x30, 0x01, 0x00};
    const uint8_t *bad_certs[] = {malformed};
    const size_t bad_len[] = {sizeof(malformed)};
    assert(platform_cert_verify(bad_certs, bad_len, 1, "mqvpn-test", NULL) != 0);

    free(der);
    puts("platform certificate verifier tests passed");
    return 0;
}
