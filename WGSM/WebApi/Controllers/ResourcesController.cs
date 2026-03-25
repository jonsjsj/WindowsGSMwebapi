using System;
using System.IO;
using Microsoft.AspNetCore.Mvc;
using WGSM.WebApi.Models;
using WGSM.WebApi.Services;

namespace WGSM.WebApi.Controllers
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
            };

            foreach (var s in servers)
            {
                if (s.Status == "Started")
                    summary.OnlineServers++;

                // Register PID so the background CPU sampler starts tracking it
                if (s.Pid.HasValue)
                    _resources.TrackPid(s.Pid.Value);

                var cpu = _resources.GetCpuPercent(s.Pid);
                var ram = _resources.GetRamMb(s.Pid);

                if (cpu.HasValue) summary.TotalCpuPercent += cpu.Value;
                if (ram.HasValue) summary.TotalRamMb      += ram.Value;
            }

            summary.TotalCpuPercent = Math.Round(summary.TotalCpuPercent, 1);
            summary.TotalRamMb      = Math.Round(summary.TotalRamMb, 1);

            // System total RAM via GC memory info (reflects installed physical memory)
            try
            {
                var gcInfo = GC.GetGCMemoryInfo();
                summary.SystemTotalRamMb = Math.Round(gcInfo.TotalAvailableMemoryBytes / (1024.0 * 1024.0), 0);
                if (summary.SystemTotalRamMb > 0)
                    summary.RamPercent = Math.Round(summary.TotalRamMb / summary.SystemTotalRamMb * 100, 1);
            }
            catch { /* best-effort */ }

            // Disk space for the drive where WGSM is installed
            try
            {
                var root = Path.GetPathRoot(WgsmPath.AppDir) ?? "C:\\";
                var drive = new DriveInfo(root);
                summary.DiskTotalGb = Math.Round(drive.TotalSize        / (1024.0 * 1024.0 * 1024.0), 1);
                summary.DiskFreeGb  = Math.Round(drive.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0), 1);
                summary.DiskUsedGb  = Math.Round(summary.DiskTotalGb - summary.DiskFreeGb, 1);
            }
            catch { /* best-effort */ }

            return Ok(summary);
        }
    }
}
