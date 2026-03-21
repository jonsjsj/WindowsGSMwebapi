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
        private readonly ServerManagerService  _manager;
        private readonly A2SQueryService       _a2s;
        private readonly ResourceMonitorService _resources;
        private readonly PortCheckService      _ports;

        public ServerController(
            ServerManagerService  manager,
            A2SQueryService       a2s,
            ResourceMonitorService resources,
            PortCheckService      ports)
        {
            _manager   = manager;
            _a2s       = a2s;
            _resources = resources;
            _ports     = ports;
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

        // POST /api/servers/{id}/start
        [HttpPost("{id}/start")]
        public async Task<IActionResult> Start(string id)
        {
            var (success, message) = await _manager.StartAsync(id);
            var result = new ApiActionResult { Success = success, Message = message };
            return success ? Ok(result) : BadRequest(result);
        }

        // POST /api/servers/{id}/stop
        [HttpPost("{id}/stop")]
        public async Task<IActionResult> Stop(string id)
        {
            var (success, message) = await _manager.StopAsync(id);
            var result = new ApiActionResult { Success = success, Message = message };
            return success ? Ok(result) : BadRequest(result);
        }

        // POST /api/servers/{id}/restart
        [HttpPost("{id}/restart")]
        public async Task<IActionResult> Restart(string id)
        {
            var (success, message) = await _manager.RestartAsync(id);
            var result = new ApiActionResult { Success = success, Message = message };
            return success ? Ok(result) : BadRequest(result);
        }

        // GET /api/servers/{id}/logs?count=200
        [HttpGet("{id}/logs")]
        public IActionResult GetLogs(string id, [FromQuery] int count = 200)
        {
            count = System.Math.Clamp(count, 1, 2000);
            var logs = _manager.GetServerLogs(id, count);
            return Ok(logs);
        }
    }
}
