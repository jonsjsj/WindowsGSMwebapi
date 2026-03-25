using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using WindowsGSM.WebApi.Models;
using WindowsGSM.WebApi.Services;

namespace WindowsGSM.WebApi.Controllers
{
    [ApiController]
    [Route("api/servers")]
    public class ServerController : ControllerBase
    {
        private readonly ServerManagerService   _manager;
        private readonly A2SQueryService        _a2s;
        private readonly ResourceMonitorService _resources;
        private readonly PortCheckService       _ports;
        private readonly PortManagementService  _fw;
        private readonly CloudBackupService     _cloud;

        public ServerController(
            ServerManagerService   manager,
            A2SQueryService        a2s,
            ResourceMonitorService resources,
            PortCheckService       ports,
            PortManagementService  fw,
            CloudBackupService     cloud)
        {
            _manager   = manager;
            _a2s       = a2s;
            _resources = resources;
            _ports     = ports;
            _fw        = fw;
            _cloud     = cloud;
        }

        // GET /api/servers
        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            var servers = _manager.GetAllServers();
            foreach (var s in servers)
                await EnrichAsync(s).ConfigureAwait(false);
            return Ok(servers);
        }

        // GET /api/servers/{id}
        [HttpGet("{id}")]
        public async Task<IActionResult> GetOne(string id)
        {
            var servers = _manager.GetAllServers();
            var server = servers.Find(s => s.Id == id);
            if (server == null)
                return NotFound(new ApiActionResult { Success = false, Message = $"Server '{id}' not found." });
            await EnrichAsync(server).ConfigureAwait(false);
            return Ok(server);
        }

        // Open or close auto-managed firewall rules for a server's ports.
        private void ApplyFirewall(string id, bool open)
        {
            var cfg = _manager.GetConfig(id);
            if (cfg == null) return;
            if (!int.TryParse(cfg.ServerPort, out var gp)) return;
            int.TryParse(cfg.QueryPort, out var qp);
            if (open)
                _fw.OpenPortsForServer(id, gp, qp);
            else
                _fw.ClosePortsForServer(id, gp, qp);
        }

        // Populate the extended fields that are only meaningful for running servers
        private async Task EnrichAsync(ServerDto s)
        {
            // Always track the PID so the resource monitor keeps a warm cache
            if (s.Pid.HasValue)
                _resources.TrackPid(s.Pid.Value);

            if (s.Status != "Started")
                return; // leave extended fields null for stopped servers

            // Resolve effective host for port checks (0.0.0.0 is not connectable)
            var host = string.IsNullOrWhiteSpace(s.ServerIp) || s.ServerIp == "0.0.0.0"
                       ? "127.0.0.1"
                       : s.ServerIp;

            // Run A2S query and port checks in parallel
            var a2sTask       = _a2s.QueryAsync(host, s.QueryPort);
            var gamePortTask  = _ports.IsReachableAsync(host, s.ServerPort);
            var queryPortTask = _ports.IsReachableAsync(host, s.QueryPort);

            await Task.WhenAll(a2sTask, gamePortTask, queryPortTask).ConfigureAwait(false);

            var players = a2sTask.Result;
            if (players.HasValue)
            {
                s.PlayersCurrent = players.Value.current;
                s.PlayersMax     = players.Value.max;
            }

            s.GamePortReachable  = gamePortTask.Result;
            s.QueryPortReachable = queryPortTask.Result;

            // CPU/RAM come from the background cache (may be null on first request)
            s.CpuPercent = _resources.GetCpuPercent(s.Pid);
            s.RamMb      = _resources.GetRamMb(s.Pid);
        }

        // POST /api/servers/{id}/start  →  202 Accepted, poll GET /api/servers/{id} for status
        [HttpPost("{id}/start")]
        public IActionResult Start(string id)
        {
            var (success, message) = _manager.Start(id);
            if (success)
                ApplyFirewall(id, open: true);
            var result = new ApiActionResult { Success = success, Message = message };
            return success ? Accepted(result) : BadRequest(result);
        }

        // POST /api/servers/{id}/stop
        [HttpPost("{id}/stop")]
        public IActionResult Stop(string id)
        {
            var (success, message) = _manager.Stop(id);
            if (success)
                ApplyFirewall(id, open: false);
            var result = new ApiActionResult { Success = success, Message = message };
            return success ? Accepted(result) : BadRequest(result);
        }

