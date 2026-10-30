# Server Fallback Proxy

The Linux server can share its UDP listen address with another QUIC service.
It routes QUIC by the SNI in the client Initial packet and can also translate
ordinary HTTP/3 requests handled by mqvpn to a local HTTP/2 backend.

The feature is disabled by default. It does not change CONNECT-IP, Hybrid TCP,
or client behavior when disabled.

## Routing model

The proxy makes two routing decisions:

```text
Public UDP listener
  |
  +-- SNI does not match --> raw QUIC/UDP --> QuicFallback
  |
  `-- SNI matches --------> mqvpn QUIC/TLS/HTTP/3
                              |
                              +-- CONNECT-IP --> VPN tunnel
                              +-- mqvpn-tcp ---> Hybrid TCP lane
                              `-- other H3 ----> h2c Http2Backend
```

For an unmatched SNI, mqvpn reads only the QUIC Initial packet needed to make
the routing decision. It does not terminate the fallback connection's TLS.
Handshake and 1-RTT packets are forwarded unchanged.

For a matched SNI, mqvpn terminates QUIC and TLS as usual. CONNECT-IP remains
the first request handler, followed by the optional Hybrid TCP protocol.
Only ordinary HTTP/3 requests reach the HTTP/2 backend.

## Requirements

- Linux server build with nghttp2 support. Official Linux release packages
  include it.
- A UDP QUIC backend for unmatched SNI traffic.
- A prior-knowledge cleartext HTTP/2 (h2c) backend for ordinary matched HTTP/3
  requests.
- Enough file descriptors for the configured connection limit.

Windows, macOS, iOS, and Android builds do not provide the server proxy path.

## Configuration

### INI

Add the following section to the server configuration:

```ini
[Proxy]
Enabled = true
SNI = vpn.example.com,*.edge.example
QuicFallback = 127.0.0.1:4443
QuicFallbackProxyProtocol = false
Http2Backend = 127.0.0.1:8080
Http2BackendTLS = false
MaxConnections = 64
IdleTimeoutSec = 60
```

| Key | Description | Default |
| --- | --- | --- |
| `Enabled` | Enables the Linux server proxy path. | `false` |
| `SNI` | Comma-separated exact names or single-label wildcard patterns. Maximum 16 entries. | none |
| `QuicFallback` | UDP `host:port` for unmatched SNI. Use `[address]:port` for IPv6 literals. | none |
| `QuicFallbackProxyProtocol` | Prefix the first fallback datagram with PROXY v2. Disable for QUIC listeners that do not support PROXY protocol (including nginx QUIC). | `true` |
| `Http2Backend` | TCP `host:port` for prior-knowledge h2c requests. | none |
| `Http2BackendTLS` | Reserved for a future TLS upstream. The server rejects `true`. | `false` |
| `MaxConnections` | Limit applied independently to tracked/fallback connections and the H2 pool. | `64` |
| `IdleTimeoutSec` | Idle connection cleanup interval in seconds, up to 86400. | `60` |

The server fails during startup if proxy mode is enabled with missing or
invalid endpoints. It also fails if the binary was built without nghttp2.

### JSON

JSON uses a bounded `proxy` object:

```json
{
  "mode": "server",
  "listen": "[::]:443",
  "cert_file": "/etc/mqvpn/server.crt",
  "key_file": "/etc/mqvpn/server.key",
  "auth_key": "replace-with-a-generated-key",
  "proxy": {
    "enabled": true,
    "sni": "vpn.example.com,*.edge.example",
    "quic_fallback": "127.0.0.1:4443",
    "quic_fallback_proxy_protocol": false,
    "http2_backend": "127.0.0.1:8080",
    "http2_backend_tls": false,
    "max_connections": 64,
    "idle_timeout_sec": 60
  }
}
```

### C API

```c
int rc = mqvpn_config_set_proxy(cfg, 1,
                                "vpn.example.com,*.edge.example",
                                "127.0.0.1:4443",
                                "127.0.0.1:8080",
                                0, 64, 60);
```

The callback and configuration objects follow the normal libmqvpn lifetime
rules. The server copies the proxy configuration during initialization.

## nginx example

A common layout keeps nginx on public TCP 443 while mqvpn owns public UDP 443.
nginx exposes a separate loopback QUIC listener for raw fallback traffic and a
loopback h2c listener for requests whose SNI is handled by mqvpn:

