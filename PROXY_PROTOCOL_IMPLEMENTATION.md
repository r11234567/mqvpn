# Proxy Protocol v2 实现总结

## 概述

为 mqvpn 的 HTTP/2 代理模块添加了 **Proxy Protocol v2** 支持，使后端服务器（如 Nginx）能够获取真实的客户端 IP 地址和端口。

## 实现内容

### 1. 核心代码修改

#### `src/h2_proxy.h`
- 在 `h2_proxy_config_t` 中添加 `backend_proxy_protocol` 字段
- 在 `h2_backend_conn_t` 中添加 `proxy_protocol_sent` 状态标记

#### `src/h2_proxy.c`
- 实现 `build_proxy_protocol_v2()` 函数，构造符合 HAProxy Proxy Protocol v2 规范的头部
- 在 `backend_conn_on_write()` 中，连接建立后首先发送 Proxy Protocol 头部
- 支持 IPv4 和 IPv6 地址格式
- 幂等性保证：每个连接只发送一次头部

#### `src/mqvpn_internal.h`
- 添加配置字段 `proxy_h2_backend_proxy_protocol`

#### `src/config.c`
- 添加配置解析：`Http2BackendProxyProtocol` (INI) / `http2_backend_proxy_protocol` (JSON)

#### `src/mqvpn_server.c`
- 在 `svr_h2_proxy_init()` 中将配置传递给 H2 代理模块

### 2. 测试基础设施

#### `tests/test_h2_proxy_protocol.sh`
- 完整的端到端测试脚本
- 创建测试用的 TLS 证书
- 启动支持 Proxy Protocol 的 Nginx 后端
- 验证客户端真实 IP 传递到后端

#### `.github/workflows/h2-proxy-protocol.yml`
- 独立的 GitHub Actions workflow
- 仅在 H2 代理相关文件修改时触发
- 不影响主 CI 流程

#### `tests/nginx-with-proxy-protocol.conf`
- 示例 Nginx 配置
- 同时支持 Proxy Protocol (8444端口) 和直接访问 (8443端口)
- 通过 `$proxy_protocol_addr` 变量记录真实客户端 IP

### 3. 文档

#### `docs/h2-proxy-protocol.md`
- 完整的使用文档
- 配置示例
- Nginx 配置指南
- 工作原理说明
- 故障排查指南

## 配置示例

### mqvpn 服务端配置 (JSON)

```json
{
    "role": "server",
    "listen": "[::]:4433",
    "cert": "/path/to/cert.pem",
    "key": "/path/to/key.pem",
    "proxy": {
        "enabled": true,
        "sni": "vpn.example.com",
        "http2_backend": "127.0.0.1:8443",
        "http2_backend_proxy_protocol": true
    }
}
```

### Nginx 配置

```nginx
# 端口 8444：支持 Proxy Protocol
server {
    listen 8444 http2 proxy_protocol;
    server_name _;
    
    set_real_ip_from 127.0.0.1;
    real_ip_header proxy_protocol;
    
    location / {
        add_header X-Real-IP $proxy_protocol_addr;
        add_header X-Real-Port $proxy_protocol_port;
        return 200 "Client: $proxy_protocol_addr:$proxy_protocol_port\n";
    }
}

# 端口 8443：直接访问（不使用 Proxy Protocol）
server {
    listen 8443 ssl http2;
    server_name _;
    
    ssl_certificate /path/to/cert.pem;
    ssl_certificate_key /path/to/key.pem;
    
    location / {
        add_header X-Real-IP $remote_addr;
        return 200 "Client: $remote_addr:$remote_port\n";
    }
}
```

## 技术细节

### Proxy Protocol v2 格式

```
0                   1                   2                   3
0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                                                               |
+                                                               +
|                  Signature (12 bytes)                         |
+                                                               +
|                                                               |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|Version|Command|   AF  | Proto |        Address Length         |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                      Source Address                           |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                   Destination Address                         |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|          Source Port          |       Destination Port        |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
```

- **Signature**: `\r\n\r\n\0\r\nQUIT\n` (12 bytes)
- **Version**: `0x2` (版本 2)
- **Command**: `0x1` (PROXY command)
- **Address Family**: `0x1` (IPv4) / `0x2` (IPv6)
- **Protocol**: `0x1` (TCP)

### 实现要点

1. **幂等性**：通过 `proxy_protocol_sent` 标志确保每个后端连接只发送一次头部
2. **时机**：在 TCP 连接建立后、发送任何 HTTP/2 数据前发送
3. **错误处理**：发送失败时记录日志，连接保持打开（让后续写操作触发断开）
4. **兼容性**：仅在配置明确启用时发送，默认关闭

## 测试运行

### 本地测试

```bash
cd mqvpn
sudo tests/test_h2_proxy_protocol.sh
```

### CI 触发条件

以下文件修改时会触发 H2 Proxy Protocol 测试：
- `src/h2_proxy.c`
- `src/h2_proxy.h`
- `src/sni_router.c`
- `tests/test_h2_proxy_protocol.sh`
- `tests/nginx-with-proxy-protocol.conf`
- `.github/workflows/h2-proxy-protocol.yml`

## 验证方法

1. 启动带有 Proxy Protocol 支持的 Nginx
2. 启动 mqvpn 服务端（启用 `http2_backend_proxy_protocol`）
3. 从客户端发起请求
4. 检查 Nginx 日志或响应头，确认 `$proxy_protocol_addr` 为客户端真实 IP

## 安全考虑

- **仅在内部使用**：Proxy Protocol 不应暴露到公网，因为任何人都可以伪造头部
- **信任来源**：Nginx 的 `set_real_ip_from` 指令应限制为本地回环地址
- **端口隔离**：分离支持 Proxy Protocol 的端口和直接访问的端口

## 未来改进

- [ ] 支持 Proxy Protocol v1（文本格式，可选）
- [ ] 支持 TLV 扩展（传递额外元数据）
- [ ] 添加性能基准测试

## 相关文件

- 实现：`src/h2_proxy.{c,h}`
- 配置：`src/config.c`, `src/mqvpn_internal.h`
- 测试：`tests/test_h2_proxy_protocol.sh`
- CI：`.github/workflows/h2-proxy-protocol.yml`
- 文档：`docs/h2-proxy-protocol.md`
