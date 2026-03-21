using Microsoft.AspNetCore.Mvc;
using WindowsGSM.WebApi.Models;
using WindowsGSM.WebApi.Services;

namespace WindowsGSM.WebApi.Controllers
{
    [ApiController]
    [Route("api/ports")]
    public class PortsController : ControllerBase
    {
        private readonly PortManagementService _fw;

        public PortsController(PortManagementService fw) => _fw = fw;

        // GET /api/ports/{port}/status?protocol=TCP
        [HttpGet("{port:int}/status")]
        public IActionResult GetStatus(int port, [FromQuery] string protocol = "TCP")
        {
            var (exists, enabled) = _fw.GetFirewallStatus(port, protocol);
            return Ok(new FirewallStatusDto
            {
                Port      = port,
                Protocol  = protocol.ToUpper(),
                RuleExists = exists,
                IsEnabled  = enabled
            });
        }

        // POST /api/ports/{port}/open
        [HttpPost("{port:int}/open")]
        public IActionResult OpenPort(int port, [FromQuery] string protocol = "TCP")
        {
            var (success, message) = _fw.OpenPort(port, protocol);
            var result = new ApiActionResult { Success = success, Message = message };
            return success ? Ok(result) : BadRequest(result);
        }

        // DELETE /api/ports/{port}/close
        [HttpDelete("{port:int}/close")]
        public IActionResult ClosePort(int port, [FromQuery] string protocol = "TCP")
        {
            var (success, message) = _fw.ClosePort(port, protocol);
            var result = new ApiActionResult { Success = success, Message = message };
            return success ? Ok(result) : BadRequest(result);
        }
    }
}
