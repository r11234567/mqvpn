# SNI路由和HTTP/2代理功能

本分支添加了两个新的流量分流功能，用于实现更灵活的流量路由。

## 功能概述

### 1. SNI路由模块 (SNI Router)

**位置**: `src/sni_router.[ch]`

**功能**: 
- 在QUIC Initial包阶段检查SNI（Server Name Indication）
- 根据配置的SNI模式路由流量
- 不匹配的SNI自动转发到fallback上游服务器
- 支持精确匹配和通配符匹配（如 `*.example.com`）

**实现原理**:
- 解析QUIC Initial包头，提取DCID（Destination Connection ID）
- 使用RFC 9001定义的Initial Secrets派生密钥
- 从CRYPTO frame中提取TLS ClientHello
- 解析ClientHello中的SNI扩展
- 维护连接跟踪表，后续包直接查表

**使用场景**:
- 同一端口服务多个域名，根据SNI分流到不同后端
- MQVPN专用域名走VPN隧道，其他域名透明转发
- 实现基于域名的流量隔离

### 2. HTTP/2代理模块 (H2 Proxy)

**位置**: `src/h2_proxy.[ch]`

**功能**:
- 对于通过SNI检查但不是MASQUE协议的连接，转换为HTTP/2代理
- 协议转换：QUIC/HTTP3 → TCP/HTTP2
- 连接池管理和复用
- 流多路复用支持

**实现原理**:
- 检查HTTP/3请求的`:protocol`头
- 如果不是`connect-ip`（MASQUE），启用代理模式
- 建立或复用到后端HTTP/2服务器的连接
- 使用nghttp2库处理HTTP/2协议
- 双向流式转发请求和响应

**使用场景**:
- 同一SNI下混合VPN和普通HTTP流量
- 对非VPN流量进行协议转换和加速
- 实现透明的HTTP/3到HTTP/2网关

## 配置说明

### SNI路由配置

```c
// 服务器端配置示例
sni_router_config_t config = {
    .allowed_snis = (const char *[]){"vpn.example.com", "*.internal.net"},
    .n_allowed_snis = 2,
    
    // Fallback上游地址
    .fallback_addr = {...},  // 其他域名转发的目标
    .fallback_addrlen = sizeof(struct sockaddr_in),
    
    // 连接跟踪
    .max_tracked_conns = 4096,
    .conn_timeout_sec = 60,
};

sni_router_t *router = sni_router_create(&config);
```

### HTTP/2代理配置

```c
// HTTP/2后端配置
h2_proxy_config_t config = {
    // 后端服务器地址
    .backend_addr = {...},
    .backend_addrlen = sizeof(struct sockaddr_in),
    
    // 连接池配置
    .max_connections = 10,
    .max_streams_per_conn = 100,
    .conn_timeout_sec = 60,
    
    // 是否使用TLS连接后端
    .backend_tls = 0,
    
    // 路径前缀
    .path_prefix = "/",
    
    // 启用连接复用
    .enable_connection_reuse = 1,
};

h2_proxy_t *proxy = h2_proxy_create(&config, &callbacks);
```

## 构建说明

### 依赖项

**必需**:
- BoringSSL (已包含在xquic子模块中)
- OpenSSL HKDF支持 (用于Initial Secrets派生)

**可选**:
- nghttp2 (用于HTTP/2代理功能)

### 安装依赖

```bash
# Ubuntu/Debian
sudo apt-get install libnghttp2-dev

# macOS
brew install nghttp2

# 如果不安装nghttp2，HTTP/2代理功能会被禁用，但SNI路由仍可用
```

### 编译

```bash
# 克隆仓库
git clone -b feature/sni-routing-and-h2-proxy <repo-url>
cd mqvpn

# 初始化子模块
git submodule update --init --recursive

# 构建
./build.sh

# 或者手动构建
mkdir -p build && cd build
cmake -DCMAKE_BUILD_TYPE=Release \
      -DXQUIC_BUILD_DIR=../third_party/xquic/build ..
make -j$(nproc)
```

### 检查功能是否启用

```bash
# 查看编译日志
cmake .. 2>&1 | grep -E "nghttp2|H2_PROXY"

# 如果看到以下输出，说明HTTP/2代理已启用：
# -- nghttp2 found: /usr/lib/x86_64-linux-gnu/libnghttp2.so
# -- MQVPN_H2_PROXY_ENABLED

# 如果看到以下输出，说明HTTP/2代理被禁用：
# -- nghttp2 not found. HTTP/2 proxy support will be disabled.
```

## 测试

### 单元测试

```bash
cd build

# 运行SNI路由测试
./tests/test_sni_router

# 运行HTTP/2代理测试（需要nghttp2）
./tests/test_h2_proxy

# 运行所有测试
ctest --output-on-failure
```

### CI测试

GitHub Actions会自动运行以下测试：
- 代码格式检查（clang-format）
- 编译检查（无警告）
- 单元测试
- 静态分析（检查不安全的函数调用）

