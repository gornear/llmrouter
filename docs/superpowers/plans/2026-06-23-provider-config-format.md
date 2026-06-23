# Provider-Grouped Config Format Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restructure config.json from flat `{ "modelname": {...} }` to provider-grouped format with shared baseUrl/apiKey per endpoint group, and `Provider/modelid` namespaced model references.

**Architecture:** Parse the new 3-level JSON (Provider → EndpointGroup[] → Model[]) into new C# types, then flatten into the existing `Dictionary<string, ModelEntry>` at load boundary so downstream code (RouterService, ProxyService, HealthCheckService, ProxyEndpoint) requires zero changes.

**Tech Stack:** C# 13, .NET 10 AOT, System.Text.Json Source Generator

## Global Constraints

- `<PublishAot>true</PublishAot>` — all JSON types must be source-generator compatible (closed generics only)
- No reflection-based deserialization
- Property naming: camelCase in JSON, PascalCase in C#
- No backward compatibility with old flat format required

---

### Task 1: Restructure ConfigModels.cs with Provider-aware types

**Files:**
- Modify: `Models/ConfigModels.cs`

**Interfaces:**
- Consumes: nothing new
- Produces: `RouterConfig.Providers` (Dictionary<string,List<EndpointGroup>>), `EndpointGroup`, `ProviderModel`, `Flatten()` → populates `RouterConfig.Models` (Dictionary<string,ModelEntry>)

- [ ] **Step 1: Replace ConfigModels.cs**

Replace the entire file with the new type definitions:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace llmrouter.Models;

// --- Source Generator registrations ---
[JsonSerializable(typeof(RouterConfig))]
[JsonSerializable(typeof(EndpointGroup))]
[JsonSerializable(typeof(ProviderModel))]
[JsonSerializable(typeof(Dictionary<string, List<EndpointGroup>>))]
[JsonSerializable(typeof(List<EndpointGroup>))]
[JsonSerializable(typeof(List<ProviderModel>))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(List<string>))]
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true
)]
public partial class ConfigModelsJsonContext : JsonSerializerContext { }

// --- Config types ---

/// <summary>
/// Root config object. JSON shape:
/// {
///   "local": [ { "baseUrl": "...", "apiKey": "...", "models": [...] } ],
///   "deepseek": [ { ... } ]
/// }
/// After deserialization, Flatten() builds the runtime Models dictionary.
/// </summary>
public class RouterConfig
{
    /// <summary>Raw deserialized providers. Key = provider name, Value = endpoint groups.</summary>
    public Dictionary<string, List<EndpointGroup>>? Providers { get; set; }

    /// <summary>Flattened runtime model lookup (Provider/modelid → ModelEntry). Built by Flatten().</summary>
    public Dictionary<string, ModelEntry>? Models { get; set; }

    /// <summary>
    /// Flatten Providers into the Models dictionary.
    /// Each model gets keyed as "Provider/modelid".
    /// baseUrl and apiKey are inherited from EndpointGroup; fallback values are
    /// already in "Provider/modelid" format.
    /// </summary>
    public void Flatten()
    {
        Models = new Dictionary<string, ModelEntry>(StringComparer.OrdinalIgnoreCase);
        if (Providers == null) return;

        foreach (var (provider, groups) in Providers)
        {
            foreach (var group in groups)
            {
                if (group.Models == null) continue;

                foreach (var pm in group.Models)
                {
                    var key = $"{provider}/{pm.ModelId}";
                    Models[key] = new ModelEntry
                    {
                        BaseUrl = group.BaseUrl,
                        ApiKey = group.ApiKey,
                        DefaultParams = pm.DefaultParams,
                        Fallback = pm.Fallback
                    };
                }
            }
        }
    }
}

/// <summary>
/// One endpoint configuration group under a provider.
/// A provider may have multiple groups (e.g. different regions / deployments).
/// </summary>
public class EndpointGroup
{
    public string? BaseUrl { get; set; }
    public string? ApiKey { get; set; }
    public List<ProviderModel>? Models { get; set; }
}

/// <summary>
/// A single model entry within an endpoint group.
/// Inherits baseUrl/apiKey from its parent EndpointGroup.
/// </summary>
public class ProviderModel
{
    public string ModelId { get; set; } = string.Empty;
    public Dictionary<string, JsonElement>? DefaultParams { get; set; }
    public List<string>? Fallback { get; set; }
}

