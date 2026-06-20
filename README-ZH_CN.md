# LLM Router

轻量级 LLM 模型路由网关 — 使用 .NET 10 AOT 编译为单一二进制文件。

## 功能特性

- **统一 API 入口**：客户端使用单一 API Key 访问，llmrouter 根据请求中的模型名自动路由到对应上游
- **Fallback 链**：主模型不可用时自动降级到备用模型，连接失败和上游 4xx/5xx 均触发 fallback
- **模型名映射**：客户端请求的模型名自动替换为上游实际模型名（config key 即上游模型名）
- **透明代理**：支持所有 OpenAI 兼容 API（`/v1/chat/completions`、`/v1/embeddings`、`/v1/images/*`、`/v1/audio/*` 等）
- **流式 SSE**：完整支持 `stream: true` 场景
- **默认参数注入**：可为每个模型配置默认参数（支持 number/string/bool/object），客户端未传时自动注入
- **健康检查**：后台轮询上游 `/v1/models` + 请求失败即时标记，30 秒 cooldown 自动重试
- **热更新**：`config.json` 修改后自动重载，无需重启
- **AOT 编译**：PublishAot 编译为单一原生二进制（约 12 MB），无 .NET 运行时依赖
- **Systemd 集成**：日志输出 stdout，部署为 systemd 服务后自动进入 journal

## 前置条件

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)（仅编译时需要，运行时不依赖）
- Linux x64 运行环境（glibc 2.23+ / musl 均可）

## 快速开始

### 1. 克隆项目

```bash
git clone https://github.com/gornear/llmrouter.git
cd llmrouter
```

### 2. 创建配置文件

项目提供了示例模板 `config.json.example`：

```bash
cp config.json.example config.json
```

编辑 `config.json`，配置您的上游模型：

```json
{
  "models": {
    "deepseek-v4-flash": {
      "baseUrl": "https://api.deepseek.com",
      "apiKey": "sk-your-deepseek-key",
      "defaultParams": {
        "temperature": 0.0,
        "top_p": 0.9,
        "thinking": {
          "type": "disabled"
        }
      }
    },
    "gemma4": {
      "baseUrl": "http://192.168.1.100:8000/v1",
      "apiKey": "your-api-key or not-needed",
      "fallback": ["deepseek-v4-flash"],
      "defaultParams": {
        "temperature": 0.0,
        "top_p": 0.9
      }
    }
  }
}
```

编辑 `appsettings.json`，设置监听地址和统一 API Key：

```json
{
  "Kestrel": {
    "Endpoints": {
      "Http": { "Url": "http://0.0.0.0:5000" }
    },
    "Limits": {
      "MaxConcurrentConnections": 200
    }
  },
  "ApiKey": "sk-your-unified-api-key",
  "RequestTimeoutSeconds": 300,
  "MaxRetryAttempts": 3,
  "DebugPrompt": false
}
```

> **提示**：`baseUrl` 可以写 `http://host:port` 也可以写 `http://host:port/v1`。

### 3. 编译

```bash
dotnet publish -c Release -o publish
```

产物在 `publish/llmrouter`（单一二进制，约 12 MB）。

> 也可用 `dotnet run` 直接开发调试，运行端口由 `appsettings.json` 中 `Kestrel.Endpoints.Http.Url` 决定。

### 4. 运行

```bash
./publish/llmrouter
```

输出：

```
info: LLM Router starting on http://0.0.0.0:5000
info: Health check service started (interval: 30s, timeout: 5s)
```

### 5. 测试

```bash
# 健康检查（无需认证）
curl http://localhost:5000/health

# 列出可用模型
curl -H "Authorization: Bearer sk-your-unified-api-key" http://localhost:5000/v1/models

# Chat Completions
curl -H "Authorization: Bearer sk-your-unified-api-key" \
     -H "Content-Type: application/json" \
     -d '{"model":"local-vllm","messages":[{"role":"user","content":"Hello!"}]}' \
     http://localhost:5000/v1/chat/completions

# 流式
curl -N -H "Authorization: Bearer sk-your-unified-api-key" \
     -H "Content-Type: application/json" \
     -d '{"model":"deepseek","messages":[{"role":"user","content":"Hello!"}],"stream":true}' \
     http://localhost:5000/v1/chat/completions
```

