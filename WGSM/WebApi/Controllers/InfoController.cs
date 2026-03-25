using System.Linq;
using Microsoft.AspNetCore.Mvc;
using WGSM.WebApi.Models;
using WGSM.WebApi.Services;

namespace WGSM.WebApi.Controllers
{
    [ApiController]
    public class InfoController : ControllerBase
    {
        private readonly WebApiConfig          _config;
        private readonly ServerManagerService  _manager;

        public InfoController(WebApiConfig config, ServerManagerService manager)
        {
            _config  = config;
            _manager = manager;
        }

        // GET /api/info  — public, no auth required
        // Used by the dashboard to verify connectivity and get instance identity
        [HttpGet("api/info")]
        public IActionResult GetInfo()
        {
            var servers = _manager.GetAllServers();
            return Ok(new
            {
                instanceName  = _config.InstanceName,
                totalServers  = servers.Count,
                onlineServers = servers.Count(s => s.Status == "Started"),
                hasKeys       = _config.ApiKeys.Any(k => !string.IsNullOrEmpty(k.Token)),
                appVersion    = UpdateService.CurrentVersion
            });
        }
    }
}
