# Client TLS Certificate Verification

Certificate verification has exactly one owner for each handshake.

| Client | Owner | Trust source | Identity check |
|---|---|---|---|
| Linux CLI/library default | xquic/BoringSSL | BoringSSL default CA file/directory, overridable by `SSL_CERT_FILE` and `SSL_CERT_DIR` | BoringSSL hostname/IP verification |
| Windows CLI | `mqvpn_windows_cert_verify` | Windows certificate stores and enterprise policy | `CERT_CHAIN_POLICY_SSL` with the effective server name |
| macOS CLI and iOS PoC | `mqvpn_apple_cert_verify` | Keychain trust settings through `SecTrust` | `SecPolicyCreateSSL` with the effective server name |
| Android SDK/app | `jni_cert_verify` / `PlatformTrust` | Android `X509TrustManager` | DNS and IP subjectAltName matching; no commonName fallback |

The platform functions are installed with
`mqvpn_config_set_cert_verifier()`. When a verifier is installed, mqvpn asks
xquic for `NEED_VERIFY | APP_VERIFY`, so the callback is the sole judge of both
the chain and the endpoint identity. When no callback is installed, mqvpn asks
for `NEED_VERIFY` and BoringSSL owns the decision. `insecure=1` selects
`ALLOW_SELF_SIGNED` and bypasses either verifier.

This replaces the old `src/cert_verify.c` design. That implementation combined
a process-global Android trust hook with a second library-side verifier, so the
owner depended on which BoringSSL error happened first. Android could appear to
work when a chain happened not to reach the fallback callback, while other
failures were retried through a different verifier. The global hook, its JNI
setter, and its tests were removed rather than retained as a fallback.

## Effective server name

`ServerName` (`--tls-server-name`) controls both TLS SNI and certificate
identity. When it is empty, the configured server host is used.

```ini
[Server]
Address = 203.0.113.10:443
ServerName = vpn.example.com
```

Use `ServerName` when connecting to an address while the certificate contains a
DNS SAN. If the certificate contains the literal address as an IP SAN, no
override is needed. A server certificate file must contain the leaf followed by
its intermediates; clients do not fetch a missing intermediate.

## Failure behavior

- Missing or malformed chains, empty hostnames, expired certificates, unknown
  roots, and identity mismatches fail closed.
- A platform verifier rejection is reported as `MQVPN_ERR_TLS` before the QUIC
  drain completes. Library-owned BoringSSL failures close the connection.
- PSK authentication happens after TLS and cannot override a certificate
  rejection.
- Android logs the platform rejection reason through the existing native log
  bridge. The APK log viewer remains available.

## Maintenance checks

When updating mqvpn or xquic, verify all of the following:

1. `mqvpn_client.c` selects `APP_VERIFY` only when `cert_verify_fn` is set.
2. The callback receives `cli_effective_sni()`, not an independently derived
   hostname.
3. Linux does not install a platform callback.
4. Windows/macOS/iOS/Android install their callback on each newly created
   config, never in process-global state.
5. Android passes the hostname across JNI and validates both chain and SAN.
6. `insecure=1` remains the only verification bypass.
7. The xquic pin still contains the application-verifier and bounded-hostname
   fixes documented by the fork's sync commit.

Relevant code:

- `src/mqvpn_client.c`
- `include/libmqvpn.h`
- `src/platform/windows/cert_verify_windows.c`
- `src/platform/darwin/cert_verify_apple.c`
- `android/sdk-native/src/main/jni/mqvpn_jni.c`
- `android/sdk-native/src/main/kotlin/com/mqvpn/sdk/native_/PlatformTrust.kt`
- `tests/test_api.c` and `tests/test_server.c`
