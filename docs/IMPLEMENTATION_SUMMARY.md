# 实施总结：SNI路由和HTTP/2代理功能

## 📋 项目概述

本分支添加了SNI路由与HTTP/2代理的API、构建入口和原型实现。经2026-08-01
代码复核，这两个模块尚未接入`mqvpn_server.c`，也尚未形成可部署的端到端功能。

> **当前状态：实验性原型。** `sni_router.c`尚未实现QUIC Initial包的
> Header Protection移除和AEAD解密；`h2_proxy.c`尚未实现H3/H2 header、body和
> response转发。不得把当前模块用于生产流量分流。

## 当前实现

### 核心功能实现

#### 1. SNI路由模块
**文件**: `src/sni_router.[ch]`

**实现细节**:
- QUIC长包头的部分字段解析
- Initial Secrets派生（基于Connection ID）
- 明文TLS ClientHello解析器和SNI匹配
- 连接跟踪（哈希表，O(1)查找）
- Fallback转发机制
- 支持精确匹配和通配符（`*.example.com`）

**未完成**:
- QUIC Header Protection移除和Initial payload AEAD解密
- CRYPTO frame重组与分片ClientHello处理
- 与服务端收包和转发路径集成
- 真实QUIC报文端到端测试和性能基准

#### 2. HTTP/2代理模块
**文件**: `src/h2_proxy.[ch]`

**实现细节**:
- nghttp2会话、连接池和统计结构骨架
- POSIX非阻塞TCP连接管理
- H3侧和事件循环侧的API定义

**未完成**:
- nghttp2实际回调注册
- H3 request header/body到H2的提交
- H2 response到H3的回传和流关闭
- backend TLS
- 与`mqvpn_server.c`和事件循环集成
- Windows socket实现（构建系统现仅在Linux探测并启用nghttp2）

**架构**:
```
Client (HTTP/3) → mqvpn (代理) → Backend (HTTP/2)
     ↓                ↓                 ↓
   QUIC          协议转换             TCP
```

### 测试和质量保证

#### 单元测试
**文件**: `tests/test_sni_router.c`, `tests/test_h2_proxy.c`

- SNI匹配测试（精确、通配符、不匹配）
- cleanup API空表调用测试
- UDP fallback API冒烟调用
- HTTP/2代理配置、生命周期和初始统计测试

当前测试不覆盖真实QUIC解密、连接跟踪命中、H3/H2转发或端到端代理行为。

#### CI/CD集成
**文件**: `.github/workflows/sni-h2-proxy-tests.yml`

**三个Job**:
1. **test-sni-router**: 编译和单元测试
2. **integration-test**: 集成测试和证书生成
3. **static-analysis**: 代码格式和安全检查

**检查项**:
- ✅ 代码格式（clang-format 18.1.3）
- ✅ 新增C文件格式检查
- ✅ 不安全函数检测
- ⚠️ 工作流中的“integration-test”目前只构建并生成证书，不发送测试流量
- ⚠️ H2测试失败被shell命令忽略，不能作为功能正确性门禁

#### 本地验证脚本
**文件**: `scripts/check_sni_h2_feature.sh`

快速检查：
- 文件完整性
- 安全性
- 构建配置
- 文档完整性

### 构建系统集成

#### CMakeLists.txt修改
1. **新增源文件**:
   ```cmake
   src/sni_router.c
   src/h2_proxy.c
   ```

2. **依赖检测**:
   ```cmake
   find_package(nghttp2)  # 可选
   ```

3. **条件编译**:
   ```cmake
   if(MQVPN_ENABLE_H2_PROXY)
       target_compile_definitions(mqvpn_lib PRIVATE MQVPN_H2_PROXY_ENABLED)
   endif()
   ```

4. **测试目标**:
   ```cmake
   add_executable(test_sni_router ...)
   add_executable(test_h2_proxy ...)
   ```

### 文档

#### 主文档
**文件**: `docs/SNI_ROUTING_H2_PROXY.md` (350+ 行)

**内容**:
- 功能概述和使用场景
- 配置说明和示例
- 构建和测试指南
- 性能考虑和基准
- 安全分析
- 已知限制
- 故障排除和FAQ
- 未来路线图

#### API文档
- 头文件中的完整注释
- 每个公开函数的说明
- 参数和返回值文档
- 使用示例

## 🔧 修复的问题

### 第一轮CI失败
1. ✅ 代码格式问题 → 使用clang-format-18格式化
2. ✅ xquic构建路径 → 使用绝对路径`${GITHUB_WORKSPACE}`
3. ✅ strerror误报 → 改进静态分析正则表达式

### 第二轮CI失败
4. ✅ BoringSSL头文件路径 → 添加`BORINGSSL_INCLUDE_DIR`到测试目标

### Windows全局CI失败
5. ✅ 修复Windows SDK的`X509_NAME`宏与BoringSSL类型名冲突
6. ✅ fallback socket在Windows使用`SOCKET`，避免64位句柄截断
7. ✅ 按Winsock的`sendto`签名检查并转换`size_t`长度