```nginx
# Existing public HTTPS listener. TCP 443 does not conflict with mqvpn UDP 443.
server {
    listen 443 ssl;
    server_name public.example.com;

    ssl_certificate     /etc/letsencrypt/live/public.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/public.example.com/privkey.pem;

    add_header Alt-Svc 'h3=":443"; ma=86400' always;
    location / {
        proxy_pass http://127.0.0.1:9000;
    }
}

# Raw QUIC destination for SNI names not claimed by mqvpn. mqvpn sends a
# PROXY protocol v2 UDP header before the first fallback datagram.
server {
    listen 127.0.0.1:4443 quic reuseport proxy_protocol;
    server_name public.example.com;

    ssl_certificate     /etc/letsencrypt/live/public.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/public.example.com/privkey.pem;

    location / {
        proxy_pass http://127.0.0.1:9000;
    }
}

# Cleartext prior-knowledge HTTP/2 destination for matched ordinary H3.
server {
    listen 127.0.0.1:8080 http2;
    server_name vpn.example.com;

    location / {
        proxy_pass http://127.0.0.1:9000;
    }
}
```

The exact HTTP/2 directive depends on the nginx version. Newer nginx releases
may prefer `http2 on;` instead of the `listen ... http2` parameter. The
important requirement is prior-knowledge h2c on the configured loopback TCP
port, not HTTP/1.1 and not TLS.

Keep both backend ports private. Do not expose the h2c port to an untrusted
network. mqvpn does not currently authenticate or encrypt the H2 upstream.

## SNI matching

- Matching is ASCII case-insensitive.
- An exact pattern matches only the complete DNS name.
- `*.example.com` matches `api.example.com`.
- The same wildcard does not match `example.com` or
  `dev.api.example.com`.
- Empty labels, trailing dots, embedded wildcards, and invalid DNS label
  lengths are rejected during startup.

The SNI list selects routing only. It does not replace certificate hostname
verification. The mqvpn certificate must cover every matched name used by a
client.

Encrypted ClientHello hides the inner SNI. If only an outer name or no usable
name is visible, that visible result determines the route; a parseable
ClientHello with no configured match goes to `QuicFallback`.

## QUIC behavior

The router supports QUIC v1 Initial processing from RFC 9001 and QUIC v2 from
RFC 9369. Initial protection keys are derived from values carried in the
packet, as required by QUIC, so inspecting the ClientHello does not reveal
Handshake or 1-RTT secrets.

Before a decision is available, the router keeps a bounded set of Initial
datagrams and replays them in order to the selected destination. Malformed or
unsupported Initial traffic fails open to the normal mqvpn/xquic path instead
of being silently dropped.

For fallback connections, mqvpn tracks a backend Retry or server Initial SCID
so that the client's subsequent DCID change remains on the same connected UDP
socket. Backend replies are returned through the public mqvpn listener.

mqvpn sends a PROXY protocol v2 UDP header before the first datagram of every
fallback session. Configure the fallback listener to accept PROXY protocol;
Nginx and CrowdSec then receive the original client address instead of the
mqvpn loopback address. Subsequent datagrams are unchanged QUIC packets.

## HTTP/3 to HTTP/2 translation

Request pseudo-fields, regular fields, body data, and end-of-stream state are
translated to HTTP/2 with nghttp2. Connection-specific fields are removed:

- `connection`
- `keep-alive`
- `proxy-connection`
- `transfer-encoding`
- `upgrade`

`TE` is forwarded only when its value is `trailers`. Informational responses,
final response headers, body data, trailers, and stream completion are mapped
back to HTTP/3. If the backend fails before a final response starts, mqvpn
attempts to return HTTP status 502.

Backend sockets are nonblocking and reused. One H2 connection supports up to
100 concurrent streams, and the pool can grow to `MaxConnections` connections.
Request and response buffers are bounded; a stream is closed if it exceeds a
limit rather than consuming unbounded memory.

Response pacing is per stream and comes from xquic itself. mqvpn hands a
response body to `xqc_h3_request_send_body` until the buffer empties or xquic
declines — a short write or `-XQC_EAGAIN`. There is no per-turn byte quota: a
quota cannot work here, because xquic also removes a stream from writable
scheduling as soon as it accepts a whole write, so a response that stopped
early had nothing left to re-arm its notification and advanced only on the
server's recovery tick.