/// <summary>
/// Flattened runtime model entry (same as before — no change).
/// </summary>
public class ModelEntry
{
    public string? BaseUrl { get; set; }
    public string? ApiKey { get; set; }
    /// <summary>
    /// Default parameters to inject into requests if not provided by the client.
    /// Supports any JSON value type (number, string, bool, object, array) via JsonElement.
    /// </summary>
    public Dictionary<string, JsonElement>? DefaultParams { get; set; }
    public List<string>? Fallback { get; set; }
}
```

- [ ] **Step 2: Verify compilation**

```bash
dotnet build
```

Expected: Build succeeds with no errors.

- [ ] **Step 3: Commit**

```bash
git add Models/ConfigModels.cs
git commit -m "refactor: restructure config models for provider-grouped format"
```
```

---

### Task 2: Update ConfigReloadService to call Flatten()

**Files:**
- Modify: `Services/ConfigReloadService.cs`

**Interfaces:**
- Consumes: `RouterConfig.Flatten()`
- Produces: `RouterConfig.Models` populated at load time

- [ ] **Step 1: Add Flatten() call in LoadConfig()**

In `ConfigReloadService.cs`, find the `LoadConfig()` method (~line 50). After the deserialization block where `_config = config;` is set, add the `config.Flatten();` call. The relevant section should look like:

```csharp
var json = File.ReadAllText(_configPath);
var config = JsonSerializer.Deserialize(json, ConfigModelsJsonContext.Default.RouterConfig);

if (config != null)
{
    config.Flatten();  // <-- ADD THIS LINE
    lock (_lock)
    {
        _config = config;
    }
    _logger.LogInformation("Config loaded successfully with {Count} models from {Path}",
        config.Models?.Count ?? 0, _configPath);
}
else
{
    _logger.LogWarning("Config deserialized as null from {Path}", _configPath);
    lock (_lock)
    {
        _config = new RouterConfig { Models = new Dictionary<string, ModelEntry>() };
    }
}
```

- [ ] **Step 2: Verify compilation**

```bash
dotnet build
```

Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add Services/ConfigReloadService.cs
git commit -m "feat: call Flatten() after config deserialization"
```
```

---

### Task 3: Update config.json.example with new format

**Files:**
- Modify: `config.json.example`

**Interfaces:**
- Consumes: new `RouterConfig` types
- Produces: valid example for users

- [ ] **Step 1: Rewrite config.json.example**

Replace entire content:

```json
{
  "local": [
    {
      "baseUrl": "http://192.168.1.100:8000/v1",
      "apiKey": "your-local-api-key",
      "models": [
        {
          "modelid": "gemma4-it-31b",
          "fallback": [
            "deepseek/deepseek-v4-flash",
            "deepseek/deepseek-v4-pro-thinking"
          ],
          "defaultParams": {
            "temperature": 0.0,
            "top_p": 0.9,
            "top_k": 40
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
          "modelid": "deepseek-v4-pro",
          "defaultParams": {
            "temperature": 0.0,
            "top_p": 0.9,
            "top_k": 40,
            "thinking": {
              "type": "disabled"
            }
          }
        },
        {
          "modelid": "deepseek-v4-flash",
          "defaultParams": {
            "temperature": 0.0,
            "top_p": 0.9,
            "top_k": 40,
            "thinking": {
              "type": "disabled"
            }
          }
        },
        {
          "modelid": "deepseek-v4-pro-thinking",
          "defaultParams": {
            "temperature": 0.0,
            "top_p": 0.9,
            "top_k": 40
          }
        },
        {
          "modelid": "deepseek-v4-flash-thinking",
          "defaultParams": {
            "temperature": 0.0,
            "top_p": 0.9,
            "top_k": 40
          }
        }
      ]
    }
  ]
}
```

- [ ] **Step 2: Commit**

