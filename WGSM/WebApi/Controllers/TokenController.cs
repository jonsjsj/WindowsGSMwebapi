using System;
using Microsoft.AspNetCore.Mvc;
using WGSM.WebApi.Models;
using WGSM.WebApi.Services;

namespace WGSM.WebApi.Controllers
{
    [ApiController]
    [Route("api/token")]
    public class TokenController : ControllerBase
    {
        private readonly WebApiConfig _config;
        private readonly NetworkInfoService _network;

        public TokenController(WebApiConfig config, NetworkInfoService network)
        {
            _config = config;
            _network = network;
        }

        // POST /api/token/generate
        // Called from the WPF settings panel — generates a new cryptographic token and saves it.
        [HttpPost("generate")]
        public IActionResult Generate()
        {
            _config.ApiToken = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            _config.Save();
            return Ok(new TokenResponse { Token = _config.ApiToken });
        }

        // DELETE /api/token/revoke
        // Clears the current token — all clients will be locked out until a new one is generated.
        [HttpDelete("revoke")]
        public IActionResult Revoke()
        {
            _config.ApiToken = string.Empty;
            _config.Save();
            return Ok(new ApiActionResult { Success = true, Message = "Token revoked. Generate a new token to re-enable access." });
        }

        // GET /api/status  (public — no auth required, used by UI to check connectivity)
        [HttpGet("/api/status")]
        public async System.Threading.Tasks.Task<IActionResult> Status()
        {
            var lanIp = _network.GetLanIp();
            var publicIp = await _network.GetPublicIpAsync();
            var port = _config.Port;
            var scheme = _config.HttpsEnabled ? "https" : "http";

            return Ok(new StatusResponse
            {
                Running = true,
                BindUrl = _network.BuildBindAddress(_config.Scope, port),
                LocalUrl = $"{scheme}://localhost:{port}/ui",
                LanUrl = $"{scheme}://{lanIp}:{port}/ui",
                PublicUrl = $"{scheme}://{publicIp}:{port}/ui",
                Scope = _config.Scope,
                Port = port
            });
        }
    }
}
