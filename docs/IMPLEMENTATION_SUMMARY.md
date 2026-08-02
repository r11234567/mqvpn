# SNI 分流与 HTTP/2 回落实施总结

## 状态

本分支已经把 SNI 分流和 HTTP/3 到 HTTP/2 回落接入 Linux 服务端主路径，
不再是独立原型。Windows 构建是 client-only，不编译 POSIX server-side SNI/H2
模块；macOS、iOS 和 Android 也不启用代理集成。

当前实现对应两级路由：

```text
同一个 UDP 监听端口
  |
  +-- QUIC Initial SNI 不匹配 --> 原始 QUIC/UDP 双向转发到 QuicFallback
  |
  `-- QUIC Initial SNI 匹配 --> xquic 完成 QUIC/TLS/HTTP/3
                                |
                                +-- MASQUE CONNECT-IP --> 原有 VPN 路径
                                +-- mqvpn-tcp ----------> 原有 Hybrid TCP 路径
                                `-- 其他 H3 请求 -------> h2c Http2Backend
```

SNI 不匹配路径不会终止 TLS。只有 SNI 匹配并交给 xquic 的连接才在 mqvpn 内终止
QUIC/TLS，然后按 HTTP/3 请求类型继续分流。CONNECT-IP 判断保持第一优先级。

## 已实现内容

### QUIC SNI 路由

`src/sni_router.[ch]` 实现了：

- RFC 9001 QUIC v1 和 RFC 9369 QUIC v2 Initial 密钥派生。
- AES header protection 移除、packet number 还原和 AES-128-GCM 解密。
- ACK、CRYPTO、PADDING 和 PING frame 处理。
- 乱序 CRYPTO 数据重组以及跨 Initial packet 的 ClientHello/SNI 提取。
- DNS 名称精确匹配和仅匹配一个最左标签的 `*.example.com` 通配符。
- 基于客户端地址的有界连接表、超时清理和 pending packet 上限。
- 每个 fallback 连接使用 connected UDP socket，支持上游回包转发给原客户端。
- 对解密失败、未知 QUIC 版本、资源不足和 pending 超限执行 fail-open，交给原
  xquic 路径处理，避免路由器把合法 VPN 流量静默丢弃。

### HTTP/3 到 HTTP/2

`src/h2_proxy.[ch]` 使用 nghttp2 实现：

- 非阻塞 TCP 建连和 HTTP/2 连接复用。
- H3 请求 headers/body/FIN 转换为 H2 请求。
- 过滤 HTTP connection-specific fields；`TE` 仅允许 `trailers`。
- H2 informational/final response headers、body、trailers 和 FIN 回传 H3。
- xquic 与 nghttp2 两侧的 EAGAIN、deferred body、RST 和关闭生命周期处理。
- 后端失败时尽量返回 `502`；已开始响应的流则关闭 H3 request。
- 每连接并发流、连接数、空闲超时、header 和 body 缓冲上限。

后端目前只支持 prior-knowledge h2c。`Http2BackendTLS=true` 会在服务启动时明确
失败，不会静默降级成明文。

### 配置和 API

INI 示例：

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

JSON 使用对应的 snake_case key。INI 和 JSON 解析结果有一致性测试。公共 builder
API 为 `mqvpn_config_set_proxy()`。

`MaxConnections` 同时限制 SNI tracking/fallback 和 H2 后端连接池。启动时还会按
`RLIMIT_NOFILE` 的可用余量收窄该值，并为 fallback、H2 和 Hybrid TCP 统一计算
事件 reactor 的 fd 容量，避免建立了 socket 却没有可用事件槽。

### 主路径兼容性

与 `main` 对比后的约束如下：

- `[Proxy] Enabled=false` 时，UDP 收包路径只增加一次空指针分支，随后执行原有
  `xqc_engine_packet_process()`。
- 匹配 SNI 后仍使用原有证书、xquic TLS 和 H3 server。
- `connect-ip` 分支仍是 request header dispatch 的第一个分支。
- Hybrid `mqvpn-tcp` 分支仍在普通 H2 回落之前，并继续受 `[Hybrid] Enabled`
  控制。
- 只有剩余的普通 H3 请求进入 H2 后端；代理关闭时仍返回原来的 `501`。

## RFC 边界

- RFC 9000：QUIC long header、varint、packet number 和 frame 编码。
- RFC 9001：QUIC v1 Initial protection 和 TLS over QUIC。
- RFC 9369：QUIC v2 version、Initial salt 和 v2 HKDF labels。
- RFC 6066 / RFC 8446：TLS ClientHello `server_name`。
- RFC 9525：DNS 标识比较及单标签通配符边界。
- RFC 9113：HTTP/2、connection-specific field 禁止项和流生命周期。
- RFC 9114：HTTP/3 请求/响应语义。
- RFC 9484：原有 MASQUE CONNECT-IP 路径，未改变协议处理顺序。

## 测试门禁

`.github/workflows/sni-h2-proxy-tests.yml` 包含三个 job：

1. Linux format-check：clang-format 18.1.3 检查本分支涉及的 C/H 文件。
2. Linux protocol regression：RFC v1/v2 fixed vector、
   SNI exact/wildcard、乱序 ClientHello、双向 UDP fallback、真实 nghttp2 双向代理、
   config parity、server 和 CONNECT-IP/TCP egress 回归。
3. Linux ASan/UBSan：SNI 和 H2 协议测试，包含 H3 提前关闭后的 nghttp2 生命周期。

全局 `CI` 的 Windows job 负责 client-only 构建；Linux CI 和 release job 安装
`libnghttp2-dev`，因此 Linux 正式产物不会因
构建机缺少 nghttp2 而静默删除 H2 功能。DEB 使用 `dpkg-shlibdeps` 生成运行时
nghttp2 依赖。

## 资源与性能

- SNI 检查只对未决连接的 Initial packet 做 HKDF/AES/ClientHello 工作；决定后
  通过哈希连接表直接转发。
- 代理关闭时不进行 QUIC 解密或额外复制，保留原收包路径。
- fallback 每个活动客户端最多占一个 UDP fd；H2 使用多路复用连接池。
- 默认 ClientHello 重组上限 64 KiB、pending Initial 上限 8 packet。
- H2 每个流每方向 body buffer 上限 1 MiB，response field section 上限 32 KiB，
  response field 数上限 256。
- 没有写入未经基准验证的固定微秒延迟数字。发布前以 Actions correctness gate 和
  部署后的实际 workload 验证为准。

## 已知限制

- 只识别 QUIC v1 和 v2 Initial；其他版本 fail-open 到 mqvpn。
- ECH 隐藏真实 SNI 时无法按内层名称匹配，将走配置的 QUIC fallback。
- fallback tracking 以客户端 IP/port 为键。决定完成后的 QUIC connection
  migration/NAT rebinding 无法关联到旧 fallback socket。
- 单个监听器目前只有一个 `QuicFallback` 和一个 `Http2Backend`。
- H2 upstream TLS 尚未实现；需要由同机 nginx 或其他可信 sidecar 提供 h2c。
- 当前采用有界内存缓冲；超过上限会终止受影响的代理流/后端连接，而不是无限制
  占用内存。

## 发布和部署顺序

本分支的验证顺序固定为：新增协议 workflow，随后全局 CI 和 gitleaks，然后
release/build workflow，最后创建 pre-release 并使用该预发布产物替换本机部署。
不以零散的本地依赖构建代替发布产物验证。
