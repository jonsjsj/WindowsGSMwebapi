using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using WindowsGSM.WebApi.Models;
using WindowsGSM.WebApi.Services;

namespace WindowsGSM.WebApi.Controllers
{
    [ApiController]
    [Route("api/backup")]
    public class BackupController : ControllerBase
    {
        private readonly BackupService _backup;

        public BackupController(BackupService backup) => _backup = backup;

        // POST /api/backup/create
        [HttpPost("create")]
        public IActionResult Create()
        {
            var (success, message, zipPath) = _backup.CreateBackup();
            if (!success)
                return BadRequest(new ApiActionResult { Success = false, Message = message });

            return Ok(new BackupResultDto
            {
                Success  = true,
                Message  = message,
                FileName = zipPath != null ? Path.GetFileName(zipPath) : null
            });
        }

        // GET /api/backup/list
        [HttpGet("list")]
        public IActionResult List()
        {
            var files = _backup.ListLocalBackups()
                .Select(f => new { name = Path.GetFileName(f), size = new FileInfo(f).Length })
                .OrderByDescending(f => f.name)
                .ToList();
            return Ok(files);
        }

        // POST /api/servers/{id}/backups/upload
        // Accepts a backup ZIP via application/octet-stream; filename from X-Backup-Filename header.
        // Used by the TrueNAS dashboard migration feature.
        [HttpPost("/api/servers/{id}/backups/upload")]
        [RequestSizeLimit(2L * 1024 * 1024 * 1024)] // 2 GB max
        public async Task<IActionResult> Upload(string id,
            [FromHeader(Name = "X-Backup-Filename")] string? filename)
        {
            if (string.IsNullOrWhiteSpace(filename))
                filename = $"backup-upload-{System.DateTime.UtcNow:yyyyMMdd-HHmmss}.zip";

            // Sanitise: strip any path components the caller may have included
            filename = Path.GetFileName(filename);
            if (string.IsNullOrEmpty(filename))
                return BadRequest(new ApiActionResult { Success = false, Message = "Invalid filename." });

            var backupDir = Path.Combine(WgsmPath.AppDir, "backups");
            Directory.CreateDirectory(backupDir);
            var destPath = Path.Combine(backupDir, filename);

            using (var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None))
                await Request.Body.CopyToAsync(fs);

            return Ok(new ApiActionResult { Success = true, Message = $"Uploaded to {filename}." });
        }
    }
}
