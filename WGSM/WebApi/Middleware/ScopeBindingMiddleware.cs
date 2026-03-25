using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using WGSM.WebApi.Models;
using WGSM.WebApi.Services;

namespace WGSM.WebApi.Middleware
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
        private readonly ApiLogger _logger;

        public ScopeBindingMiddleware(RequestDelegate next, WebApiConfig config, NetworkInfoService network, ApiLogger logger)
        {
            _next = next;
            _config = config;
            _network = network;
            _logger = logger;
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
                _logger.Log($"SCOPE DENIED [{remoteIp}] — scope is {_config.Scope}, remote IP not allowed. Change scope in Web API settings to allow this connection.");
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

            // IPv6 link-local (fe80::/10) and unique-local (fc00::/7)
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                var b = ip.GetAddressBytes();
                if ((b[0] & 0xFE) == 0xFC) return true; // fc00::/7 — unique local
                if (b[0] == 0xFE && (b[1] & 0xC0) == 0x80) return true; // fe80::/10 — link-local
                return false;
            }

            var bytes = ip.GetAddressBytes();
            if (bytes.Length != 4) return false;

            return bytes[0] == 10                                               // RFC 1918 10.0.0.0/8
                || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)       // RFC 1918 172.16.0.0/12
                || (bytes[0] == 192 && bytes[1] == 168)                         // RFC 1918 192.168.0.0/16
                || (bytes[0] == 169 && bytes[1] == 254)                         // RFC 3927 169.254.0.0/16 link-local (APIPA / VMs)
                || (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127);     // RFC 6598 100.64.0.0/10 CGNAT (Tailscale, carrier NAT)
        }
    }
}
