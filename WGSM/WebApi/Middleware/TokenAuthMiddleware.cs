using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using WindowsGSM.WebApi.Models;
using WindowsGSM.WebApi.Services;

namespace WindowsGSM.WebApi.Middleware
{
    /// <summary>
    /// Validates the Authorization: Bearer {token} header on all /api/* requests.
    /// /ui/*, /, and /api/info are public. Everything else requires a valid API key.
    /// </summary>
    public class TokenAuthMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly WebApiConfig    _config;
        private readonly ApiLogger       _logger;

        public TokenAuthMiddleware(RequestDelegate next, WebApiConfig config, ApiLogger logger)
        {
            _next   = next;
            _config = config;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var path   = context.Request.Path.Value ?? "";
            var remote = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            // Public routes — no token required
            // OAuth start/callback routes are opened directly in a browser popup,
            // so they cannot carry a Bearer token.
            if (path.StartsWith("/ui") || path == "/" || path == "/api/info" ||
                path.StartsWith("/api/oauth/google/start")    ||
                path.StartsWith("/api/oauth/google/callback") ||
                path.StartsWith("/api/oauth/onedrive/start")  ||
                path.StartsWith("/api/oauth/onedrive/callback"))
            {
                await _next(context);
                return;
            }

            if (path.StartsWith("/api"))
            {
                var activeKeys = _config.ApiKeys.Where(k => !string.IsNullOrEmpty(k.Token)).ToList();

                if (activeKeys.Count == 0)
                {
                    _logger.Log($"AUTH DENIED [{remote}] {path} — no API keys configured (add one in Web API settings)");
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    await context.Response.WriteAsJsonAsync(new { error = "No API keys configured." });
                    return;
                }

                var authHeader = context.Request.Headers["Authorization"].ToString();
                var matchedKey = activeKeys.FirstOrDefault(k =>
                    string.Equals(authHeader, $"Bearer {k.Token}", System.StringComparison.Ordinal));

                if (matchedKey == null)
                {
                    var provided = string.IsNullOrEmpty(authHeader) ? "(none)" : authHeader;
                    _logger.Log($"AUTH DENIED [{remote}] {path} — wrong token. Provided: {provided}");
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsJsonAsync(new { error = "Invalid or missing API token." });
                    return;
                }

                _logger.Log($"AUTH OK    [{remote}] {path} (key: {matchedKey.Name})");
            }

            await _next(context);
        }
    }
}
