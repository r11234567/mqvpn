# HTTP/2 Proxy with Proxy Protocol v2

mqvpn 的 H2 代理模块支持 Proxy Protocol v2，可以在代理转发时保留真实客户端的 IP 地址和端口。

## 功能概述

当 mqvpn 作为 QUIC/HTTP3 到 HTTP/2 的代理时，后端服务器（如 Nginx）看到的客户端地址是 mqvpn 服务器的地址（通常是 127.0.0.1）。启用 Proxy Protocol 后，mqvpn 会在建立到后端的 TCP 连接后立即发送一个 Proxy Protocol v2 头部，其中包含：

- 真实客户端的 IP 地址
- 真实客户端的端口号
- 目标服务器的 IP 地址和端口（后端地址）

后端服务器（如 Nginx）可以解析这个头部，获取真实客户端信息用于日志、访问控制等。

## 配置

### mqvpn 服务端配置

在服务端 JSON 配置中启用 Proxy Protocol：

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

**配置项说明：**

- `proxy.http2_backend`: 后端 HTTP/2 服务器地址（必须支持 Proxy Protocol）
- `proxy.http2_backend_proxy_protocol`: 是否启用 Proxy Protocol v2（默认 false）

### Nginx 后端配置

Nginx 需要在 `listen` 指令中添加 `proxy_protocol` 参数：

```nginx
http {
    server {
        # 启用 Proxy Protocol 监听
        listen 127.0.0.1:8443 proxy_protocol;
        listen [::1]:8443 proxy_protocol;
        server_name _;

        # 信任来自 localhost 的 Proxy Protocol 头部
        set_real_ip_from 127.0.0.1;
        set_real_ip_from ::1;
        real_ip_header proxy_protocol;

        location / {
            # 将真实客户端信息传递给上游
            proxy_pass http://backend;
            proxy_set_header X-Real-IP $proxy_protocol_addr;
            proxy_set_header X-Real-Port $proxy_protocol_port;
            proxy_set_header X-Forwarded-For $proxy_protocol_addr;
        }
    }

    upstream backend {
        server 127.0.0.1:9000;
    }
}
```

**关键指令：**

- `listen ... proxy_protocol`: 告诉 Nginx 期望连接带有 Proxy Protocol 头部
- `set_real_ip_from`: 设置信任的 Proxy Protocol 来源（仅信任本地连接）
- `real_ip_header proxy_protocol`: 使用 Proxy Protocol 提供的地址作为真实 IP
- `$proxy_protocol_addr`: 客户端 IP 变量
- `$proxy_protocol_port`: 客户端端口变量

## 同时支持 Proxy Protocol 和直接访问

推荐使用**专用端口方式**（最简单、性能最好）：

```nginx
http {
    # 端口 8443: 从 mqvpn 接收 Proxy Protocol 流量
    server {
        listen 127.0.0.1:8443 proxy_protocol;
        server_name _;
        
        set_real_ip_from 127.0.0.1;
        real_ip_header proxy_protocol;
        
        location / {
            proxy_pass http://backend;
            proxy_set_header X-Real-IP $proxy_protocol_addr;
        }
    }

    # 端口 8444: 直接访问（用于测试、内部调用等）
    server {
        listen 127.0.0.1:8444;
        server_name _;
        
        location / {
            proxy_pass http://backend;
            proxy_set_header X-Real-IP $remote_addr;
        }
    }
}
```

更复杂的混合端口方案见 `docs/nginx-proxy-protocol-example.conf`。

## Proxy Protocol v2 格式

mqvpn 实现的是 Proxy Protocol **版本 2**（二进制格式），结构如下：

```
12 bytes  - 签名: \x0D\x0A\x0D\x0A\x00\x0D\x0A\x51\x55\x49\x54\x0A
1 byte    - 版本和命令: 0x21 (v2 + PROXY)
1 byte    - 地址族和协议: 0x11 (IPv4+STREAM) 或 0x21 (IPv6+STREAM)
2 bytes   - 地址数据长度（大端序）
N bytes   - 地址数据:
              IPv4: src_ip(4) + dst_ip(4) + src_port(2) + dst_port(2) = 12 bytes
              IPv6: src_ip(16) + dst_ip(16) + src_port(2) + dst_port(2) = 36 bytes
```

