using System.Text.Json;
using llmrouter.Models;
using Microsoft.Extensions.Logging;

namespace llmrouter.Services;

public class RouterService
{
    private readonly ILogger<RouterService> _logger;
    private readonly ConfigReloadService _configReload;
    private readonly HealthCheckService _healthCheck;
    private readonly int _maxRetryAttempts;

    public RouterService(
        ILogger<RouterService> logger,
        ConfigReloadService configReload,
        HealthCheckService healthCheck,
        int maxRetryAttempts = 3)
    {
        _logger = logger;
        _configReload = configReload;
        _healthCheck = healthCheck;
        _maxRetryAttempts = maxRetryAttempts;
    }

    /// <summary>
    /// Resolve the routing chain for a given model name.
    /// Returns a list of (baseUrl, apiKey) tuples in order: primary model first, then fallbacks.
    /// Unhealthy endpoints and models without baseUrl/apiKey are skipped.
    /// </summary>
    public List<RouteTarget> ResolveRoute(string modelName)
    {
        var config = _configReload.Config;
        var results = new List<RouteTarget>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (config.Models == null || config.Models.Count == 0)
        {
            _logger.LogWarning("No models configured, cannot route model: {Model}", modelName);
            return results;
        }

        // Try primary model + fallback chain
        CollectRouteChain(modelName, config.Models, results, visited);

        if (results.Count == 0)
        {
            _logger.LogWarning("No healthy routes found for model: {Model}", modelName);
        }

        return results;
    }

    private void CollectRouteChain(
        string modelName,
        Dictionary<string, ModelEntry> models,
        List<RouteTarget> results,
        HashSet<string> visited)
    {
        if (!visited.Add(modelName))
            return; // Prevent circular fallback references

        if (!models.TryGetValue(modelName, out var entry))
        {
            _logger.LogWarning("Model '{Model}' not found in config", modelName);
            return;
        }

        if (!string.IsNullOrEmpty(entry.BaseUrl) && !string.IsNullOrEmpty(entry.ApiKey))
        {
            if (_healthCheck.IsHealthy(entry.BaseUrl))
            {
                results.Add(new RouteTarget
                {
                    ModelName = modelName,
                    BaseUrl = entry.BaseUrl,
                    ApiKey = entry.ApiKey,
                    DefaultParams = entry.DefaultParams
                });
                _logger.LogDebug("Route added: {Model} @ {BaseUrl}", modelName, entry.BaseUrl);
            }
            else
            {
                _logger.LogWarning("Skipping unhealthy route: {Model} @ {BaseUrl}", modelName, entry.BaseUrl);
            }
        }

        // Process fallback chain
        if (entry.Fallback != null)
        {
            foreach (var fallbackModel in entry.Fallback)
            {
                CollectRouteChain(fallbackModel, models, results, visited);
            }
        }
    }

    /// <summary>
    /// Try routing a request through the resolved chain until one succeeds (or all fail).
    /// Returns the upstream HttpResponseMessage on success.
    /// </summary>
    public record RouteTarget
    {
        public string ModelName { get; init; } = string.Empty;
        public string BaseUrl { get; init; } = string.Empty;
        public string ApiKey { get; init; } = string.Empty;
        public Dictionary<string, JsonElement>? DefaultParams { get; init; }
    }
}