        // POST /api/servers/{id}/restart
        [HttpPost("{id}/restart")]
        public IActionResult Restart(string id)
        {
            var (success, message) = _manager.Restart(id);
            var result = new ApiActionResult { Success = success, Message = message };
            return success ? Accepted(result) : BadRequest(result);
        }

        // GET /api/servers/{id}/logs?count=200
        [HttpGet("{id}/logs")]
        public IActionResult GetLogs(string id, [FromQuery] int count = 200)
        {
            count = System.Math.Clamp(count, 1, 2000);
            var logs = _manager.GetServerLogs(id, count);
            return Ok(logs);
        }

        // POST /api/servers/{id}/command
        // Body: { "command": "status", "waitMs": 500 }
        [HttpPost("{id}/command")]
        public async Task<IActionResult> SendCommand(string id, [FromBody] SendCommandRequest req)
        {
            if (string.IsNullOrWhiteSpace(req?.Command))
                return BadRequest(new ApiActionResult { Success = false, Message = "command is required." });

            var (success, message) = await _manager.SendCommandAsync(id, req.Command, req.WaitMs);
            var result = new ApiActionResult { Success = success, Message = message };
            return success ? Ok(result) : BadRequest(result);
        }

        // POST /api/servers/{id}/update  →  202 Accepted
        [HttpPost("{id}/update")]
        public IActionResult Update(string id)
        {
            var (success, message) = _manager.Update(id);
            var result = new ApiActionResult { Success = success, Message = message };
            return success ? Accepted(result) : BadRequest(result);
        }

        // POST /api/servers/{id}/backup?destination=local|gdrive|onedrive  →  202 Accepted
        // For cloud destinations the response includes { jobId } — poll GET /api/cloud-backup/jobs/{jobId}.
        [HttpPost("{id}/backup")]
        public IActionResult Backup(string id, [FromQuery] string destination = "local")
        {
            if (destination == "gdrive" || destination == "onedrive")
            {
                var cfg = _cloud;  // just validates injection; actual check is inside StartJob
                try
                {
                    var jobId = _cloud.StartJob(id, destination);
                    return Accepted(new { jobId, message = $"Cloud backup to {destination} started." });
                }
                catch (System.Exception ex)
                {
                    return BadRequest(new ApiActionResult { Success = false, Message = ex.Message });
                }
            }

            // Default: local backup via WGSM
            var (success, message) = _manager.Backup(id);
            var result = new ApiActionResult { Success = success, Message = message };
            return success ? Accepted(result) : BadRequest(result);
        }

        // GET /api/servers/{id}/backups
        [HttpGet("{id}/backups")]
        public IActionResult ListBackups(string id)
        {
            var backups = _manager.ListBackupsForServer(id);
            return Ok(backups);
        }

        // POST /api/servers/{id}/restore  →  202 Accepted
        [HttpPost("{id}/restore")]
        public IActionResult RestoreBackup(string id, [FromBody] RestoreBackupRequest req)
        {
            if (string.IsNullOrWhiteSpace(req?.FileName))
                return BadRequest(new ApiActionResult { Success = false, Message = "fileName is required." });

            var (success, message) = _manager.RestoreBackup(id, req.FileName);
            var result = new ApiActionResult { Success = success, Message = message };
            return success ? Accepted(result) : BadRequest(result);
        }

        // GET /api/servers/{id}/config
        [HttpGet("{id}/config")]
        public IActionResult GetConfig(string id)
        {
            var cfg = _manager.GetConfig(id);
            if (cfg == null)
                return NotFound(new ApiActionResult { Success = false, Message = $"Config for server '{id}' not found." });
            return Ok(cfg);
        }

        // PATCH /api/servers/{id}/config
        // Body: any subset of ServerConfigDto fields — only provided fields are updated
        [HttpPatch("{id}/config")]
        public IActionResult UpdateConfig(string id, [FromBody] UpdateServerConfigRequest req)
        {
            if (req == null)
                return BadRequest(new ApiActionResult { Success = false, Message = "Request body required." });

            var (success, message) = _manager.SaveConfig(id, req);
            var result = new ApiActionResult { Success = success, Message = message };
            return success ? Ok(result) : BadRequest(result);
        }
    }
}
