# H2 Proxy Protocol v2 Implementation - 完成清单

## 实现完成 ✅

### 代码修改

#### 1. 核心实现文件
- [x] `src/h2_proxy.h` - 添加 `backend_proxy_protocol` 配置字段
- [x] `src/h2_proxy.c` - 实现 Proxy Protocol v2 构造和发送逻辑
  - 添加 PP v2 常量定义
  - 实现 `build_proxy_protocol_v2()` 函数
  - 在 `submit_request()` 中发送 PP v2 头部
  - 添加 `proxy_protocol_sent` 状态跟踪

#### 2. 配置文件
- [x] `src/mqvpn_internal.h` - 添加 `proxy_h2_backend_proxy_protocol` 字段
- [x] `src/config.c` - 添加配置解析
  - INI: `[Proxy] Http2BackendProxyProtocol`
  - JSON: `proxy.http2_backend_proxy_protocol`

#### 3. 集成代码
- [x] `src/mqvpn_server.c` - 在 H2 proxy 初始化时传递配置
  - 修改 `h2_proxy_handle_request()` 调用，传递客户端地址
  - 在 `svr_h2_proxy_init()` 中设置 `backend_proxy_protocol`

### 测试基础设施

#### 4. 测试脚本
- [x] `tests/test_h2_proxy_protocol.sh` - 端到端测试脚本
  - 创建测试证书
  - 启动 Nginx with Proxy Protocol
  - 启动 mqvpn server
  - 验证客户端 IP 传递

#### 5. CI/CD
- [x] `.github/workflows/h2-proxy-protocol.yml` - 独立 workflow
  - 构建 mqvpn with H2 proxy support
  - 运行单元测试
  - 运行 E2E 测试
  - 仅在相关文件修改时触发

### 文档

#### 6. 用户文档
- [x] `docs/h2-proxy-protocol.md` - 完整使用文档
  - 功能概述
  - 配置示例（mqvpn + Nginx）
  - Proxy Protocol v2 格式说明
  - 安全考虑
  - 故障排查指南

#### 7. 配置示例
- [x] `docs/nginx-proxy-protocol-example.conf` - Nginx 配置示例
  - 专用端口方案（推荐）
  - 混合端口方案（高级）
  - 完整注释说明

#### 8. 实现文档
- [x] `PROXY_PROTOCOL_IMPLEMENTATION.md` - 实现总结
  - 技术细节
  - 修改文件清单
  - 测试说明

## 配置选项

### mqvpn 配置

**JSON 格式：**
```json
{
    "proxy": {
        "enabled": true,
        "http2_backend": "127.0.0.1:8443",
        "http2_backend_proxy_protocol": true
    }
}
```

**INI 格式：**
```ini
[Proxy]
Enabled = true
Http2Backend = 127.0.0.1:8443
Http2BackendProxyProtocol = true
```

### Nginx 配置

```nginx
server {
    listen 8443 http2 proxy_protocol;
    
    set_real_ip_from 127.0.0.1;
    real_ip_header proxy_protocol;
    
    location / {
        proxy_set_header X-Real-IP $proxy_protocol_addr;
        # ... 其他配置
    }
}
```

## 关键技术点

### Proxy Protocol v2 格式
- **签名**: 12 bytes `\r\n\r\n\0\r\nQUIT\n`
- **版本+命令**: 1 byte `0x21` (v2 + PROXY)
- **地址族+协议**: 1 byte `0x11` (IPv4+TCP) 或 `0x21` (IPv6+TCP)
- **地址长度**: 2 bytes (big-endian)
- **地址数据**: 12 bytes (IPv4) 或 36 bytes (IPv6)

### 发送时机
- 在 TCP 连接建立后
- 在发送任何 HTTP/2 数据前
- 每个连接只发送一次（通过 `proxy_protocol_sent` 标志）

### 错误处理
- 构造失败：记录日志，继续请求（不发送 PP 头部）
- 发送失败：记录日志，拒绝请求（返回 -1）
- 后端不支持：Nginx 会立即断开连接

## 测试验证

### 本地测试
```bash
cd mqvpn/build
sudo ../tests/test_h2_proxy_protocol.sh
```

### 预期输出
```
=== H2 Proxy Protocol Test ===
[1/6] Generating test certificates...
[2/6] Creating Nginx config...
[3/6] Starting Nginx...
[4/6] Testing direct Nginx access...
[5/6] Creating mqvpn server config...
[6/6] Starting mqvpn server with H2 proxy...

=== Testing H2 Proxy with Proxy Protocol ===
✓ Proxy Protocol test PASSED
  Real client IP: 192.168.1.100:54321
```

## 安全最佳实践

1. **仅在本地回环使用**
   - 后端监听 `127.0.0.1` 或 `::1`
   - 不要暴露到公网

2. **信任源限制**
   ```nginx
   set_real_ip_from 127.0.0.1;
   set_real_ip_from ::1;
   # 不要添加其他地址
   ```

3. **配置一致性**
   - mqvpn: `http2_backend_proxy_protocol: true`
   - Nginx: `listen ... proxy_protocol`
   - 两边必须匹配

## 已知限制

- 仅支持 TCP 传输（不支持 UDP）
- 不支持 Proxy Protocol v1（文本格式）
- 不支持 TLV 扩展（但可以在未来添加）

## 未来改进

- [ ] 支持 Proxy Protocol v1（向后兼容）
- [ ] 支持 TLV 扩展（传递额外元数据，如 SSL 信息）
- [ ] 性能基准测试
- [ ] 支持连接池中的连接复用策略优化

## 提交信息建议

```
feat: Add Proxy Protocol v2 support for H2 backend

- Implement Proxy Protocol v2 header construction (IPv4/IPv6)
- Send PP v2 on first request of each backend connection
- Add config option: proxy.http2_backend_proxy_protocol
- Add E2E test with Nginx backend
- Add documentation and examples

This allows backend servers (like Nginx) to see the real client IP
instead of the mqvpn proxy IP when using H2 backend forwarding.

Closes #XXX
```

## 相关 PR/Issues

- [ ] 创建 PR 合并到主分支
- [ ] 更新 CHANGELOG.md
- [ ] 更新 server-fallback-proxy.md 文档
- [ ] 标记版本号（建议 minor version bump）

## 验证检查表

构建和测试：
- [ ] Linux 构建成功
- [ ] 单元测试通过 (`test_h2_proxy`)
- [ ] E2E 测试通过 (`test_h2_proxy_protocol.sh`)
- [ ] CI 工作流触发并通过

功能验证：
- [ ] Proxy Protocol 头部格式正确（用 Wireshark 验证）
- [ ] 后端收到真实客户端 IP
- [ ] 配置禁用时不发送 PP 头部
- [ ] IPv4 和 IPv6 都能正常工作

文档完整性：
- [ ] 用户文档清晰易懂
- [ ] 配置示例可直接使用
- [ ] 故障排查指南准确

## 完成时间

**实现日期**: 2024-XX-XX
**预计合并**: 待 PR 审核

---

**Status**: ✅ 实现完成，待测试和合并
