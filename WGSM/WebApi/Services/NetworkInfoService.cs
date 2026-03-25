using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using WindowsGSM.WebApi.Models;

namespace WindowsGSM.WebApi.Services
{
    /// <summary>
    /// Resolves the host's LAN IPv4 address and public IP.
    /// Public IP is cached for 2 minutes (same interval Raziel7893's webhook uses).
    /// </summary>
    public class NetworkInfoService
    {
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        private string _cachedPublicIp = string.Empty;
        private DateTime _cacheExpiry = DateTime.MinValue;
        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);

        public string GetLanIp()
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.Connect("8.8.8.8", 80);
                return (socket.LocalEndPoint as IPEndPoint)?.Address.ToString() ?? "127.0.0.1";
            }
            catch
            {
                // Fallback: first non-loopback IPv4
                return Dns.GetHostAddresses(Dns.GetHostName())
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                    ?.ToString() ?? "127.0.0.1";
            }
        }

        public async Task<string> GetPublicIpAsync()
        {
            await _lock.WaitAsync();
            try
            {
                if (DateTime.UtcNow < _cacheExpiry && !string.IsNullOrEmpty(_cachedPublicIp))
                    return _cachedPublicIp;

                try
                {
                    _cachedPublicIp = (await _http.GetStringAsync("https://ipinfo.io/ip")).Trim();
                    _cacheExpiry = DateTime.UtcNow.AddMinutes(2);
                }
                catch
                {
                    _cachedPublicIp = "unavailable";
                }
                return _cachedPublicIp;
            }
            finally
            {
                _lock.Release();
            }
        }

        public string BuildBindAddress(ConnectionScope scope, int port)
        {
            return scope switch
            {
                ConnectionScope.LocalOnly => $"http://localhost:{port}",
                ConnectionScope.LAN => $"http://{GetLanIp()}:{port};http://localhost:{port}",
                ConnectionScope.External => $"http://0.0.0.0:{port}",
                _ => $"http://localhost:{port}"
            };
        }
    }
}
