using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using WindowsGSM.WebApi;

namespace WindowsGSM.WebApi.Controllers
{
    [ApiController]
    public class PluginsController : ControllerBase
    {
        private static readonly HttpClient _http = new HttpClient();

        static PluginsController()
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("WGSM-WebAPI/1.0");
            _http.Timeout = TimeSpan.FromSeconds(15);
        }

        private static readonly string PluginsDir = WgsmPath.Combine("plugins");

        // ── GET /api/plugins/installed ────────────────────────────────────────
        [HttpGet("api/plugins/installed")]
        public IActionResult GetInstalled()
        {
            Directory.CreateDirectory(PluginsDir);
            var plugins = Directory.GetDirectories(PluginsDir, "*.cs", SearchOption.TopDirectoryOnly)
                .Select(dir =>
                {
                    var name = Path.GetFileNameWithoutExtension(Path.GetFileName(dir)); // e.g. Minecraft
                    var file = Path.Combine(dir, Path.GetFileName(dir));               // e.g. Minecraft.cs
                    return new
                    {
                        name,
                        folder = Path.GetFileName(dir),
                        hasFile = System.IO.File.Exists(file),
                    };
                })
                .ToList();
            return Ok(plugins);
        }

        // ── GET /api/plugins/available ────────────────────────────────────────
        // Searches GitHub for repos with topic:windowsgsm
        [HttpGet("api/plugins/available")]
        public async Task<IActionResult> GetAvailable([FromQuery] string q = "")
        {
            try
            {
                var search = string.IsNullOrWhiteSpace(q)
                    ? "topic:windowsgsm"
                    : $"topic:windowsgsm {q} in:name";
                var url = $"https://api.github.com/search/repositories?q={Uri.EscapeDataString(search)}&sort=stars&order=desc&per_page=60";

                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Accept.ParseAdd("application/vnd.github+json");
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode)
                    return StatusCode(502, new { error = $"GitHub returned {(int)resp.StatusCode}" });

                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);

                // Get installed folder names for cross-reference
                Directory.CreateDirectory(PluginsDir);
                var installedFolders = Directory.GetDirectories(PluginsDir, "*.cs", SearchOption.TopDirectoryOnly)
                    .Select(d => Path.GetFileName(d))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var items = doc.RootElement.GetProperty("items").EnumerateArray()
                    .Select(item =>
                    {
                        var repoName   = item.GetProperty("name").GetString() ?? "";
                        var owner      = item.GetProperty("owner").GetProperty("login").GetString() ?? "";
                        var branch     = item.TryGetProperty("default_branch", out var b) ? b.GetString() ?? "master" : "master";
                        // Convention: plugin file is named after the repo minus "WindowsGSM." prefix
                        var pluginName = repoName.StartsWith("WindowsGSM.", StringComparison.OrdinalIgnoreCase)
                            ? repoName["WindowsGSM.".Length..]
                            : repoName;
                        var fileName   = pluginName + ".cs";
                        var installed  = installedFolders.Contains(fileName);

                        return new
                        {
                            owner,
                            repo        = repoName,
                            pluginName,
                            fileName,
                            branch,
                            description = item.TryGetProperty("description", out var d) ? d.GetString() : null,
                            stars       = item.GetProperty("stargazers_count").GetInt32(),
                            htmlUrl     = item.GetProperty("html_url").GetString(),
                            installed,
                            rawUrl      = $"https://raw.githubusercontent.com/{owner}/{repoName}/{branch}/{fileName}",
                        };
                    })
                    .ToList();

                return Ok(new { totalCount = doc.RootElement.GetProperty("total_count").GetInt32(), items });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // ── POST /api/plugins/install ─────────────────────────────────────────
        // Body: { "owner": "...", "repo": "...", "fileName": "Minecraft.cs", "branch": "master" }
        [HttpPost("api/plugins/install")]
        public async Task<IActionResult> InstallPlugin([FromBody] InstallPluginRequest body)
        {
            if (string.IsNullOrWhiteSpace(body?.Owner) ||
                string.IsNullOrWhiteSpace(body?.Repo) ||
                string.IsNullOrWhiteSpace(body?.FileName))
                return BadRequest(new { error = "owner, repo and fileName are required." });

            if (!body.FileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { error = "fileName must end with .cs" });

            try
            {
                var branch  = string.IsNullOrWhiteSpace(body.Branch) ? "master" : body.Branch;
                var rawUrl  = $"https://raw.githubusercontent.com/{body.Owner}/{body.Repo}/{branch}/{body.FileName}";
                var content = await _http.GetStringAsync(rawUrl).ConfigureAwait(false);

                var destDir  = Path.Combine(PluginsDir, body.FileName);
                var destFile = Path.Combine(destDir, body.FileName);
                Directory.CreateDirectory(destDir);
                await System.IO.File.WriteAllTextAsync(destFile, content).ConfigureAwait(false);

                return Ok(new
                {
                    ok      = true,
                    message = $"{body.FileName} installed. Restart WGSM to load the plugin.",
                    path    = destFile,
                });
            }
            catch (HttpRequestException ex)
            {
                return StatusCode(502, new { error = $"Failed to download plugin: {ex.Message}" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // ── DELETE /api/plugins/{fileName} ────────────────────────────────────
        [HttpDelete("api/plugins/{fileName}")]
        public IActionResult UninstallPlugin(string fileName)
        {
            if (!fileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { error = "fileName must end with .cs" });

            var destDir = Path.Combine(PluginsDir, fileName);
            if (!Directory.Exists(destDir))
                return NotFound(new { error = "Plugin not found." });

            Directory.Delete(destDir, recursive: true);
            return Ok(new { ok = true, message = $"{fileName} removed. Restart WGSM to unload." });
        }

        public class InstallPluginRequest
        {
            public string? Owner    { get; set; }
            public string? Repo     { get; set; }
            public string? FileName { get; set; }
            public string? Branch   { get; set; }
        }
    }
}