## 工作原理

```
客户端  ──POST /v1/chat/completions──→  LLM Router  ──POST /v1/chat/completions──→  上游 A
  "model":"local-vllm"                  │  替换 "model":"local-vllm"                    (vllm)
                                        │  → body 注入 "model":"deepseek"
                                        │
                                    上游 A 不可达？
                                        │
                                        └──POST /v1/chat/completions──→  上游 B
                                            "model":"deepseek"              (deepseek)
```


## API 规范

### 认证

除 `/health` 外所有端点需携带统一 API Key：

```
Authorization: Bearer sk-your-unified-api-key
```

### 端点

| 端点 | 方法 | 认证 | 说明 |
|------|------|------|------|
| `/health` | GET | 无 | 服务存活检测 |
| `/v1/models` | GET | Bearer | 返回 config.json 中配置的模型列表 |
| `/v1/chat/completions` | POST | Bearer | Chat Completions（支持 stream） |
| `/v1/{**path}` | ANY | Bearer | 透明代理到上游（embeddings/images/audio 等） |

### 错误响应

```json
{
  "error": {
    "message": "No healthy routes found for model 'unknown-model'",
    "type": "no_route"
  }
}
```

## 配置参考

### appsettings.json

| 参数 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `Kestrel.Endpoints.Http.Url` | string | `http://0.0.0.0:5000` | 监听地址 |
| `Kestrel.Limits.MaxConcurrentConnections` | int | 200 | 最大并发连接 |
| `ApiKey` | string | — | 统一 API Key（客户端使用） |
| `RequestTimeoutSeconds` | int | 300 | 上游请求超时（秒） |
| `MaxRetryAttempts` | int | 3 | Fallback 最大尝试次数 |
| `DebugPrompt` | bool | false | 设为 true 时，Debug 级别日志输出 messages 内容（注意隐私） |
| `HealthCheck.Enabled` | bool | true | 是否启用后台健康检查 |
| `HealthCheck.IntervalSeconds` | int | 30 | 轮询间隔（秒） |
| `HealthCheck.TimeoutSeconds` | int | 5 | 健康检查请求超时（秒） |
| `HealthCheck.UnhealthyCooldownSeconds` | int | 30 | 标记不健康后的冷却时间（秒），过期后允许重试 |
| `Logging.LogLevel.Default` | string | `Information` | 日志级别 |

### config.json

| 参数 | 类型 | 必填 | 说明 |
|------|------|------|------|
| `models.<key>` | string | 是 | 模型名（同时也是上游实际模型名） |
| `models.<key>.baseUrl` | string | 是 | 上游 API 地址（如 `https://api.deepseek.com`，含/不含 `/v1` 均可） |
| `models.<key>.apiKey` | string | 是 | 上游 API Key |
| `models.<key>.defaultParams` | object | 否 | 默认参数（支持 number/string/bool/object），客户端未传时注入 |
| `models.<key>.fallback` | string[] | 否 | 备用模型 key 列表，按顺序尝试 |

### 常见配置示例

**DeepSeek**：
```json
"deepseek-chat": {
  "baseUrl": "https://api.deepseek.com",
  "apiKey": "sk-xxx"
}
```

**OpenAI**：
```json
"gpt-4o": {
  "baseUrl": "https://api.openai.com",
  "apiKey": "sk-xxx",
  "defaultParams": { "temperature": 0.7 }
}
```

**本地 vLLM**：
```json
"qwen3": {
  "baseUrl": "http://192.168.1.100:8000/v1",
  "apiKey": "not-needed",
  "fallback": ["deepseek-chat"]
}
```

## 部署

### Systemd（推荐）

```bash
sudo cp publish/llmrouter /opt/llmrouter/
sudo cp appsettings.json config.json /opt/llmrouter/
sudo chmod +x /opt/llmrouter/llmrouter
```

创建 `/etc/systemd/system/llmrouter.service`：

```ini
[Unit]
Description=LLM Router
After=network.target

[Service]
Type=simple
WorkingDirectory=/opt/llmrouter
ExecStart=/opt/llmrouter/llmrouter
Restart=always
RestartSec=5
User=nobody
Group=nogroup
Environment=DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now llmrouter
sudo journalctl -u llmrouter -f   # 查看日志
```

## License

MIT