**示例（IPv4）：**

```
0000: 0d 0a 0d 0a 00 0d 0a 51 55 49 54 0a  # 签名
000c: 21                                    # v2 + PROXY
000d: 11                                    # IPv4 + STREAM
000e: 00 0c                                 # 长度 = 12 bytes
0010: c0 a8 01 64                           # 源 IP: 192.168.1.100
0014: 7f 00 00 01                           # 目标 IP: 127.0.0.1
0018: 1f 90                                 # 源端口: 8080 (大端序)
001a: 20 eb                                 # 目标端口: 8443 (大端序)
```

## 安全考虑

1. **仅在本地连接上使用**

   Proxy Protocol **不包含认证机制**，任何能连接到后端的客户端都可以伪造客户端 IP。因此：
   
   - 后端监听地址应该绑定到 `127.0.0.1` 或 `::1`
   - 如果后端必须监听公网地址，务必使用防火墙限制访问
   - Nginx 的 `set_real_ip_from` 必须只信任 mqvpn 服务器地址

2. **错误配置的风险**

   - 如果后端启用了 `proxy_protocol` 但 mqvpn 没有发送，连接会失败
   - 如果 mqvpn 发送了 Proxy Protocol 但后端没有启用，后端会把头部当作 HTTP 数据（导致解析错误）
   - 确保配置一致：`h2_proxy_protocol: true` ↔ `listen ... proxy_protocol`

3. **日志记录**

   启用 Proxy Protocol 后，mqvpn 会在日志中记录：
   
   ```
   [INFO] Sent Proxy Protocol v2 (28 bytes)
   ```
   
   如果发送失败，会记录错误并关闭后端连接。

## 测试

运行集成测试验证 Proxy Protocol 功能：

```bash
cd build
sudo ../tests/test_h2_proxy_protocol.sh
```

测试脚本会：

1. 启动 Nginx（带 Proxy Protocol 支持）
2. 启动 mqvpn 服务器（启用 Proxy Protocol）
3. 发送 HTTP/3 请求
4. 验证 Nginx 收到的是真实客户端 IP（而非 127.0.0.1）

## 故障排查

### 问题：后端连接立即关闭

**可能原因：** 后端没有启用 `proxy_protocol`，但 mqvpn 发送了 Proxy Protocol 头部。

**解决方案：** 在 Nginx 的 `listen` 指令中添加 `proxy_protocol` 参数。

### 问题：日志显示客户端 IP 仍然是 127.0.0.1

**可能原因 1：** Nginx 没有配置 `set_real_ip_from` 和 `real_ip_header proxy_protocol`。

**解决方案：**

```nginx
set_real_ip_from 127.0.0.1;
real_ip_header proxy_protocol;
```

**可能原因 2：** mqvpn 的 `h2_proxy_protocol` 没有设置为 `true`。

**解决方案：** 检查服务端配置文件。

### 问题：Nginx 报错 "broken header"

**可能原因：** Nginx 启用了 `proxy_protocol`，但 mqvpn 没有发送（或配置为 `false`）。

**解决方案：** 确保 mqvpn 配置中 `h2_proxy_protocol: true`，或者移除 Nginx `listen` 中的 `proxy_protocol`。

## 性能影响

Proxy Protocol v2 头部开销：

- IPv4: 28 bytes
- IPv6: 52 bytes

开销极小，且只在建立后端连接时发送一次（连接复用时不会重复发送）。

## 参考资料

- [Proxy Protocol Specification](https://www.haproxy.org/download/2.8/doc/proxy-protocol.txt)
- [Nginx ngx_http_realip_module](http://nginx.org/en/docs/http/ngx_http_realip_module.html)
- [HAProxy Proxy Protocol Support](https://www.haproxy.com/blog/haproxy/proxy-protocol/)
