using Microsoft.AspNetCore.Mvc;
using WGSM.WebApi.Models;
using WGSM.WebApi.Services;

namespace WGSM.WebApi.Controllers
{
    /// <summary>
    /// Exposes the job-polling endpoint for cloud backup operations.
    /// The job is started via POST /api/servers/{id}/backup?destination=gdrive|onedrive
    /// (handled in ServerController) which delegates to CloudBackupService.
    /// </summary>
    [ApiController]
    [Route("api/cloud-backup")]
    public class CloudBackupController : ControllerBase
    {
        private readonly CloudBackupService _cloud;

        public CloudBackupController(CloudBackupService cloud) => _cloud = cloud;

        // GET /api/cloud-backup/jobs/{jobId}
        // Poll this until status === "done" or "failed".
        [HttpGet("jobs/{jobId}")]
        public IActionResult GetJob(string jobId)
        {
            if (!_cloud.Jobs.TryGetValue(jobId, out var job))
                return NotFound(new ApiActionResult { Success = false, Message = $"Job '{jobId}' not found." });

            return Ok(job);
        }
    }
}
