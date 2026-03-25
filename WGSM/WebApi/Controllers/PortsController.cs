using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using WGSM.WebApi.Models;
using WGSM.WebApi.Services;

namespace WGSM.WebApi.Controllers
{
    [ApiController]
    [Route("api/ports")]
    public class PortsController : ControllerBase
    {
        private static readonly HttpClient _http = new();
        private readonly PortManagementService _fw;
        private readonly NetworkInfoService    _network;

        public PortsController(PortManagementService fw, NetworkInfoService network)
        {
            _fw      = fw;
            _network = network;
        }

        // GET /api/ports/{port}/status?protocol=TCP
        [HttpGet("{port:int}/status")]
        public IActionResult GetStatus(int port, [FromQuery] string protocol = "TCP")
        {
            var (exists, enabled) = _fw.GetFirewallStatus(port, protocol);
            return Ok(new FirewallStatusDto
            {
                Port      = port,
                Protocol  = protocol.ToUpper(),
                RuleExists = exists,
                IsEnabled  = enabled
            });
        }

        // POST /api/ports/{port}/open
        [HttpPost("{port:int}/open")]
        public IActionResult OpenPort(int port, [FromQuery] string protocol = "TCP")
        {
            var (success, message) = _fw.OpenPort(port, protocol);
            var result = new ApiActionResult { Success = success, Message = message };
            return success ? Ok(result) : BadRequest(result);
        }

        // DELETE /api/ports/{port}/close
        [HttpDelete("{port:int}/close")]
        public IActionResult ClosePort(int port, [FromQuery] string protocol = "TCP")
        {
            var (success, message) = _fw.ClosePort(port, protocol);
            var result = new ApiActionResult { Success = success, Message = message };
            return success ? Ok(result) : BadRequest(result);
        }

        // GET /api/ports/{port}/reachability?protocol=TCP
        // Asks portchecker.co whether the port is reachable from the internet.
        [HttpGet("{port:int}/reachability")]
        public async Task<IActionResult> GetReachability(int port, [FromQuery] string protocol = "TCP")
        {
            var publicIp = await _network.GetPublicIpAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(publicIp))
                return StatusCode(503, new ApiActionResult
                {
                    Success = false,
                    Message = "Could not determine public IP address."
                });

            try
            {
                var body = JsonSerializer.Serialize(new
                {
                    host  = publicIp,
                    ports = new[] { port.ToString() }
                });
                using var req = new HttpRequestMessage(HttpMethod.Post, "https://portchecker.co/api/v1/query");
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");

                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();

                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                bool reachable = doc.RootElement
                    .GetProperty("ports")[0]
                    .GetProperty("status")
                    .GetString() == "open";

                return Ok(new PortReachabilityDto
                {
                    Port      = port,
                    Protocol  = protocol.ToUpper(),
                    Reachable = reachable,
                    PublicIp  = publicIp
                });
            }
            catch (Exception ex)
            {
                return StatusCode(502, new ApiActionResult
                {
                    Success = false,
                    Message = $"Reachability check failed: {ex.Message}"
                });
            }
        }
    }
}
