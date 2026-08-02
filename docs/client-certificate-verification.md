# Client TLS Certificate Verification

This document describes the secure-by-default client certificate verification
carried by this fork, the code paths that implement it, and the procedure for
preserving it while merging future upstream releases. Windows uses its native
trust store. Non-Windows builds use the default CA paths exposed by the bundled
OpenSSL-compatible X.509 implementation; packagers must make those paths usable
on their target.

## Why this patch exists

The TLS handshake authenticates the mqvpn server only when the client verifies
both of the following:

1. The server certificate chains to a trusted root.
2. The certificate identity matches the hostname used by the client.

Without both checks, a PSK does not provide a substitute for server identity
verification. An active network attacker could terminate TLS before application
authentication is evaluated. The normal client mode in this fork therefore
rejects untrusted, expired, malformed, or wrong-host certificates.

`--insecure` remains available for isolated testing and self-signed development
setups. It is an explicit opt-out and must not be enabled in production. The
equivalent configuration value is `[Server] Insecure = true` (or JSON
`"insecure": true`).

## User-visible behavior

Strict verification is the default:

```bash
sudo mqvpn --mode client \
    --server 203.0.113.10:443 \
    --tls-server-name vpn.example.com \
    --auth-key "$MQVPN_AUTH_KEY"
```

`--tls-server-name` controls both TLS SNI and certificate hostname
verification. When it is omitted, mqvpn uses the host portion of `--server`.
Deployments that connect to an IP address while presenting a DNS certificate
must set this option (or `[Server] ServerName`) to the certificate's DNS name.

A self-signed development server requires an explicit bypass:

```bash
sudo mqvpn --mode client \
    --server 192.0.2.10:443 \
    --auth-key "$MQVPN_AUTH_KEY" \
    --insecure
```

The client emits a warning whenever this bypass is active.

## Implementation map

The implementation is intentionally split between xquic integration and a
small platform verifier:

- `src/mqvpn_client.c` registers `cb_cert_verify` in
  `xqc_transport_callbacks_t`, selects `XQC_TLS_CERT_FLAG_NEED_VERIFY` unless
  insecure mode is active, and passes the effective hostname as SNI to
  `xqc_h3_connect`.
- `src/cert_verify.c` and `src/cert_verify.h` parse the DER certificate chain
  supplied by xquic and perform chain and hostname verification. The leaf is
  `certs[0]`; subsequent entries are untrusted intermediates.
- `src/main.c`, `src/config.c`, and the public configuration API carry
  `tls_server_name` and `insecure` from CLI/INI/JSON configuration into the
  client. Insecure mode defaults to false.
- `CMakeLists.txt` compiles `src/cert_verify.c` into both library variants,
  exposes the BoringSSL headers to those targets, and links `crypt32` on
  Windows.

On Windows, verification uses `CertGetCertificateChain` and
`CertVerifyCertificateChainPolicy(CERT_CHAIN_POLICY_SSL)` with the native
Windows trust store and the expected server name.

On non-Windows builds, verification uses the OpenSSL-compatible X.509 API
provided by the bundled BoringSSL build. `X509_STORE_set_default_paths` loads
the configured default CA locations, `X509_VERIFY_PARAM_set1_host` enforces the
hostname, and `X509_CHECK_FLAG_NEVER_CHECK_SUBJECT` requires a Subject
Alternative Name rather than accepting the legacy certificate Common Name.
Packagers must ensure that their target supplies a usable default CA bundle or
directory. This code does not currently provide a custom CA-file option.

The hostname length argument to `xqc_h3_connect` must be `0` for its
NUL-terminated hostname API. Do not replace it with `strlen(sni)` without first
confirming the exact xquic API contract in the pinned submodule. Passing the
wrong form caused connection failures in an earlier version of this patch.

## Security invariants

Future changes must preserve all of these properties:

- Verification is enabled by default. Only an explicit insecure setting may
  bypass it.
- A missing connection context, missing certificate, empty hostname, malformed
  DER object, invalid chain, untrusted root, expired certificate, or hostname
  mismatch fails closed.
- The verifier checks the complete DER object instead of silently accepting
  trailing bytes.
- Intermediates supplied by the peer are untrusted inputs; they may help build
  a chain but never become trust anchors.
