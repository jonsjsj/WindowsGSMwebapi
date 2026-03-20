using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using WindowsGSM.WebApi.Models;
using WindowsGSM.WebApi.Services;

namespace WindowsGSM.WebApi.Middleware
{
    /// <summary>
    /// Enforces the connection scope at the request level as a defence-in-depth layer.
    /// Kestrel's bind address already restricts listening, but this middleware provides
    /// an explicit reject + log for requests that somehow arrive outside their allowed scope.
    /// </summary>
    public class ScopeBindingMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly WebApiConfig _config;
        private readonly NetworkInfoService _network;

        public ScopeBindingMiddleware(RequestDelegate next, WebApiConfig config, NetworkInfoService network)
        {
            _next = next;
            _config = config;
            _network = network;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            // External scope: all IPs allowed — pass through
            if (_config.Scope == ConnectionScope.External)
            {
                await _next(context);
                return;
            }

            var remoteIp = context.Connection.RemoteIpAddress;
            if (remoteIp == null)
            {
                await _next(context);
                return;
            }

            // Normalise IPv4-mapped-IPv6 (::ffff:127.0.0.1 → 127.0.0.1)
            if (remoteIp.IsIPv4MappedToIPv6)
                remoteIp = remoteIp.MapToIPv4();

            bool allowed = _config.Scope switch
            {
                ConnectionScope.LocalOnly => IPAddress.IsLoopback(remoteIp),
                ConnectionScope.LAN => IsLanOrLoopback(remoteIp),
                _ => true
            };

            if (!allowed)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = $"Access denied: server is configured for {_config.Scope} access only."
                });
                return;
            }

            await _next(context);
        }

        private bool IsLanOrLoopback(IPAddress ip)
        {
            if (IPAddress.IsLoopback(ip)) return true;
            // RFC 1918 private ranges
            var bytes = ip.GetAddressBytes();
            if (bytes.Length != 4) return false;
            return bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                || (bytes[0] == 192 && bytes[1] == 168);
        }
    }
}
