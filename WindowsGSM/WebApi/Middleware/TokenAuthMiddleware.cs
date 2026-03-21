using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using WindowsGSM.WebApi.Models;
using WindowsGSM.WebApi.Services;

namespace WindowsGSM.WebApi.Middleware
{
    /// <summary>
    /// Validates the Authorization: Bearer {token} header on all /api/* requests.
    /// The /ui/* static SPA and /api/status GET are public (status shows only running state, no server data).
    /// </summary>
    public class TokenAuthMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly WebApiConfig _config;
        private readonly ApiLogger _logger;

        public TokenAuthMiddleware(RequestDelegate next, WebApiConfig config, ApiLogger logger)
        {
            _next = next;
            _config = config;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var path   = context.Request.Path.Value ?? "";
            var remote = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            // Allow SPA and its assets through unauthenticated
            if (path.StartsWith("/ui") || path == "/")
            {
                await _next(context);
                return;
            }

            // Validate token for all /api/* routes
            if (path.StartsWith("/api"))
            {
                if (string.IsNullOrEmpty(_config.ApiToken))
                {
                    _logger.Log($"AUTH DENIED [{remote}] {path} — no API token configured (generate one in the Web API settings)");
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    await context.Response.WriteAsJsonAsync(new { error = "No API token configured." });
                    return;
                }

                var authHeader     = context.Request.Headers["Authorization"].ToString();
                var expectedBearer = $"Bearer {_config.ApiToken}";

                if (!string.Equals(authHeader, expectedBearer, System.StringComparison.Ordinal))
                {
                    var provided = string.IsNullOrEmpty(authHeader) ? "(none)" : authHeader;
                    _logger.Log($"AUTH DENIED [{remote}] {path} — wrong token. Provided: {provided}");
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsJsonAsync(new { error = "Invalid or missing API token." });
                    return;
                }

                _logger.Log($"AUTH OK    [{remote}] {path}");
            }

            await _next(context);
        }
    }
}