- SNI and the verification hostname are derived from the same effective name.
- POSIX hostname verification requires SAN and does not fall back to Common
  Name.
- The PSK/authentication result never overrides a certificate failure.

## Merging a future upstream release

First determine whether upstream has implemented equivalent verification. Do
not judge this only by the presence of an `--insecure` flag. Inspect the actual
xquic client callback and confirm that normal mode performs trust-chain and
hostname verification on every supported client platform.

Useful checks after fetching upstream are:

```bash
git grep -n "cert_verify_cb" upstream/main -- src include
git grep -n "XQC_TLS_CERT_FLAG_NEED_VERIFY" upstream/main -- src
git grep -n "X509_VERIFY_PARAM_set1_host\|CertVerifyCertificateChainPolicy" \
    upstream/main -- src
```

If upstream now provides equivalent or stronger behavior, prefer its design and
remove this fork patch only after the negative certificate tests pass. Compare
the security invariants above, configuration compatibility, Windows behavior,
and release packaging before declaring the implementations equivalent.

If upstream still lacks the verifier, merge the release normally and retain the
fork commits. A practical sequence is:

```bash
git fetch upstream --tags
git switch -c sync-upstream-vX.Y.Z main
git merge --no-ff upstream/vX.Y.Z

# Confirm that the verifier survived the merge.
git diff upstream/vX.Y.Z...HEAD -- \
    src/cert_verify.c src/cert_verify.h src/mqvpn_client.c CMakeLists.txt
```

The original patch was introduced by these commits, in dependency order:

```text
60e78bb fix(client): verify peer certificates with platform trust stores
b0bbc3d fix(build): expose BoringSSL headers to library targets
87f2581 fix(tls): pass hostname length to BoringSSL
c45d279 fix(ci): format certificate verifier and stabilize connection cap test
```

These commits are already ancestors of this fork's `main`; normally they should
not be cherry-picked again. The list is a recovery reference for a clean branch
based directly on upstream. When such a clean rebase is necessary, cherry-pick
the relevant commits in the order shown, resolve conflicts, and compare the
result with current `main`. The last commit also contains an unrelated CI test
stabilization, so select only its certificate-formatting changes if that CI fix
is not wanted.

### Conflict checklist

Upstream changes most likely to conflict are xquic API upgrades and CMake target
reorganization. After resolving conflicts, verify each item explicitly:

1. `src/cert_verify.c` remains in the source list used by both `mqvpn_lib` and
   `mqvpn_shared`.
2. Both targets can include the bundled BoringSSL X.509 headers.
3. Windows library and executable links still include `crypt32`.
4. `xqc_transport_callbacks_t.cert_verify_cb` still points to the strict
   callback.
5. Normal mode requests certificate verification; insecure mode is the only
   bypass.
6. The effective server name is used consistently for SNI and hostname checks.
7. The `xqc_h3_connect` hostname and hostname-length arguments match the pinned
   xquic signature.
8. CLI, INI, JSON, and `libmqvpn` callers still default to secure mode.

Do not resolve an xquic conflict by returning success unconditionally from the
certificate callback or by setting `ALLOW_SELF_SIGNED` in normal mode. Both
choices silently restore the original vulnerability.

## Validation before release

Run the normal matrix because the verifier is part of the static and shared
libraries on every client platform:

```bash
cmake -S . -B build -DCMAKE_BUILD_TYPE=Release
cmake --build build -j"$(nproc)"
ctest --test-dir build --output-on-failure
sudo -E scripts/ci_e2e/run_test.sh
```

The E2E suite must demonstrate both sides of the policy:

- A self-signed test certificate connects only with `--insecure`.
- The same certificate is rejected when `--insecure` is omitted, and no tunnel
  becomes usable.

Also require the repository CI jobs for Linux sanitizers, Windows, macOS, and
Android to pass. For a production certificate, perform one positive connection
test without `--insecure` using the real DNS name. A complete certificate test
matrix should additionally cover a wrong hostname, expired leaf, untrusted root,
missing intermediate, and malformed DER chain whenever the verifier is changed.

## Release policy for this fork

Publish each release under a new immutable semantic version tag such as
`vX.Y.Z`. Never move an existing tag, replace an older release, or reuse a
mutable `latest` tag. GitHub's "Latest" release marker may point to the newest
immutable release without changing any previous tag or artifact.
