// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 mp0rta and mqvpn contributors

package com.mqvpn.sdk.native_

import android.net.http.X509TrustManagerExtensions
import java.io.ByteArrayInputStream
import java.net.IDN
import java.net.InetAddress
import java.security.KeyStore
import java.security.cert.CertificateFactory
import java.security.cert.X509Certificate
import java.util.Locale
import javax.net.ssl.TrustManagerFactory
import javax.net.ssl.X509TrustManager

/**
 * Server-certificate check backed by Android's CA store.
 *
 * BoringSSL's `X509_STORE_set_default_paths` finds nothing on Android: the CA
 * store sits at none of the paths it compiles in, it moved into an updatable
 * APEX in Android 14, and user-installed CAs are reachable only through the
 * framework. Every server certificate therefore looked untrusted, the
 * handshake failed, and the client reconnected forever. Asking the platform is
 * the only check that stays correct across releases.
 *
 * Called from native code: `mqvpn_jni.c` resolves [checkServerTrusted] by name
 * in `JNI_OnLoad`; each native config installs it through
 * `mqvpn_config_set_cert_verifier`. The class name, method name and signature
 * are part of that contract — see `consumer-rules.pro`.
 */
object PlatformTrust {

    /**
     * @param chain DER-encoded certificates as delivered by the TLS stack,
     *   leaf first, peer-supplied intermediates after it.
     * @param hostname effective TLS server name, or the configured address.
     * @return `null` only when Android accepts the chain and the leaf SAN
     *   matches [hostname], otherwise a short reason for the native log.
     */
    @JvmStatic
    fun checkServerTrusted(chain: Array<ByteArray>, hostname: String): String? {
        if (chain.isEmpty()) return "empty certificate chain"
        if (hostname.isBlank()) return "empty certificate hostname"
        return try {
            val certs = decode(chain)
            val manager = systemTrustManager ?: return "no system X509 trust manager"
            X509TrustManagerExtensions(manager).checkServerTrusted(
                certs,
                authTypeOf(certs[0]),
                hostname,
            )
            if (!matchesHostname(certs[0], hostname)) {
                return "certificate subjectAltName does not match $hostname"
            }
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

    internal fun matchesHostname(certificate: X509Certificate, hostname: String): Boolean {
        val unbracketed = hostname.removePrefix("[").removeSuffix("]").removeSuffix(".")
        val address = parseIpLiteral(unbracketed)
        val names = certificate.subjectAlternativeNames ?: return false
        if (address != null) {
            return names.any { entry ->
                entry.size >= 2 && entry[0] == 7 && parseSanAddress(entry[1])
                    ?.contentEquals(address) == true
            }
        }

        val host = normalizeDnsName(unbracketed) ?: return false
        return names.any { entry ->
            entry.size >= 2 && entry[0] == 2 &&
                (entry[1] as? String)?.let { matchesDnsName(host, it) } == true
        }
    }

    private fun matchesDnsName(host: String, certificateName: String): Boolean {
        val raw = certificateName.removeSuffix(".")
        if (raw.startsWith("*.") && raw.indexOf('*', 1) == -1) {
            val suffix = normalizeDnsName(raw.substring(2)) ?: return false
            return host.endsWith(".$suffix") && host.count { it == '.' } == suffix.count { it == '.' } + 1
        }
        val name = normalizeDnsName(raw) ?: return false
        return '*' !in name && host == name
    }

    private fun normalizeDnsName(name: String): String? = try {
        IDN.toASCII(name, IDN.USE_STD3_ASCII_RULES).lowercase(Locale.US)
            .takeIf { it.isNotEmpty() }
    } catch (_: IllegalArgumentException) {
        null
    }

    private fun parseSanAddress(value: Any?): ByteArray? = when (value) {
        is ByteArray -> value.takeIf { it.size == 4 || it.size == 16 }
        is String -> parseIpLiteral(value)
        else -> null
    }

    private fun parseIpLiteral(value: String): ByteArray? {
        if (':' in value) {
            return try {
                InetAddress.getByName(value).address.takeIf { it.size == 16 }
            } catch (_: Exception) {
                null
            }
        }
        val parts = value.split('.')
        if (parts.size != 4) return null
        val bytes = ByteArray(4)
        for (i in parts.indices) {
            if (parts[i].isEmpty() || parts[i].any { !it.isDigit() }) return null
            val octet = parts[i].toIntOrNull() ?: return null
            if (octet !in 0..255) return null
            bytes[i] = octet.toByte()
        }
        return bytes
    }
}
