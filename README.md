# LLM Router

[中文说明](README-ZH_CN.md)

A lightweight LLM model routing gateway — compiled via .NET 10 AOT into a single binary (~12 MB).

## Features

- **Unified API Entry** — clients use a single API key; LLM Router dispatches to the correct upstream based on the model name in each request
- **Fallback Chain** — automatically degrades to backup models when the primary is unreachable or returns 4xx/5xx
- **Model Name Rewriting** — the client's requested model name is transparently replaced with the upstream model name (the `config.json` key *is* the upstream model name)
- **Transparent Proxy** — forwards all OpenAI-compatible endpoints (`/v1/chat/completions`, `/v1/embeddings`, `/v1/images/*`, `/v1/audio/*`, etc.)
- **SSE Streaming** — full support for `stream: true` Server-Sent Events
- **Default Parameter Injection** — configure default body parameters per model (supports number/string/bool/object). Client-supplied values are never overwritten
- **Health Checking** — background polling of upstream `/v1/models` + immediate marking on request failure; 30-second cooldown before automatic retry
- **Hot Reload** — `config.json` changes are picked up automatically without restart
- **AOT Compiled** — single native binary, no .NET runtime required
- **Systemd Ready** — logs to stdout, integrates with `journalctl`

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (build only, not required at runtime)
- Linux x86-64 (glibc 2.23+ or musl)

## Quick Start

### 1. Clone

```bash
git clone https://github.com/gornear/llmrouter.git
cd llmrouter
```

### 2. Configure

Copy and edit the configuration:

```bash
cp config.json.example config.json
```

Edit `config.json` to add your upstream models:

```json
{
  "local": [
    {
      "baseUrl": "http://192.168.1.100:8000/v1",
      "apiKey": "your-api-key-or-not-needed",
      "models": [
        {
          "modelid": "gemma4",
          "fallback": ["deepseek/deepseek-v4-flash"],
          "defaultParams": {
            "temperature": 0.0,
            "top_p": 0.9
          }
        }
      ]
    }
  ],
  "deepseek": [
    {
      "baseUrl": "https://api.deepseek.com",
      "apiKey": "sk-your-deepseek-key",
      "models": [
        {
          "modelid": "deepseek-v4-flash",
          "defaultParams": {
            "temperature": 0.0,
            "top_p": 0.9,
            "thinking": {
              "type": "disabled"
            }
          }
        }
      ]
    }
  ]
}
```

Edit `appsettings.json` to set the listen address and unified API key:

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

> **Note**: `baseUrl` may include a path prefix (e.g. `http://host:8000/v1`) or not. The program automatically deduplicates overlapping path prefixes — no `/v1/v1` double-writes.
>
> **Model Alias**: Use `alias` to expose the same upstream model under a different public name with different `defaultParams`. For example, the same vLLM model can be listed as both `local/gemma4-it-31b` and `local/gemma4-it-31b-thinking` — the upstream receives `"model":"gemma4-it-31b"` in both cases, but the thinking variant injects `"enable_thinking":true`.

### 3. Build

```bash
dotnet publish -c Release -o publish
```

Output: `publish/llmrouter` ~12 MB single binary.

For development, use `dotnet run`.

### 4. Run

```bash
./publish/llmrouter
```

Output:
```
info: LLM Router starting on http://0.0.0.0:5000
info: Health check service started (interval: 30s, timeout: 5s)
```

### 5. Test

```bash
# Health check (no auth)
curl http://localhost:5000/health

# List models
curl -H "Authorization: Bearer sk-your-unified-api-key" http://localhost:5000/v1/models

# Chat Completions
curl -H "Authorization: Bearer sk-your-unified-api-key" \
     -H "Content-Type: application/json" \
     -d '{"model":"local/gemma4","messages":[{"role":"user","content":"Hello!"}]}' \
     http://localhost:5000/v1/chat/completions

# Streaming
curl -N -H "Authorization: Bearer sk-your-unified-api-key" \
     -H "Content-Type: application/json" \
     -d '{"model":"deepseek/deepseek-v4-flash","messages":[{"role":"user","content":"Hello!"}],"stream":true}' \
     http://localhost:5000/v1/chat/completions
```

## How It Works

```
Client  ──POST /v1/chat/completions──→  LLM Router  ──POST /v1/chat/completions──→  Upstream A
  "model":"local/gemma4"                 │  rewrites "model":"gemma4"                    (vllm)
                                         │  injects default params
                                         │
                                      Upstream A unreachable?
                                         │
                                         └──POST /v1/chat/completions──→  Upstream B
                                             "model":"deepseek-v4-flash"          (deepseek)
```


## API Reference

### Authentication

All endpoints except `/health` require the unified API key:

```
Authorization: Bearer sk-your-unified-api-key
```

### Endpoints

