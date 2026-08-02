# SNI 分流与 HTTP/3 到 HTTP/2 回落

## 目标

该功能允许一个 UDP 监听地址同时承载 mqvpn 和另一个 QUIC 服务，并在 mqvpn
连接内部继续区分 MASQUE CONNECT-IP、Hybrid TCP 和普通 HTTP 请求。

它不是“先终止所有 TLS 再按 SNI 转发”。第一层只解密公开可派生密钥保护的
QUIC Initial，读取 ClientHello SNI：

- SNI 不匹配：原始 UDP datagram 双向转发到 `QuicFallback`，mqvpn 不终止 TLS。
- SNI 匹配：datagram 交给 xquic，由 mqvpn 完成 QUIC/TLS/H3。

第二层只发生在匹配 SNI 的连接中：

- Extended CONNECT `:protocol=connect-ip` 使用现有 RFC 9484 VPN 实现。
- `:protocol=mqvpn-tcp` 且 Hybrid 已启用时使用现有 TCP lane。
- 其他 HTTP/3 request 转换为 HTTP/2 并发送到 `Http2Backend`。

## 配置

```ini
[Proxy]
Enabled = true
SNI = vpn.example.com,*.edge.example
QuicFallback = 127.0.0.1:4443
Http2Backend = 127.0.0.1:8080
Http2BackendTLS = false
MaxConnections = 64
IdleTimeoutSec = 60
```

字段说明：

| 字段 | 含义 |
| --- | --- |
| `Enabled` | 启用 Linux 服务端两级代理路径，默认 `false` |
| `SNI` | 最多 16 个逗号分隔 DNS 名称或 `*.example.com` 模式 |
| `QuicFallback` | SNI 不匹配时的 UDP `host:port`，IPv6 使用 `[addr]:port` |
| `Http2Backend` | 普通 H3 request 的 prior-knowledge h2c `host:port` |
| `Http2BackendTLS` | 当前必须为 `false`；`true` 会让服务启动失败 |
| `MaxConnections` | SNI tracking/fallback 和 H2 pool 的各自上限，默认 64 |
| `IdleTimeoutSec` | 无活动连接清理时间，默认 60，最大 86400 |

JSON 配置使用 `enabled`、`sni`、`quic_fallback`、`http2_backend`、
`http2_backend_tls`、`max_connections` 和 `idle_timeout_sec`。

公共 API：

```c
int rc = mqvpn_config_set_proxy(cfg, 1,
                                "vpn.example.com,*.edge.example",
                                "127.0.0.1:4443",
                                "127.0.0.1:8080",
                                0, 64, 60);
```

## SNI 路由协议处理

### Initial 解密

路由器接受 RFC 9001 QUIC v1 和 RFC 9369 QUIC v2 Initial：

1. 校验 long header、版本、DCID/SCID、token 和 payload length。
2. 用版本对应 Initial salt 和 DCID 派生 client Initial secret。
3. 使用 AES header protection key 恢复首字节和 packet number。
4. 按 RFC 9000 packet-number reconstruction 恢复完整 packet number。
5. 使用 AES-128-GCM 解密 payload。
6. 读取 ACK/CRYPTO/PADDING/PING，按 CRYPTO offset 重组 ClientHello。
7. 从 TLS `server_name` extension 取出 host_name。

RFC 规定 Initial protection 的秘密可由线上的 DCID 推导，所以这一步不破坏后续
TLS 机密性。Handshake 和 1-RTT 数据不会被 SNI router 解密。

### 匹配规则

- DNS 名称使用 ASCII 不区分大小写比较。
- 精确模式只匹配完整名称。
- `*.example.com` 只匹配一个最左标签，如 `api.example.com`。
- 它不匹配 `example.com` 或 `dev.api.example.com`。
- 不接受中间通配符、空标签、超长标签和尾随点模式。

该边界与 RFC 9525 的服务标识通配符约束一致。这里执行的是路由选择，不替代
证书名称验证。

### Fail-open 与 fallback

路由器在完整 SNI 决定前保存少量 Initial datagram。决定后按原顺序回放给 xquic
或 UDP fallback。下列情况交给 xquic，而不是静默丢包：

- QUIC 版本不是 v1/v2。
- Initial 格式、AEAD 或 CRYPTO frame 不能安全解析。
- 内存分配、pending queue 或 tracking table 资源不足。
- 未决连接超时或被新连接驱逐。

可解析且 ClientHello 不含可匹配 SNI（包括使用 ECH 的情况）会明确选择
`QuicFallback`。

fallback 为每个客户端创建一个 connected nonblocking UDP socket。上行 datagram
保持原字节；回包通过主监听 socket 发回原客户端。已决定连接的查找是哈希表
操作，不会对每个后续 packet 重做 Initial 密码运算。

## HTTP/3 到 HTTP/2 转换

### 请求

H3 pseudo-fields 和普通 fields 交给 nghttp2 编码。以下 connection-specific
fields 不会转发：

