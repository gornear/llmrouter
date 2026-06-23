using System.Text;
using System.Text.Json;
using llmrouter.Models;
using llmrouter.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace llmrouter.Endpoints;

public static class ProxyEndpoint
{
    /// <summary>
    /// Catch-all proxy endpoint for /v1/{**path}
    /// Extracts model from the request body, resolves the route, and proxies to upstream.
    /// </summary>
    public static async Task HandleProxyAsync(
        HttpContext context,
        RouterService router,
        ProxyService proxy,
        ILogger logger)
    {
        // path = "chat/completions" (the **path captured from /v1/{**path})
        // We reconstruct the full OpenAI path: /v1/chat/completions
        var capturedPath = context.Request.RouteValues["path"]?.ToString() ?? string.Empty;
        var path = "/v1/" + capturedPath.TrimStart('/');

        // Extract model from request body
        string? model = null;
        if (context.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
            context.Request.ContentLength > 0)
        {
            // We need to read the body to extract the model, then rewind for forwarding
            context.Request.EnableBuffering();
            var bodyBytes = await context.Request.BodyReader.ReadAsync();

            if (bodyBytes.Buffer.Length > 0)
            {
                var bodyText = Encoding.UTF8.GetString(bodyBytes.Buffer);
                context.Request.Body.Position = 0; // Rewind for forwarding

                // Quick manual extraction of "model" field to avoid full deserialization
                model = ExtractJsonField(bodyText, "model");
            }
            else
            {
                context.Request.Body.Position = 0;
            }
        }
        else
        {
            // GET /models might not have a body, but we still need model
            // Try query string
            model = context.Request.Query["model"].FirstOrDefault();
        }

        if (string.IsNullOrEmpty(model))
        {
            context.Response.StatusCode = 400;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                "{\"error\":{\"message\":\"Model parameter is required\",\"type\":\"bad_request\"}}");
            return;
        }

        // Resolve route chain
        var routes = router.ResolveRoute(model);
        if (routes.Count == 0)
        {
            context.Response.StatusCode = 502;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                $"{{\"error\":{{\"message\":\"No healthy routes found for model '{model}'\",\"type\":\"no_route\"}}}}");
            return;
        }

        // Rewind body stream so each retry attempt reads from start
        if (context.Request.Body.CanSeek)
            context.Request.Body.Position = 0;

        // Try each route in the chain
        var ct = context.RequestAborted;
        for (int i = 0; i < routes.Count; i++)
        {
            var route = routes[i];
            logger.LogInformation(
                "Routing request: model={ClientModel} -> target={TargetModel} @ {BaseUrl}{Path}",
                model, route.UpstreamModelName, route.BaseUrl, path);

            var upstreamResponse = await proxy.ForwardAsync(
                route.BaseUrl,
                route.ApiKey,
                path,
                route.UpstreamModelName,
                context.Request,
                route.DefaultParams,
                ct);

            if (upstreamResponse != null)
            {
                // Success — stream the response back
                await proxy.StreamResponseAsync(upstreamResponse, context.Response, ct);
                logger.LogInformation(
                    "Completed: model={ClientModel} via {TargetModel} (status={Status})",
                    model, route.ModelName, (int)upstreamResponse.StatusCode);
                return;
            }

            // Failed — try next fallback
            logger.LogWarning(
                "Route {Index}/{Total} failed: {TargetModel}, trying next fallback...",
                i + 1, routes.Count, route.ModelName);
        }

        // All routes failed
        context.Response.StatusCode = 502;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(
            $"{{\"error\":{{\"message\":\"All routes exhausted for model '{model}'\",\"type\":\"all_routes_failed\"}}}}");
    }

    /// <summary>
    /// Health check endpoint
    /// </summary>
    public static async Task HandleHealthAsync(HttpContext context)
    {
        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync("{\"status\":\"ok\"}");
    }

    /// <summary>
    /// List models endpoint — returns all models from config.json
    /// </summary>
    public static async Task HandleModelsAsync(
        HttpContext context,
        ConfigReloadService configReload)
    {
        var config = configReload.Config;
        var modelList = new ListModelsResponse();

        if (config.Models != null)
        {
            foreach (var kvp in config.Models)
            {
                modelList.Data.Add(new ModelInfo
                {
                    Id = kvp.Key,
                    Object = "model",
                    Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    OwnedBy = "llmrouter"
                });
            }
        }

        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/json";
        var json = JsonSerializer.Serialize(modelList, ListModelsJsonContext.Default.ListModelsResponse);
        await context.Response.WriteAsync(json);
    }

    /// <summary>
    /// Extract a simple string field value from a JSON string.
    /// Handles: "model": "gpt-4o" and "model":"gpt-4o"
    /// </summary>
    private static string? ExtractJsonField(string json, string fieldName)
    {
        var search = $"\"{fieldName}\"";
        var idx = json.IndexOf(search, StringComparison.Ordinal);
        if (idx < 0) return null;

        idx += search.Length;
        // Skip whitespace and colon
        while (idx < json.Length && (json[idx] == ' ' || json[idx] == ':'))
        {
            idx++;
            // Also skip \r\n\t
            if (idx < json.Length && (json[idx] == '\r' || json[idx] == '\n' || json[idx] == '\t'))
                idx++;
        }

        if (idx >= json.Length) return null;

        // Expect opening quote
        if (json[idx] != '"') return null;
        idx++;

        // Read until closing unescaped quote
        var end = idx;
        while (end < json.Length)
        {
            if (json[end] == '\\')
            {
                end += 2; // skip escaped char
                continue;
            }
            if (json[end] == '"')
                break;
            end++;
        }

        if (end > idx)
            return json[idx..end];

        return null;
    }
}