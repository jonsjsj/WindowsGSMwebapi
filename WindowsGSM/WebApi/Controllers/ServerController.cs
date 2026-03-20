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
        private readonly ServerManagerService _manager;

        public ServerController(ServerManagerService manager)
        {
            _manager = manager;
        }

        // GET /api/servers
        [HttpGet]
        public IActionResult GetAll()
        {
            var servers = _manager.GetAllServers();
            return Ok(servers);
        }

        // GET /api/servers/{id}
        [HttpGet("{id}")]
        public IActionResult GetOne(string id)
        {
            var servers = _manager.GetAllServers();
            var server = servers.Find(s => s.Id == id);
            if (server == null) return NotFound(new ActionResult { Success = false, Message = $"Server '{id}' not found." });
            return Ok(server);
        }

        // POST /api/servers/{id}/start
        [HttpPost("{id}/start")]
        public async Task<IActionResult> Start(string id)
        {
            var (success, message) = await _manager.StartAsync(id);
            var result = new ActionResult { Success = success, Message = message };
            return success ? Ok(result) : BadRequest(result);
        }

        // POST /api/servers/{id}/stop
        [HttpPost("{id}/stop")]
        public async Task<IActionResult> Stop(string id)
        {
            var (success, message) = await _manager.StopAsync(id);
            var result = new ActionResult { Success = success, Message = message };
            return success ? Ok(result) : BadRequest(result);
        }

        // POST /api/servers/{id}/restart
        [HttpPost("{id}/restart")]
        public async Task<IActionResult> Restart(string id)
        {
            var (success, message) = await _manager.RestartAsync(id);
            var result = new ActionResult { Success = success, Message = message };
            return success ? Ok(result) : BadRequest(result);
        }
    }
}
