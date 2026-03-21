using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using WindowsGSM.WebApi.Models;
using WindowsGSM.WebApi.Services;

namespace WindowsGSM.WebApi.Controllers
{
    [ApiController]
    [Route("api/update")]
    public class UpdateController : ControllerBase
    {
        private readonly UpdateService _updater;

        public UpdateController(UpdateService updater) => _updater = updater;

        // GET /api/update/check
        [HttpGet("check")]
        public async Task<IActionResult> Check()
        {
            var (hasUpdate, latestTag, downloadUrl, error) =
                await _updater.CheckForUpdateAsync().ConfigureAwait(false);

            if (error != null)
                return StatusCode(503, new ApiActionResult { Success = false, Message = error });

            return Ok(new UpdateCheckDto
            {
                CurrentVersion = UpdateService.CurrentVersion,
                LatestTag      = latestTag,
                HasUpdate      = hasUpdate,
                DownloadUrl    = downloadUrl
            });
        }

        // POST /api/update/apply  (body: { "downloadUrl": "..." })
        [HttpPost("apply")]
        public async Task<IActionResult> Apply([FromBody] ApplyUpdateRequest req)
        {
            if (string.IsNullOrWhiteSpace(req?.DownloadUrl))
                return BadRequest(new ApiActionResult { Success = false, Message = "downloadUrl is required." });

            var (success, message) = await _updater.ApplyUpdateAsync(req.DownloadUrl).ConfigureAwait(false);
            var result = new ApiActionResult { Success = success, Message = message };
            return success ? Ok(result) : BadRequest(result);
        }
    }
}