## 📊 代码统计

```
语言              文件数    代码行数    注释行数
C                   4       1800+       400+
C Header            2       200+        150+
CMake               1       50+         20+
YAML                1       150+        30+
Markdown            1       350+        -
Bash                1       75+         10+
────────────────────────────────────────────
总计                10      2625+       610+
```

## 🎯 技术亮点

### 1. 标准相关基础
- **RFC 9001**: 已实现Initial secret派生，报文解密尚未实现
- **TLS 1.3**: 已有明文ClientHello SNI解析器
- **nghttp2**: 已建立会话管理骨架，协议桥接尚未实现

### 2. 性能优化
- 连接跟踪避免重复解析
- 哈希表O(1)查找
- 非阻塞I/O
- 连接复用减少握手

### 3. 安全考虑
- 无unsafe字符串函数
- 基础分配和socket错误处理
- 可配置哈希表大小和超时字段
- 部分输入边界检查

### 4. 可维护性
- 清晰的模块化设计
- 完整的文档
- API级冒烟测试
- 遵循项目代码规范

## 📈 Git提交历史

```
642c16f chore: Add sanity check script
216ee17 fix: Add BoringSSL include path for test_sni_router
28269fb fix: Correct SSL_PATH in CI workflow
c4db45f style: Apply clang-format to SNI router and H2 proxy modules
261fd95 docs: Add comprehensive documentation
14edd59 feat: Add SNI routing and HTTP/2 proxy support
```

## 构建与原型验证

### 构建
```bash
git clone -b feature/sni-routing-and-h2-proxy <repo>
cd mqvpn
git submodule update --init --recursive

# 安装依赖（可选）
sudo apt-get install libnghttp2-dev

# 构建
./build.sh
```

### API草案示例

**服务器端**:
```c
// 1. 配置SNI路由
const char *allowed_snis[] = {"vpn.example.com", "*.internal.net"};
sni_router_config_t sni_config = {
    .allowed_snis = allowed_snis,
    .n_allowed_snis = 2,
    .fallback_addr = fallback_addr,
    .max_tracked_conns = 4096,
    .conn_timeout_sec = 60,
};

// 2. 配置HTTP/2代理
h2_proxy_config_t h2_config = {
    .backend_addr = backend_addr,
    .max_connections = 10,
    .max_streams_per_conn = 100,
    .backend_tls = 0,
    .enable_connection_reuse = 1,
};
```

### 测试
```bash
cd build

# 单元测试
./tests/test_sni_router
./tests/test_h2_proxy

# 所有测试
ctest --output-on-failure

# 本地验证
../scripts/check_sni_h2_feature.sh
```

## 📝 上线前必需工作

### 短期（1-2周）
- [ ] 完成QUIC Initial Header Protection和AEAD解密
- [ ] 解析并重组CRYPTO frame，不扫描密文寻找ClientHello
- [ ] 完善HTTP/2代理的header转换逻辑
- [ ] 实现request/response body和关闭状态双向转发
- [ ] 添加端到端集成测试
- [ ] 性能基准测试和优化
- [ ] 集成到主服务器代码（mqvpn_server.c）

### 中期（1-2月）
- [ ] 支持ClientHello分片场景
- [ ] 添加Prometheus指标导出
- [ ] 支持配置热重载
- [ ] 添加管理API（动态更新SNI列表）

### 长期（3-6月）
- [ ] 支持ECH (Encrypted ClientHello)
- [ ] 支持QUIC连接迁移的跟踪
- [ ] 实现HTTP/3代理（避免协议降级）
- [ ] 分布式连接跟踪（多服务器场景）

## 🎓 学习价值

### 涉及的技术栈
- QUIC协议（RFC 9001, 9114）
- TLS 1.3 (ClientHello解析)
- HTTP/2和HTTP/3
- BoringSSL (HKDF密钥派生)
- nghttp2库
- 非阻塞I/O和事件驱动
- CMake构建系统
- GitHub Actions CI/CD

### 设计模式
- 工厂模式（连接创建）
- 对象池模式（连接池）
- 观察者模式（事件回调）
- 策略模式（SNI匹配）

## 📞 支持

- **文档**: `docs/SNI_ROUTING_H2_PROXY.md`
- **示例**: 测试文件中的用法
- **问题**: GitHub Issues
- **讨论**: GitHub Discussions

## 📜 许可证

Apache-2.0 License

---

**最近复核时间**: 2026-08-01
**实现状态**: ⚠️ 实验性原型，核心数据路径未完成
**测试覆盖**: ⚠️ API冒烟测试，不含协议和端到端覆盖
**CI状态**: 🔄 Windows兼容修复待GitHub Actions验证

**Co-Authored-By**: Claude Opus 5 (1M context) <noreply@anthropic.com>
