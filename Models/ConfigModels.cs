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
    /// <summary>Flattened runtime model lookup (Provider/modelid → ModelEntry). Built by Flatten().</summary>
    public Dictionary<string, ModelEntry>? Models { get; set; }

    /// <summary>
    /// Flatten a providers dictionary into the Models dictionary.
    /// Each model gets keyed as "Provider/modelid".
    /// baseUrl and apiKey are inherited from EndpointGroup; fallback values are
    /// already in "Provider/modelid" format.
    /// </summary>
    public static RouterConfig Flatten(Dictionary<string, List<EndpointGroup>> providers)
    {
        var config = new RouterConfig
        {
            Models = new Dictionary<string, ModelEntry>(StringComparer.OrdinalIgnoreCase)
        };

        foreach (var (provider, groups) in providers)
        {
            foreach (var group in groups)
            {
                if (group.Models == null) continue;

                foreach (var pm in group.Models)
                {
                    var key = $"{provider}/{pm.ModelId}";
                    config.Models[key] = new ModelEntry
                    {
                        BaseUrl = group.BaseUrl,
                        ApiKey = group.ApiKey,
                        DefaultParams = pm.DefaultParams,
                        Fallback = pm.Fallback
                    };
                }
            }
        }

        return config;
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
/// Flattened runtime model entry (unchanged from previous version).
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