- `connection`
- `keep-alive`
- `proxy-connection`
- `transfer-encoding`
- `upgrade`

`TE` 只有值为 `trailers` 时允许。request body 由有界 buffer 接收，nghttp2 在
流量窗口允许时读取；H3 FIN 映射为 H2 END_STREAM。

### 响应

nghttp2 解码的 informational headers、final headers、DATA、trailers 和
END_STREAM 按顺序发送回 H3。响应必须有合法的 `:status`；101 不允许用于 H2。
后端在 final response 前失败时尽量发送仅含 `:status=502` 的 H3 response。

H3 流提前关闭会提交 H2 `RST_STREAM(CANCEL)`，并先解除 nghttp2 stream
user-data，避免延迟 close callback 访问已经释放的 H3 proxy stream。

### 连接池

- 后端 socket 为 nonblocking TCP，启用 `TCP_NODELAY`。
- 一个 H2 connection 默认最多并发 100 stream。
- pool 最多创建 `MaxConnections` 个 connection。
- 空闲且无 stream 的 connection 在 `IdleTimeoutSec` 后关闭。
- Linux reactor 根据 read/write interest 更新 libevent registration。

后端目前必须直接支持 prior-knowledge h2c。常见部署方式是在 mqvpn 同机启动
nginx 或其他网关的 h2c listener；后端 TLS 和证书校验尚未实现。

## 资源限制

| 资源 | 上限 |
| --- | --- |
| SNI patterns | 16 |
| ClientHello 重组 | 默认 64 KiB，硬上限 1 MiB |
| 未决 Initial datagram | 默认 8，硬上限 64 / connection |
| H2 response field section | 32 KiB、256 fields |
| H2 request/response body buffer | 当前 server integration 每方向 1 MiB / stream |
| tracked/fallback 和 H2 pool | 各自 `MaxConnections` |

服务器启动时以 `RLIMIT_NOFILE - 64` 为总 fd headroom，并为最坏情况下的
fallback、H2 和启用的 Hybrid TCP 连接统一计算 reactor registry。若 headroom
不足，实际代理 connection cap 会收窄；不足以容纳一对代理 fd 时启动失败。

## 构建与平台

Linux 构建需要 nghttp2 development headers：

```bash
cmake -S . -B build -DCMAKE_BUILD_TYPE=Release \
      -DXQUIC_BUILD_DIR=third_party/xquic/build
cmake --build build
```

CMake 在 Linux 找到 nghttp2 后定义 `MQVPN_H2_PROXY_ENABLED`。启用了 `[Proxy]`
但产物没有该能力时，服务启动会明确失败。release workflow 安装
`libnghttp2-dev`；DEB 的运行时依赖由 `dpkg-shlibdeps` 生成。

当前平台矩阵：

- Linux：SNI、UDP fallback、H3/H2 和 reactor 集成全部启用。
- Windows：SNI module 参与 MSVC warning-as-error 编译；服务 reactor 不启用代理。
- macOS/iOS/Android：不启用该代理路径。

## 测试

`SNI and H2 Protocol Tests` workflow 是该功能的第一道门禁：

- RFC 9001 Appendix A.2 v1 1200-byte Client Initial fixed vector。
- RFC 9369 Appendix A.2 v2 1200-byte Client Initial fixed vector。
- 每个截断长度、AEAD tag corruption、精确/通配符路由。
- 乱序 fragmented ClientHello、pending timeout fail-open。
- loopback 上真实双向 UDP fallback。
- loopback 上真实 nghttp2 client/server，覆盖 request body、103、200、response
  body/FIN 以及 H3 提前关闭。
- config INI/JSON parity、server、CONNECT-IP 和 Hybrid TCP egress regression。
- ASan/UBSan protocol jobs 和 Windows MSVC build。

该 workflow 通过后，再运行全局 CI 和 gitleaks；随后才运行 release/build，创建
pre-release，并用发布产物执行本机整体部署验证。

## 已知限制

- fallback 连接以源 IP/port 关联；QUIC migration 或 NAT rebinding 后的新地址不
  会自动关联旧 fallback socket。
- 同一监听端口当前只有一个 QUIC fallback 和一个 H2 backend。
- ECH 内层 SNI 不可见，因此无法按内层名称进入 mqvpn。
- H2 upstream TLS 未实现。
- 有界 buffer 达到上限时关闭受影响的代理流/连接；当前没有跨 H2/H3 的零拷贝
  backpressure bridge。

## RFC 参考

- RFC 9000, QUIC: A UDP-Based Multiplexed and Secure Transport
- RFC 9001, Using TLS to Secure QUIC
- RFC 9369, QUIC Version 2
- RFC 6066, TLS Extensions: `server_name`
- RFC 8446, TLS 1.3
- RFC 9525, Service Identity in TLS
- RFC 9113, HTTP/2
- RFC 9114, HTTP/3
- RFC 9484, Proxying IP in HTTP