While a response is waiting, mqvpn keeps its write notification armed, and
xquic keeps such a stream in writable scheduling. The acknowledgement that
releases queue space therefore resumes the response directly, with no timer
involved. Reads on the shared h2c socket are never disabled: nghttp2
connection credit is returned once bytes enter the bounded per-stream buffer,
while stream credit is returned only after xquic accepts them, so
backpressure stays scoped to the one stream.

A separate valve bounds memory rather than rate. If one QUIC connection is
holding more than 1 MiB that has not reached the wire,
`xqc_h3_request_get_unsent_queue_bytes` reports it and the next response in
the rotation waits. That estimate excludes packets in flight on purpose:
in-flight bytes are the congestion controller's budget, and a connection
running at its bandwidth-delay product holds a congestion window of them at
all times, so a threshold that counted them stayed closed on exactly the fast
connections it was meant to pace.

Recovery is fair within a downstream QUIC connection. Each pass starts at a
persistent round-robin cursor and offers every response a turn. If the valve
trips, the cursor stays on the response that did not get to send rather than
on the one that just filled the queue, so it runs first once the queue
drains. This prevents a large JavaScript response at the list head from
repeatedly starving later module chunks on a high-RTT, lossy path.

The server event loop also wakes every 50 ms while response work is
outstanding. That is a safety net, not the recovery path: xquic suppresses
write notification while connection-level `DATA_BLOCKED` is set, so a
response waiting on a `MAX_DATA` update needs some other wakeup. Because a
rotation is not byte-limited, this interval bounds recovery latency after a
suppressed notification and does not bound throughput.

## Resource limits

| Resource | Limit |
| --- | --- |
| SNI patterns | 16 |
| Reassembled ClientHello | 64 KiB by default, 1 MiB hard limit |
| Pending Initial datagrams | 8 by default, 64 hard limit per connection |
| H2 response field section | 32 KiB and 256 fields |
| H2 body buffer | 1 MiB per direction per stream in the server integration |
| Tracked fallback connections | `MaxConnections` |
| H2 backend connections | `MaxConnections` |

At startup, the server reserves 64 file descriptors for other work and checks
the remaining `RLIMIT_NOFILE` budget across fallback, H2, and Hybrid TCP. It
reduces the effective proxy cap when possible and rejects configurations that
cannot support even one proxy pair.

## Validation

Use separate DNS names that resolve to the same public address. One name
should match `SNI`; the other should be handled by the raw QUIC backend.

```bash
# Matched name: mqvpn terminates H3 and sends the request to Http2Backend.
curl --http3-only --resolve vpn.example.com:443:203.0.113.10 \
  https://vpn.example.com/

# Unmatched name: the complete QUIC connection goes to QuicFallback.
curl --http3-only --resolve public.example.com:443:203.0.113.10 \
  https://public.example.com/
```

Run each command repeatedly, not only once. Also test an idle connection past
the expected application interval, a long active transfer, concurrent matched
and unmatched traffic, and browser connection reuse. These cases exercise the
Retry/CID state and cleanup paths that a single fresh request does not cover.

Repository protocol coverage includes:

- RFC v1 and v2 fixed Initial vectors;
- exact and wildcard SNI matching;
- fragmented and out-of-order ClientHello data;
- real loopback UDP fallback in both directions;
- Retry and server Initial CID changes;
- real nghttp2 request, response, trailers, and early-close behavior;
- pending timeout fail-open behavior;
- ASan, UBSan, MSan, CodeQL, and Linux end-to-end jobs.

## Current limitations

- Only QUIC v1 and v2 Initial packets are inspected.
- A connection is associated with its source IP and UDP port. QUIC migration
  or NAT rebinding to a new address cannot be linked to an existing fallback
  socket.
- One listener supports one `QuicFallback` and one `Http2Backend`.
- The H2 backend is h2c only. Upstream TLS is not implemented.
- Buffers and connection tables are intentionally bounded.

## Standards

- RFC 9000, QUIC: A UDP-Based Multiplexed and Secure Transport
- RFC 9001, Using TLS to Secure QUIC
- RFC 9369, QUIC Version 2
- RFC 6066, TLS Extensions: `server_name`
- RFC 8446, TLS 1.3
- RFC 9113, HTTP/2
- RFC 9114, HTTP/3
- RFC 9204, QPACK
- RFC 9484, Proxying IP in HTTP
- RFC 9525, Service Identity in TLS
