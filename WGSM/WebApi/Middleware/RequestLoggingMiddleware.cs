using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using WindowsGSM.WebApi.Services;

namespace WindowsGSM.WebApi.Middleware
{
    /// <summary>
    /// Logs every HTTP request: method, path, remote IP, response code, and elapsed time.
    /// </summary>
    public class RequestLoggingMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ApiLogger _logger;

        public RequestLoggingMiddleware(RequestDelegate next, ApiLogger logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var sw = Stopwatch.StartNew();

            var method = context.Request.Method;
            var path   = context.Request.Path.Value ?? "/";
            var remote = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            await _next(context);

            sw.Stop();
            var status = context.Response.StatusCode;
            _logger.Log($"{method} {path} from {remote} → {status} ({sw.ElapsedMilliseconds}ms)");
        }
    }
}