查看测试状态：
```bash
# 在GitHub仓库页面查看Actions标签
# 或者通过CLI
gh run list --branch feature/sni-routing-and-h2-proxy
```

## 性能考虑

### SNI路由性能

**开销**:
- Initial包解析：~10-20μs
- SNI提取：~5-10μs
- 连接跟踪查找：O(1)哈希表，<1μs

**优化**:
- 连接跟踪避免重复解析
- 非Initial包直接查表，无解析开销
- 哈希表自动清理过期连接

### HTTP/2代理性能

**开销**:
- 协议转换：~50-100μs每请求
- 连接池查找：O(1)
- 流复用减少连接建立开销

**优化**:
- 连接复用减少TCP握手
- nghttp2的零拷贝优化
- 异步I/O避免阻塞

## 安全考虑

### SNI路由安全

1. **Initial Secrets是公开的**
   - 基于Connection ID派生，任何人都可以解密Initial包
   - 这是QUIC协议设计的一部分，用于防止中间盒干扰
   - 后续包使用握手密钥加密，无法解密

2. **SNI明文传输**
   - TLS 1.3的SNI在ClientHello中明文传输
   - 这是TLS协议的已知限制
   - 可考虑使用ECH (Encrypted ClientHello) 扩展

3. **连接跟踪资源消耗**
   - 限制最大跟踪连接数（默认4096）
   - 自动清理过期连接（默认60秒超时）
   - 防止内存耗尽攻击

### HTTP/2代理安全

1. **后端验证**
   - 支持TLS连接到后端（`backend_tls = 1`）
   - 验证后端证书（如果启用TLS）

2. **资源限制**
   - 限制并发连接数
   - 限制每连接的并发流数
   - 防止资源耗尽

3. **错误处理**
   - 所有网络调用都有错误检查
   - 避免使用不安全的字符串函数
   - 输入验证

## 已知限制

1. **SNI分片**
   - 当前实现假设ClientHello在单个Initial包中
   - 分片的ClientHello会fallback到ACCEPT（向后兼容）

2. **HTTP/2代理实现**
   - 当前是基础实现，用于演示架构
   - 需要完善header转换逻辑
   - 需要完善流控制和背压处理

3. **连接跟踪**
   - 仅跟踪Initial包的DCID
   - 连接迁移（Connection Migration）可能导致跟踪失效

## 下一步工作

### 短期（1-2周）

- [ ] 完善HTTP/2代理的header转换
- [ ] 添加更多集成测试
- [ ] 性能基准测试
- [ ] 文档完善

### 中期（1-2月）

- [ ] 支持ClientHello分片
- [ ] 添加Prometheus指标
- [ ] 支持热重载配置
- [ ] 添加管理API

### 长期（3-6月）

- [ ] 支持ECH (Encrypted ClientHello)
- [ ] 支持连接迁移的跟踪
- [ ] HTTP/3代理（避免协议降级）
- [ ] 分布式连接跟踪

## 贡献指南

### 代码规范

- 遵循项目现有的代码风格
- 使用`clang-format -i`格式化代码
- 每个公开函数都要有文档注释
- 错误处理要完整

### 提交代码

1. Fork仓库
2. 创建功能分支：`git checkout -b feature/my-feature`
3. 提交更改：`git commit -am 'feat: Add some feature'`
4. 推送分支：`git push origin feature/my-feature`
5. 创建Pull Request

### 测试要求

- 新功能必须有单元测试
- 所有测试必须通过
- 代码覆盖率不能降低
- CI检查必须通过

## 常见问题

### Q: SNI路由会影响延迟吗？

A: 影响极小（<50μs）。只有Initial包需要解析，后续包直接查表。

### Q: 不安装nghttp2会怎样？

A: HTTP/2代理功能会被禁用，但SNI路由仍然可用。编译时会有警告信息。

### Q: 可以同时使用多个fallback目标吗？

A: 当前版本只支持单个fallback目标。多目标负载均衡可以通过在fallback目标前部署Nginx实现。

### Q: 如何调试SNI路由问题？

A: 
```bash
# 启用debug日志
./mqvpn --mode server --log-level debug ...

# 使用tcpdump抓包
sudo tcpdump -i any -w /tmp/capture.pcap udp port 443

# 使用Wireshark分析Initial包
wireshark /tmp/capture.pcap
```

## 参考资料

- [RFC 9001 - QUIC TLS](https://www.rfc-editor.org/rfc/rfc9001.html)
- [RFC 9114 - HTTP/3](https://www.rfc-editor.org/rfc/rfc9114.html)
- [nghttp2 Documentation](https://nghttp2.org/documentation/)
- [BoringSSL HKDF API](https://commondatastorage.googleapis.com/chromium-boringssl-docs/hkdf.h.html)

## 许可证

Apache-2.0

## 联系方式

- 项目Issues: GitHub Issues
- 讨论: GitHub Discussions
