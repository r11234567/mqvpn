// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 mp0rta and mqvpn contributors

package com.mqvpn.sdk.native_

import java.io.ByteArrayInputStream
import java.security.cert.CertificateFactory
import java.security.cert.X509Certificate
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class PlatformTrustTest {
    private val certificate: X509Certificate by lazy {
        CertificateFactory.getInstance("X.509").generateCertificate(
            ByteArrayInputStream(TEST_CERT.toByteArray(Charsets.US_ASCII)),
        ) as X509Certificate
    }

    @Test
    fun subjectAlternativeNameMatchesEffectiveHostname() {
        assertTrue(PlatformTrust.matchesHostname(certificate, "mqvpn-test"))
        assertTrue(PlatformTrust.matchesHostname(certificate, "MQVPN-TEST."))
        assertFalse(PlatformTrust.matchesHostname(certificate, "vpn.example.com"))
        assertFalse(PlatformTrust.matchesHostname(certificate, "127.0.0.1"))
    }

    companion object {
        private const val TEST_CERT = """-----BEGIN CERTIFICATE-----
MIIBljCCATygAwIBAgIUZIoS95TVLdhobJb+XlwquMLrxK4wCgYIKoZIzj0EAwIw
FTETMBEGA1UEAwwKbXF2cG4tdGVzdDAeFw0yNjA5MDYxODA3MDRaFw0zNjA5MDMx
ODA3MDRaMBUxEzARBgNVBAMMCm1xdnBuLXRlc3QwWTATBgcqhkjOPQIBBggqhkjO
PQMBBwNCAAR2ZofJRLNeB7FFFsZO3p6xDgcOaEDFjbWm/wJnBZflqFnILd7nJiHo
oF4Gqw3vMXNpNFf2jkM6ywIEvGGZvFero2owaDAdBgNVHQ4EFgQUb+BsBhFjuXW7
RybMDUTX1Mv4g+kwHwYDVR0jBBgwFoAUb+BsBhFjuXW7RybMDUTX1Mv4g+kwDwYD
VR0TAQH/BAUwAwEB/zAVBgNVHREEDjAMggptcXZwbi10ZXN0MAoGCCqGSM49BAMC
A0gAMEUCIFA9UuSgMr2m0W2Sdnj2Zt+b547rxvS6QYojx7HssEomAiEAmK1nnKbv
0kgZaJb9R+pOG3Jdja3445YMF3QGXxy4ZpM=
-----END CERTIFICATE-----
"""
    }
}