```bash
git add config.json.example
git commit -m "docs: update config.json.example to provider-grouped format"
```
```

---

### Task 4: Update running config.json to new format

**Files:**
- Modify: `config.json`

**Interfaces:**
- Consumes: new `RouterConfig` types
- Produces: working runtime config

- [ ] **Step 1: Rewrite config.json with actual API keys preserved**

Replace with (API keys from existing config preserved):

```json
{
  "local": [
    {
      "baseUrl": "http://172.1.1.2:14850/v1",
      "apiKey": "=whatthefuck=",
      "models": [
        {
          "modelid": "current",
          "fallback": [
            "deepseek/deepseek-v4-flash",
            "deepseek/deepseek-v4-pro"
          ],
          "defaultParams": {
            "temperature": 0.0,
            "top_p": 0.9,
            "top_k": 40
          }
        }
      ]
    }
  ],
  "deepseek": [
    {
      "baseUrl": "https://api.deepseek.com",
      "apiKey": "sk-1bc26cf30fac4ed4a368906304906b90",
      "models": [
        {
          "modelid": "deepseek-v4-pro",
          "defaultParams": {
            "temperature": 0.0,
            "top_p": 0.9,
            "top_k": 40,
            "thinking": {
              "type": "disabled"
            }
          }
        },
        {
          "modelid": "deepseek-v4-flash",
          "defaultParams": {
            "temperature": 0.0,
            "top_p": 0.9,
            "top_k": 40,
            "thinking": {
              "type": "disabled"
            }
          }
        },
        {
          "modelid": "deepseek-v4-pro-thinking",
          "defaultParams": {
            "temperature": 0.0,
            "top_p": 0.9,
            "top_k": 40
          }
        },
        {
          "modelid": "deepseek-v4-flash-thinking",
          "defaultParams": {
            "temperature": 0.0,
            "top_p": 0.9,
            "top_k": 40
          }
        }
      ]
    }
  ]
}
```

- [ ] **Step 2: Commit**

```bash
git add config.json
git commit -m "chore: migrate config.json to provider-grouped format"
```

> **Note:** `config.json` contains API keys and is in `.gitignore` — verify before attempting to commit. If gitignored, just ensure the file is updated on disk.
```
```

---

### Task 5: Update README files with new config examples

**Files:**
- Modify: `README.md`
- Modify: `README-ZH_CN.md`

**Interfaces:**
- Consumes: new config format
- Produces: updated documentation

- [ ] **Step 1: Update README.md config example section**

In `README.md`, replace the config.json example (around lines 33-54) with:

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

Also update the model name in curl examples from `"model":"gemma4"` to `"model":"local/gemma4"` and `"model":"deepseek-v4-flash"` to `"model":"deepseek/deepseek-v4-flash"`.

Update the "How It Works" diagram model names similarly.

Also update the "Example Configurations" section at the bottom of the README to use the new format:

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

And:

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

And the config reference table — update to describe the new structure:

| Parameter | Type | Required | Description |
|---|---|---|---|
| `<provider>` | string | Yes | Provider namespace (e.g. `local`, `deepseek`, `openai`) |
| `<provider>[].baseUrl` | string | Yes | Upstream API URL |
| `<provider>[].apiKey` | string | Yes | Upstream API key |
| `<provider>[].models` | array | Yes | Model entries under this endpoint group |
| `models[].modelid` | string | Yes | Model identifier (referenced as `provider/modelid`) |
| `models[].defaultParams` | object | No | Default body parameters injected when absent from client request |
| `models[].fallback` | string[] | No | Ordered list of fallback models in `provider/modelid` format |

- [ ] **Step 2: Update README-ZH_CN.md**

Apply equivalent changes to the Chinese README.

- [ ] **Step 3: Commit**

```bash
git add README.md README-ZH_CN.md
git commit -m "docs: update READMEs for provider-grouped config format"
```
```

---

### Task 6: Build and verify AOT compatibility

**Files:**
- No file changes (verification only)

- [ ] **Step 1: Debug build (quick check)**

```bash
dotnet build
```

Expected: Build succeeds.

- [ ] **Step 2: AOT publish (full native compilation)**

```bash
dotnet publish -c Release -o publish
```

Expected: AOT compilation succeeds, produces `publish/llmrouter` binary.

- [ ] **Step 3: Quick smoke test with new config**

Start the router briefly:

```bash
./publish/llmrouter &
sleep 2

# Test health
curl -s http://localhost:5000/health

# Test models list
curl -s -H "Authorization: Bearer sk-llmrouter-local-key-change-me" http://localhost:5000/v1/models

kill %1
```

Expected: Health returns `{"status":"ok"}`, models list returns provider-prefixed model IDs like `local/current`, `deepseek/deepseek-v4-flash`, etc.

- [ ] **Step 4: Commit (if no fixes needed)**

No commit needed if everything passes.
```

