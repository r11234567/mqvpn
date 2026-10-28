# Certificate Verification Architecture Comparison

The historical comparison in this file described `src/cert_verify.c` as a
second verifier layered over xquic. That design is no longer present. It was
replaced while synchronizing upstream's config-scoped application verifier.

| Design | Verification owner | Result |
|---|---|---|
| xquic/BoringSSL `NEED_VERIFY` | BoringSSL | Used by Linux, where a filesystem CA bundle is available. |
| xquic `NEED_VERIFY | APP_VERIFY` | One mqvpn platform callback | Used by Windows, Apple platforms, and Android so their native trust stores and policies are honored. |
| ~~Old mqvpn `cert_verify.c` plus global Android trust hook~~ | ~~BoringSSL and mqvpn could both participate~~ | **Removed.** Error-dependent fallback made behavior inconsistent and process-global state made ownership unclear. |

The current behavior and maintenance checklist are documented in
[Client TLS Certificate Verification](client-certificate-verification.md).
