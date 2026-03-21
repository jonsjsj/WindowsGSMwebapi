using Microsoft.AspNetCore.Mvc;
using WindowsGSM.WebApi.Models;
using WindowsGSM.WebApi.Services;

namespace WindowsGSM.WebApi.Controllers
{
    [ApiController]
    [Route("api/resources")]
    public class ResourcesController : ControllerBase
    {
        private readonly ServerManagerService _manager;
        private readonly ResourceMonitorService _resources;

        public ResourcesController(ServerManagerService manager, ResourceMonitorService resources)
        {
            _manager   = manager;
            _resources = resources;
        }

        // GET /api/resources/summary
        [HttpGet("summary")]
        public IActionResult GetSummary()
        {
            var servers = _manager.GetAllServers();

            var summary = new ResourcesSummaryDto
            {
                TotalServers  = servers.Count,
                OnlineServers = 0,
                TotalCpuPercent = 0,
                TotalRamMb      = 0
            };

            foreach (var s in servers)
            {
                if (s.Status == "Started")
                    summary.OnlineServers++;

                var cpu = _resources.GetCpuPercent(s.Pid);
                var ram = _resources.GetRamMb(s.Pid);

                if (cpu.HasValue) summary.TotalCpuPercent += cpu.Value;
                if (ram.HasValue) summary.TotalRamMb      += ram.Value;
            }

            summary.TotalCpuPercent = System.Math.Round(summary.TotalCpuPercent, 1);
            summary.TotalRamMb      = System.Math.Round(summary.TotalRamMb, 1);

            return Ok(summary);
        }
    }
}
