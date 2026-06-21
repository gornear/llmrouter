using System.Text.Json;
using llmrouter.Endpoints;
using llmrouter.Middleware;
using llmrouter.Models;
using llmrouter.Services;
using Microsoft.AspNetCore.Http;

namespace llmrouter;

public class Program
{
    public static async Task Main(string[] args)
    {
        // Load appsettings.json with AOT-compatible serializer

        var appSettings = LoadAppSettings(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));

        var builder = WebApplication.CreateSlimBuilder(args);

        // Configure Kestrel limits
        var limits = appSettings.Kestrel?.Limits;
        if (limits != null)
        {
            builder.WebHost.ConfigureKestrel(options =>
            {
                if (limits.MaxConcurrentConnections > 0)
                    options.Limits.MaxConcurrentConnections = limits.MaxConcurrentConnections;
                if (limits.MaxConcurrentUpgradedConnections > 0)
                    options.Limits.MaxConcurrentUpgradedConnections = limits.MaxConcurrentUpgradedConnections;
            });
        }

        // Register services
        var apiKey = appSettings.ApiKey ?? "sk-llmrouter-local-key-change-me";
        builder.Services.AddSingleton(appSettings);
        builder.Services.AddSingleton(appSettings.HealthCheck ?? new HealthCheckSettings());
        builder.Services.AddSingleton<ConfigReloadService>();
        builder.Services.AddSingleton<HealthCheckService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<HealthCheckService>());
        builder.Services.AddSingleton<RouterService>(sp =>
            new RouterService(
                sp.GetRequiredService<ILogger<RouterService>>(),
                sp.GetRequiredService<ConfigReloadService>(),
                sp.GetRequiredService<HealthCheckService>(),
                appSettings.MaxRetryAttempts));
        builder.Services.AddSingleton<ProxyService>(sp =>
            new ProxyService(
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<ILogger<ProxyService>>(),
                sp.GetRequiredService<HealthCheckService>(),
                appSettings.RequestTimeoutSeconds,
                appSettings.DebugPrompt));
        // Configure IHttpClientFactory for TCP connection pooling
        builder.Services.AddHttpClient("Proxy", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(appSettings.RequestTimeoutSeconds);
        })
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = limits?.MaxConcurrentConnections ?? 200,
            EnableMultipleHttp2Connections = true
        });

        builder.Services.AddHttpClient("HealthCheck", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(appSettings.HealthCheck?.TimeoutSeconds ?? 3);
        })
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 10
        });

        var app = builder.Build();

        // Use ApiKey middleware
        app.UseMiddleware<ApiKeyMiddleware>();

        // Health check endpoint (no auth by middleware)
        app.MapGet("/health", ProxyEndpoint.HandleHealthAsync);

        // List models endpoint (OpenAI-compatible)
        app.MapGet("/v1/models", async (HttpContext context) =>
        {
            var configReload = context.RequestServices.GetRequiredService<ConfigReloadService>();
            await ProxyEndpoint.HandleModelsAsync(context, configReload);
        });

        // Catch-all proxy for all /v1/* endpoints (OpenAI-compatible)
        app.Map("/v1/{**path}", async (HttpContext context) =>
        {
            var router = context.RequestServices.GetRequiredService<RouterService>();
            var proxy = context.RequestServices.GetRequiredService<ProxyService>();
            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ProxyEndpoint");
            await ProxyEndpoint.HandleProxyAsync(context, router, proxy, logger);
        });

        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        var listenUrl = appSettings.Kestrel?.Endpoints?.Http?.Url ?? "http://0.0.0.0:5000";
        logger.LogInformation("LLM Router starting on {Url}", listenUrl);
        logger.LogInformation("Health check: {Enabled}, Interval: {Interval}s",
            appSettings.HealthCheck?.Enabled ?? true,
            appSettings.HealthCheck?.IntervalSeconds ?? 30);

        await app.RunAsync();
    }

    private static AppSettings LoadAppSettings(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var settings = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings);
                return settings ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: Failed to load {path}: {ex.Message}");
        }

        return new AppSettings();
    }

    private static int ExtractPort(string url)
    {
        try
        {
            var uri = new Uri(url);
            return uri.Port > 0 ? uri.Port : 5000;
        }
        catch
        {
            return 5000;
        }
    }
}