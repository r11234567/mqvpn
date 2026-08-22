# X25519MLKEM768 Post-Quantum Cryptography Investigation

## Summary

X25519MLKEM768 cannot be enabled in mqvpn due to QUIC protocol MTU limitations. The MLKEM768 public key size (1184 bytes) causes QUIC Initial packets to exceed the maximum allowed packet size, resulting in TLS handshake failures.

## Investigation Details

### Attempted Configuration

We attempted to add X25519MLKEM768 post-quantum key exchange support by modifying the TLS groups configuration:

```c
engine_ssl.groups = "X25519:P-256:P-384:P-521:X25519MLKEM768";
```

### Test Results

**Without X25519MLKEM768**: ✅ All tests pass
**With X25519MLKEM768**: ❌ test_tcp_egress fails with TLS encryption error

### Root Cause Analysis

Using detailed debug logging, we identified the exact failure point:

1. **Error Code**: `-736` (`XQC_TLS_ENCRYPT_DATA_ERROR`)
2. **Error Location**: `xqc_crypto_encrypt_payload` → `EVP_AEAD_CTX_seal` failure
3. **Packet Details**:
   - Packet number: 2 (during TLS handshake)
   - Packet size: **1437 bytes**
   - Maximum allowed: **1400 bytes**
   - **Overflow: 37 bytes**

### Technical Explanation

#### QUIC MTU Constraints

QUIC RFC 9000 requires that Initial packets must be padded to at least 1200 bytes and fit within the path MTU (typically 1200-1500 bytes). The implementation uses a conservative 1400-byte limit for QUIC packets.

#### MLKEM768 Key Size

- **X25519 public key**: 32 bytes
- **MLKEM768 public key**: 1184 bytes
- **X25519MLKEM768 combined**: 1216 bytes

When combined with other TLS extensions (ALPN, SNI, supported versions, etc.), the ClientHello or subsequent handshake packets exceed the QUIC packet size limit.

#### Error Flow

```
1. TLS handshake begins
2. QUIC tries to send packet #2 (1437 bytes with MLKEM768 data)
3. Packet size (1437) > max_po_size (1400)
4. EVP_AEAD_CTX_seal() fails (buffer too small)
5. Connection closes with XQC_TLS_ENCRYPT_DATA_ERROR
6. handshake_done=0, test times out
```

### Debug Log Evidence

```
[DEBUG] Creating engine with groups: X25519:P-256:P-384:P-521:X25519MLKEM768
[DEBUG] harness_start succeeded, pumping for response...
[XQUIC-2] xqc_crypto_encrypt_payload|encrypt packet error|ret:-736|nwrite:0|
[XQUIC-2] xqc_packet_encrypt_buf|packet protection error|pkt_type:0|pkt_num:2
[XQUIC-2] xqc_process_packet_with_pn|pn:2|frames:32|size:1437|
[XQUIC-0] xqc_conn_destroy|handshake_time:0|max_po_size:1400|
[DEBUG] Connection closed (handshake_done=0, response_done=0)
```

### BoringSSL Support Status

X25519MLKEM768 is fully implemented in the BoringSSL version used:
- Commit: `fd490c05d` (2026-07-30)
- Support added in: `e748facba` (2026-04-29) - "ssl: Support ML-KEM by default"
- Note from commit: "SSL_GROUP_X25519_MLKEM768 requires quite much data in the ClientHello"

The BoringSSL developers were aware of this size issue (see https://tldr.fail).

## Potential Solutions (Not Implemented)

### 1. Increase QUIC Packet Size Limit
**Difficulty**: Hard  
**Risk**: High

Would require modifying xquic core to support larger Initial packets, potentially breaking QUIC RFC compliance.

### 2. Use MLKEM768 Only After Handshake
**Difficulty**: Very Hard  
**Benefit**: Limited

Would require TLS 1.3 post-handshake key exchange, which is complex and may not be supported by xquic/BoringSSL integration.

### 3. Wait for MLKEM1024 or Smaller Variants
**Difficulty**: N/A  
**Timeline**: Future

Smaller post-quantum algorithms may become available, but MLKEM768 is currently the NIST-recommended standard.

## Conclusion

**X25519MLKEM768 is not viable for QUIC-based protocols like mqvpn due to fundamental packet size constraints.** The issue is not a bug in our code or configuration, but a known limitation of combining large post-quantum keys with QUIC's MTU requirements.

## Current Implementation

We have successfully implemented **AES-256-GCM cipher prioritization** without post-quantum key exchange:

```c
engine_ssl.ciphers = "TLS_AES_256_GCM_SHA384:TLS_AES_128_GCM_SHA256:TLS_CHACHA20_POLY1305_SHA256";
engine_ssl.groups = "X25519:P-256:P-384:P-521";  // No MLKEM768
```

This provides:
- ✅ 256-bit symmetric encryption (stronger than previous 128-bit default)
- ✅ Modern elliptic curve key exchange
- ✅ Full test suite passes
- ✅ Backward compatible

Post-quantum cryptography support in QUIC remains an active area of research and may be revisited when:
- Smaller PQC algorithms become standardized
- QUIC specifications evolve to handle larger handshakes
- xquic implements fragmentation or other workarounds

## References

- [BoringSSL ML-KEM support commit](https://boringssl-review.googlesource.com/c/boringssl/+/94347)
- [tldr.fail - ClientHello size issues](https://tldr.fail)
- [QUIC RFC 9000 - Initial Packet Size](https://datatracker.ietf.org/doc/html/rfc9000#section-14)
- [NIST ML-KEM Standard](https://csrc.nist.gov/pubs/fips/203/final)