| Endpoint | Method | Auth | Description |
|---|---|---|---|
| `/health` | GET | No | Service liveness |
| `/v1/models` | GET | Bearer | List configured models (from config.json) |
| `/v1/chat/completions` | POST | Bearer | Chat Completions (stream support) |
| `/v1/{**path}` | ANY | Bearer | Transparent proxy (embeddings, images, audio, etc.) |

### Error Response

```json
{
  "error": {
    "message": "No healthy routes found for model 'unknown-model'",
    "type": "no_route"
  }
}
```

## Configuration Reference

### appsettings.json

| Parameter | Type | Default | Description |
|---|---|---|---|
| `Kestrel.Endpoints.Http.Url` | string | `http://0.0.0.0:5000` | Listen address |
| `Kestrel.Limits.MaxConcurrentConnections` | int | 200 | Max concurrent connections |
| `ApiKey` | string | — | Unified API key (used by clients) |
| `RequestTimeoutSeconds` | int | 300 | Upstream request timeout (seconds) |
| `MaxRetryAttempts` | int | 3 | Max fallback attempts |
| `DebugPrompt` | bool | false | When true, logs message content at Debug level (privacy-sensitive) |
| `HealthCheck.Enabled` | bool | true | Enable background health checks |
| `HealthCheck.IntervalSeconds` | int | 30 | Polling interval (seconds) |
| `HealthCheck.TimeoutSeconds` | int | 5 | Health check request timeout (seconds) |
| `HealthCheck.UnhealthyCooldownSeconds` | int | 30 | Cooldown after marking unhealthy (seconds) |
| `Logging.LogLevel.Default` | string | `Information` | Log level |

### config.json

| Parameter | Type | Required | Description |
|---|---|---|---|
| `<provider>` | string | Yes | Provider namespace (e.g. `local`, `deepseek`, `openai`) |
| `<provider>[].baseUrl` | string | Yes | Upstream API URL (e.g. `https://api.deepseek.com`, with or without `/v1` suffix) |
| `<provider>[].apiKey` | string | Yes | Upstream API key (shared by all models under this endpoint group) |
| `<provider>[].models` | array | Yes | Model entries under this endpoint group |
| `models[].modelid` | string | Yes | Upstream model name sent to the provider's API |
| `models[].alias` | string | No | Public-facing name override (exposed as `provider/alias` in `/v1/models`). When set, clients use the alias, but the upstream receives `modelid`. Useful for exposing thinking/non-thinking variants of the same model. |
| `models[].defaultParams` | object | No | Default body parameters (number/string/bool/object). Injected when absent from client request |
| `models[].fallback` | string[] | No | Ordered list of fallback models in `provider/modelid` format |

### Example Configurations

**DeepSeek**:
```json
"deepseek": [
  {
    "baseUrl": "https://api.deepseek.com",
    "apiKey": "sk-xxx",
    "models": [
      { "modelid": "deepseek-chat" }
    ]
  }
]
```

**OpenAI**:
```json
"openai": [
  {
    "baseUrl": "https://api.openai.com",
    "apiKey": "sk-xxx",
    "models": [
      {
        "modelid": "gpt-4o",
        "defaultParams": { "temperature": 0.7 }
      }
    ]
  }
]
```

**Local vLLM**:
```json
"local": [
  {
    "baseUrl": "http://192.168.1.100:8000/v1",
    "apiKey": "not-needed",
    "models": [
      {
        "modelid": "qwen3",
        "fallback": ["deepseek/deepseek-chat"]
      }
    ]
  }
]
```

**Model Alias (thinking/non-thinking variants)**:
```json
"local": [
  {
    "baseUrl": "http://192.168.1.100:8000/v1",
    "apiKey": "not-needed",
    "models": [
      {
        "modelid": "qwen3-235b",
        "defaultParams": { "temperature": 0.0 }
      },
      {
        "modelid": "qwen3-235b",
        "alias": "qwen3-235b-thinking",
        "defaultParams": { "temperature": 0.0, "enable_thinking": true }
      }
    ]
  }
]
```
> **How alias works**: The alias becomes the public name (`local/qwen3-235b-thinking`), but the upstream API still receives `"model":"qwen3-235b"`. The difference is `enable_thinking: true` is injected into the request body.

## Deployment

### Systemd (recommended)

```bash
sudo cp publish/llmrouter /opt/llmrouter/
sudo cp appsettings.json config.json /opt/llmrouter/
sudo chmod +x /opt/llmrouter/llmrouter
```

Create `/etc/systemd/system/llmrouter.service`:

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
sudo journalctl -u llmrouter -f   # view logs
```

### Docker

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish -c Release -o /app

FROM ubuntu:22.04
WORKDIR /app
COPY --from=build /app/llmrouter .
EXPOSE 5000
ENTRYPOINT ["./llmrouter"]
```

```bash
docker build -t llmrouter .
docker run -d -p 5000:5000 \
  -v $(pwd)/config.json:/app/config.json \
  -v $(pwd)/appsettings.json:/app/appsettings.json \
  llmrouter
```

## License

MIT
