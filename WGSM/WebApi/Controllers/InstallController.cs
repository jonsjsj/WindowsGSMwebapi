using Microsoft.AspNetCore.Mvc;
using WindowsGSM.WebApi.Models;
using WindowsGSM.WebApi.Services;

namespace WindowsGSM.WebApi.Controllers
{
    [ApiController]
    public class InstallController : ControllerBase
    {
        private readonly ServerManagerService _manager;
        public InstallController(ServerManagerService manager) => _manager = manager;

        // GET /api/games  — list all installable game names
        [HttpGet("api/games")]
        public IActionResult GetGames()
        {
            var games = _manager.GetAvailableGames();
            return Ok(new { totalCount = games.Count, items = games });
        }

        // POST /api/servers/install
        // Body: { "game": "Minecraft: Java Edition Server", "serverName": "My MC Server" }
        [HttpPost("api/servers/install")]
        public IActionResult StartInstall([FromBody] InstallRequest req)
        {
            if (string.IsNullOrWhiteSpace(req?.Game))
                return BadRequest(new ApiActionResult { Success = false, Message = "game is required." });

            var name = string.IsNullOrWhiteSpace(req.ServerName) ? req.Game : req.ServerName;
            var job  = _manager.StartInstall(req.Game, name);
            return Accepted(new { jobId = job.JobId, serverId = job.ServerId, message = "Install started." });
        }

        // GET /api/servers/install/{jobId}
        [HttpGet("api/servers/install/{jobId}")]
        public IActionResult PollInstall(string jobId)
        {
            var job = ServerManagerService.GetJob(jobId);
            if (job == null) return NotFound(new ApiActionResult { Success = false, Message = "Job not found." });

            return Ok(new
            {
                jobId    = job.JobId,
                serverId = job.ServerId,
                status   = job.Status,   // running | done | failed
                error    = job.Error,
                log      = job.Log,
            });
        }
    }

    public class InstallRequest
    {
        public string? Game       { get; set; }
        public string? ServerName { get; set; }
    }
}
