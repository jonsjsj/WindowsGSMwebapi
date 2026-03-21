using System.IO;
using System.Linq;
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
    }
}
