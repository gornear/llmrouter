using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace llmrouter.Services;

public class ProxyService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ProxyService> _logger;
    private readonly HealthCheckService _healthCheck;
    private readonly int _timeoutSeconds;
    private readonly bool _debugPrompt;

    public ProxyService(
        IHttpClientFactory httpClientFactory,
        ILogger<ProxyService> logger,
        HealthCheckService healthCheck,
        int timeoutSeconds = 300,
        bool debugPrompt = false)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _healthCheck = healthCheck;
        _timeoutSeconds = timeoutSeconds;
        _debugPrompt = debugPrompt;
    }

    /// <summary>
    /// Forward a request to the upstream provider and return the response.
    /// Returns null if the upstream is unreachable.
    /// </summary>
    public async Task<HttpResponseMessage?> ForwardAsync(
        string baseUrl,
        string apiKey,
        string path,
        string targetModel,
        HttpRequest incomingRequest,
        Dictionary<string, JsonElement>? defaultParams = null,
        CancellationToken ct = default)
    {
        // Build upstream URL: baseUrl may or may not include a path prefix (e.g. /v1).
        // path is the full OpenAI API path (e.g. "/v1/chat/completions").
        // We normalize so the prefix is never duplicated.
        var url = BuildUpstreamUrl(baseUrl, path);
        var client = _httpClientFactory.CreateClient("Proxy");
        client.Timeout = TimeSpan.FromSeconds(_timeoutSeconds);

        using var upstreamRequest = new HttpRequestMessage
        {
            RequestUri = new Uri(url),
            Method = new HttpMethod(incomingRequest.Method)
        };

        // Copy the request body
        if (incomingRequest.Body != null && incomingRequest.ContentLength != 0)
        {
            // Rewind body stream in case of retry
            if (incomingRequest.Body.CanSeek)
                incomingRequest.Body.Position = 0;

            var ms = new MemoryStream();
            await incomingRequest.Body.CopyToAsync(ms, ct);
            ms.Position = 0;

            // Rewrite model field to upstream model name if needed
            var contentType = incomingRequest.ContentType ?? string.Empty;
            if (!string.IsNullOrEmpty(targetModel) &&
                contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            {
                var bodyBytes = ms.ToArray();
                var rewrittenBody = ReplaceModelField(bodyBytes, targetModel);
                ms = new MemoryStream(rewrittenBody);
            }

            // Merge defaultParams into JSON body if applicable
            if (defaultParams != null && defaultParams.Count > 0 &&
                contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            {
                var bodyBytes = ms.ToArray();
                var mergedBody = MergeDefaultParams(bodyBytes, defaultParams);
                ms = new MemoryStream(mergedBody);
            }

            // Debug: log messages summary (role + truncated content) when DebugPrompt is enabled
            if (_debugPrompt && _logger.IsEnabled(LogLevel.Debug))
            {
                var debugBytes = ms.ToArray();
                LogMessagesSummary(debugBytes, url);
                ms = new MemoryStream(debugBytes);
            }

            upstreamRequest.Content = new StreamContent(ms);

            // Copy content-type from original request
            if (!string.IsNullOrEmpty(incomingRequest.ContentType))
            {
                upstreamRequest.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(incomingRequest.ContentType);
            }
        }

        // Copy relevant headers (skip Host, Authorization, and connection-specific headers)
        foreach (var header in incomingRequest.Headers)
        {
            var key = header.Key.ToLowerInvariant();
            if (key is "host" or "authorization" or "connection" or "transfer-encoding")
                continue;

            try
            {
                upstreamRequest.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
            catch
            {
                // Some headers can't be added to HttpRequestMessage headers - ignore
            }
        }

        // Set the provider's API key
        upstreamRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        try
        {
            var response = await client.SendAsync(upstreamRequest, HttpCompletionOption.ResponseHeadersRead, ct);
            _healthCheck.MarkHealthy(baseUrl);

            // Treat 4xx/5xx as failure so ProxyEndpoint can try fallbacks
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Upstream {Url} returned {StatusCode}, trying next fallback",
                    url, (int)response.StatusCode);
                response.Dispose();
                return null;
            }

            return response;
        }
        catch (Exception)
        {
            _logger.LogWarning(
                "Upstream {Url} unreachable, trying next fallback",
                url);
            _healthCheck.MarkUnhealthy(baseUrl);
            return null;
        }
    }

    /// <summary>
    /// Merge defaultParams into a JSON request body.
    /// For each key in defaultParams, if the client did not provide a value,
    /// inject the default. Existing client values are never overwritten.
    /// </summary>
    private static byte[] MergeDefaultParams(byte[] bodyBytes, Dictionary<string, JsonElement> defaultParams)
    {
        try
        {
            var bodyJson = Encoding.UTF8.GetString(bodyBytes);
            using var doc = JsonDocument.Parse(bodyJson);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                return bodyBytes;

            var sb = new StringBuilder();
            sb.Append('{');

            bool first = true;
            foreach (var prop in root.EnumerateObject())
            {
                if (!first) sb.Append(',');
                first = false;

                sb.Append(EncodeJsonString(prop.Name));
                sb.Append(':');
                sb.Append(prop.Value.GetRawText());
            }

            // Append defaultParams not already present in the request
            foreach (var kvp in defaultParams)
            {
                if (root.TryGetProperty(kvp.Key, out _))
                    continue;

                if (!first) sb.Append(',');
                first = false;

                sb.Append(EncodeJsonString(kvp.Key));
                sb.Append(':');
                // JsonElement.GetRawText() preserves the original JSON type
                sb.Append(kvp.Value.GetRawText());
            }

            sb.Append('}');
            return Encoding.UTF8.GetBytes(sb.ToString());
        }
        catch
        {
            return bodyBytes;
        }
    }

    /// <summary>
    /// Extract and log messages[].role + messages[].content (first 120 chars) at Debug level.
    /// </summary>
    private void LogMessagesSummary(byte[] bodyBytes, string url)
    {
        try
        {
            using var doc = JsonDocument.Parse(bodyBytes);
            var root = doc.RootElement;

            if (!root.TryGetProperty("messages", out var messages) ||
                messages.ValueKind != JsonValueKind.Array)
                return;

            foreach (var msg in messages.EnumerateArray())
            {
                var role = msg.TryGetProperty("role", out var r) ? r.GetString() ?? "?" : "?";
                var content = msg.TryGetProperty("content", out var c)
                    ? TruncateContent(c) : "";
                _logger.LogDebug("  [{Role}] {Content}", role, content);
            }
        }
        catch
        {
            // best-effort, never break forwarding
        }
    }

    private static string TruncateContent(JsonElement content)
    {
        var text = content.ValueKind switch
        {
            JsonValueKind.String => content.GetString() ?? "",
            JsonValueKind.Array => "[multi-part content]",
            _ => content.GetRawText()
        };
        if (text.Length > 120)
            text = text[..120] + "...";
        return text;
    }

    /// <summary>
    /// Replace the "model" field value in a JSON request body.
    /// AOT-safe: scans for the "model" key and replaces its string value in-place.
    /// </summary>
    private static byte[] ReplaceModelField(byte[] bodyBytes, string newModel)
    {
        try
        {
            var bodyJson = Encoding.UTF8.GetString(bodyBytes);

            // Find "model" key
            var search = "\"model\"";
            var idx = bodyJson.IndexOf(search, StringComparison.Ordinal);
            if (idx < 0) return bodyBytes;

            idx += search.Length;
            // Skip whitespace and colon
            while (idx < bodyJson.Length && (bodyJson[idx] == ' ' || bodyJson[idx] == ':' || bodyJson[idx] == '\r' || bodyJson[idx] == '\n' || bodyJson[idx] == '\t'))
                idx++;

            if (idx >= bodyJson.Length || bodyJson[idx] != '"') return bodyBytes;

            var valStart = idx + 1; // after opening quote
            var valEnd = valStart;
            while (valEnd < bodyJson.Length)
            {
                if (bodyJson[valEnd] == '\\') { valEnd += 2; continue; }
                if (bodyJson[valEnd] == '"') break;
                valEnd++;
            }

            // Build result: prefix + new value + suffix
            var before = bodyJson[..valStart];
            var after = bodyJson[valEnd..];
            var result = before + EncodeJsonStringRaw(newModel) + after;
            return Encoding.UTF8.GetBytes(result);
        }
        catch
        {
            return bodyBytes;
        }
    }

    /// <summary>JSON string encoding without leading/trailing quotes (for inline use).</summary>
    private static string EncodeJsonStringRaw(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>AOT-safe JSON string encoding: wraps in quotes and escapes \ and ".</summary>
    private static string EncodeJsonString(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(ch); break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }


    /// <summary>
    /// Stream the upstream response body directly to the client.
    /// Used for SSE (Server-Sent Events) streaming.
    /// </summary>
    public async Task StreamResponseAsync(
        HttpResponseMessage upstreamResponse,
        HttpResponse clientResponse,
        CancellationToken ct = default)
    {
        // Copy status code
        clientResponse.StatusCode = (int)upstreamResponse.StatusCode;

        // Copy response headers
        foreach (var header in upstreamResponse.Headers)
        {
            if (header.Key.ToLowerInvariant() is "transfer-encoding" or "connection")
                continue;

            try
            {
                clientResponse.Headers[header.Key] = header.Value.ToArray();
            }
            catch
            {
                // Skip headers that can't be set
            }
        }

        if (upstreamResponse.Content?.Headers != null)
        {
            foreach (var header in upstreamResponse.Content.Headers)
            {
                try
                {
                    clientResponse.Headers[header.Key] = header.Value.ToArray();
                }
                catch
                {
                    // Skip headers that can't be set
                }
            }
        }

        var contentStream = upstreamResponse.Content != null
            ? await upstreamResponse.Content.ReadAsStreamAsync(ct)
            : Stream.Null;
        using var upstreamStream = contentStream;
        var outputStream = clientResponse.Body;

        var buffer = new byte[8192];
        int bytesRead;
        while ((bytesRead = await upstreamStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
            await outputStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            await outputStream.FlushAsync(ct);
        }
    }

    /// <summary>
    /// Build the full upstream URL, normalizing away any path prefix overlap.
    /// baseUrl may or may not include an API path prefix (e.g. "http://host:port/v1");
    /// path is always the full OpenAI path (e.g. "/v1/chat/completions").
    /// 
    /// Examples:
    ///   baseUrl="http://172.1.1.2:14850/v1" + path="/v1/chat/completions"
    ///     → http://172.1.1.2:14850/v1/chat/completions  (prefix overlap removed)
    ///   baseUrl="http://172.1.1.2:14850" + path="/v1/chat/completions"
    ///     → http://172.1.1.2:14850/v1/chat/completions
    ///   baseUrl="https://api.deepseek.com" + path="/v1/models"
    ///     → https://api.deepseek.com/v1/models
    /// </summary>
    internal static string BuildUpstreamUrl(string baseUrl, string path)
    {
        var baseUri = new Uri(baseUrl.TrimEnd('/'));
        var authority = baseUri.GetLeftPart(UriPartial.Authority); // scheme://host:port
        var basePath = baseUri.AbsolutePath.TrimEnd('/');          // e.g. "/v1" or ""

        var relativePath = path.TrimStart('/');

        // If baseUrl already has a path prefix that overlaps with the start of path, strip it
        if (basePath.Length > 0)
        {
            var prefix = basePath.TrimStart('/') + "/";          // e.g. "v1/"
            if (relativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                relativePath = relativePath[prefix.Length..];
        }

        return basePath.Length > 0
            ? $"{authority}{basePath}/{relativePath.TrimStart('/')}"
            : $"{authority}/{relativePath.TrimStart('/')}";
    }
}