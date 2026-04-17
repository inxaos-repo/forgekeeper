namespace Forgekeeper.Api.Middleware;

public class ApiKeyMiddleware
{
    private readonly RequestDelegate _next;
    private const string ApiKeyHeaderName = "X-Api-Key";

    public ApiKeyMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Skip auth for health checks and swagger
        var path = context.Request.Path.Value?.ToLower() ?? "";
        if (path.StartsWith("/health") || path.StartsWith("/swagger"))
        {
            await _next(context);
            return;
        }

        // If no API key is configured, allow all requests (backward compat)
        var configuredKey = context.RequestServices
            .GetRequiredService<IConfiguration>()
            .GetValue<string>("Security:ApiKey");

        if (string.IsNullOrEmpty(configuredKey))
        {
            await _next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue(ApiKeyHeaderName, out var providedKey) ||
            !string.Equals(providedKey, configuredKey, StringComparison.Ordinal))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "Invalid or missing API key" });
            return;
        }

        await _next(context);
    }
}
