using System.Text.Json;
using System.Text.Json.Serialization;

namespace llmrouter.Models;

[JsonSerializable(typeof(RouterConfig))]
[JsonSerializable(typeof(ModelEntry))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(List<string>))]
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true
)]
public partial class ConfigModelsJsonContext : JsonSerializerContext { }

public class RouterConfig
{
    public Dictionary<string, ModelEntry>? Models { get; set; }
}

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