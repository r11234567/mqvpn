// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 mp0rta and mqvpn contributors

package com.mqvpn.sdk.native_

import java.io.ByteArrayInputStream
import java.security.KeyStore
import java.security.cert.CertificateFactory
import java.security.cert.X509Certificate
import javax.net.ssl.TrustManagerFactory
import javax.net.ssl.X509TrustManager

/**
 * Chain-of-trust check backed by Android's own CA store.
 *
 * BoringSSL's `X509_STORE_set_default_paths` finds nothing on Android: the CA
 * store sits at none of the paths it compiles in, it moved into an updatable
 * APEX in Android 14, and user-installed CAs are reachable only through the
 * framework. Every server certificate therefore looked untrusted, the
 * handshake failed, and the client reconnected forever. Asking the platform is
 * the only check that stays correct across releases.
 *
 * Identity — hostname or IP — is deliberately *not* checked here.
 * `src/cert_verify.c` does that for every platform, before calling this, so
 * all of them agree on which names a certificate may speak for.
 *
 * Called from native code: `mqvpn_jni.c` resolves [checkServerTrusted] by name
 * in `JNI_OnLoad` and installs it through `mqvpn_set_cert_trust_check`. The
 * class name, method name and signature are part of that contract — see
 * `consumer-rules.pro` for the matching keep rule.
 */
object PlatformTrust {

    /**
     * @param chain DER-encoded certificates as delivered by the TLS stack,
     *   leaf first, peer-supplied intermediates after it.
     * @return `null` when the chain ends in a trust anchor Android accepts, or
     *   a short reason for the log when it does not.
     */
    @JvmStatic
    fun checkServerTrusted(chain: Array<ByteArray>): String? {
        if (chain.isEmpty()) return "empty certificate chain"
        return try {
            val certs = decode(chain)
            val manager = systemTrustManager ?: return "no system X509 trust manager"
            manager.checkServerTrusted(certs, authTypeOf(certs[0]))
            null
        } catch (t: Throwable) {
            // Catch everything. This return value is the only channel back to
            // the caller, and an exception left pending across the JNI
            // boundary is a crash rather than a rejected certificate.
            t.message?.takeIf { it.isNotBlank() } ?: t.javaClass.simpleName
        }
    }

    /**
     * The platform's trust manager over the system CA store, which is what
     * `init(null)` selects. Resolved once; a failure is not cached, so a later
     * attempt can still succeed.
     */
    private val systemTrustManager: X509TrustManager? by lazy {
        val factory =
            TrustManagerFactory.getInstance(TrustManagerFactory.getDefaultAlgorithm())
        factory.init(null as KeyStore?)
        factory.trustManagers.filterIsInstance<X509TrustManager>().firstOrNull()
    }

    private fun decode(chain: Array<ByteArray>): Array<X509Certificate> {
        val factory = CertificateFactory.getInstance("X.509")
        return Array(chain.size) { i ->
            factory.generateCertificate(ByteArrayInputStream(chain[i])) as X509Certificate
        }
    }

    /**
     * Conscrypt uses authType for pinning decisions and rejects a blank one; it
     * does not have to match the negotiated key exchange, so the leaf's key
     * algorithm is both accurate enough and always available.
     */
    private fun authTypeOf(leaf: X509Certificate): String =
        leaf.publicKey?.algorithm?.takeIf { it.isNotBlank() } ?: "GENERIC"
}
