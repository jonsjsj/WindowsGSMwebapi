using Microsoft.AspNetCore.Mvc;
using WindowsGSM.WebApi;

namespace WindowsGSM.WebApi.Controllers
{
    /// <summary>
    /// Serves the single-page frontend app for any /ui/* request.
    /// Static files (app.js, etc.) are served directly by the static file middleware.
    /// This controller handles HTML5 history API fallback so deep links work.
    /// </summary>
    [ApiController]
    public class UiController : ControllerBase
    {
        // GET /ui  and  GET /ui/{*any}
        [HttpGet("/ui")]
        [HttpGet("/ui/{*any}")]
        public IActionResult Index()
        {
            // index.html is embedded as a static file under wwwroot/
            return PhysicalFile(
                WgsmPath.Combine("WebApi", "wwwroot", "index.html"),
                "text/html");
        }

        // GET / — redirect to /ui
        [HttpGet("/")]
        public IActionResult Root() => Redirect("/ui");
    }
}
