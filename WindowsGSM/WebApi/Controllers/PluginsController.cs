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
        // Accepts either:
        //   { owner, repo, fileName, branch }  — standard GitHub install
        //   { githubUrl }                       — any GitHub URL (repo, blob, or raw)
        [HttpPost("api/plugins/install")]
        public async Task<IActionResult> InstallPlugin([FromBody] InstallPluginRequest body)
        {
            if (body == null) return BadRequest(new { error = "Request body required." });

            // ── resolve from a raw/blob/repo GitHub URL ───────────────────────
            if (!string.IsNullOrWhiteSpace(body.GithubUrl))
            {
                var resolved = await ResolveGithubUrl(body.GithubUrl);
                if (resolved.error != null)
                    return BadRequest(new { error = resolved.error });
                body.Owner    = resolved.owner;
                body.Repo     = resolved.repo;
                body.Branch   = resolved.branch;
                body.FileName = resolved.fileName;
            }

            if (string.IsNullOrWhiteSpace(body.Owner) ||
                string.IsNullOrWhiteSpace(body.Repo) ||
                string.IsNullOrWhiteSpace(body.FileName))
                return BadRequest(new { error = "Provide either githubUrl or owner + repo + fileName." });

            if (!body.FileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { error = "Resolved file is not a .cs file." });

            try
            {
                var branch   = string.IsNullOrWhiteSpace(body.Branch) ? "master" : body.Branch;
                var rawUrl   = $"https://raw.githubusercontent.com/{body.Owner}/{body.Repo}/{branch}/{body.FileName}";
                var content  = await _http.GetStringAsync(rawUrl).ConfigureAwait(false);

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

        // ── POST /api/plugins/save ───────────────────────────────────────────────
        // Body: { "fileName": "Enshrouded.cs", "content": "..." }
        // The browser fetches the raw .cs content itself and POSTs it here — no
        // server-side URL resolution needed, works with any WGSM version.
        [HttpPost("api/plugins/save")]
        public async Task<IActionResult> SavePlugin([FromBody] SavePluginRequest body)
        {
            if (string.IsNullOrWhiteSpace(body?.FileName) || string.IsNullOrWhiteSpace(body?.Content))
                return BadRequest(new { error = "fileName and content are required." });
            if (!body.FileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { error = "fileName must end with .cs" });

            var destDir  = Path.Combine(PluginsDir, body.FileName);
            var destFile = Path.Combine(destDir, body.FileName);
            Directory.CreateDirectory(destDir);
            await System.IO.File.WriteAllTextAsync(destFile, body.Content).ConfigureAwait(false);
            return Ok(new { ok = true, message = $"{body.FileName} installed. Restart WGSM to load the plugin." });
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

        // ── URL resolver ──────────────────────────────────────────────────────
        /// <summary>
        /// Accepts any of:
        ///   https://github.com/Owner/Repo
        ///   https://github.com/Owner/Repo/blob/branch/File.cs
        ///   https://raw.githubusercontent.com/Owner/Repo/branch/File.cs
        /// Returns (owner, repo, branch, fileName) or (error).
        /// </summary>
        private async Task<(string? owner, string? repo, string? branch, string? fileName, string? error)>
            ResolveGithubUrl(string url)
        {
            url = url.Trim();

            // raw.githubusercontent.com/Owner/Repo/branch/File.cs
            if (url.StartsWith("https://raw.githubusercontent.com/", StringComparison.OrdinalIgnoreCase))
            {
                var parts = url["https://raw.githubusercontent.com/".Length..].Split('/');
                if (parts.Length < 4)
                    return (null, null, null, null, "Raw URL must be: raw.githubusercontent.com/Owner/Repo/branch/File.cs");
                var file = parts[3];
                if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    return (null, null, null, null, $"File '{file}' is not a .cs file.");
                return (parts[0], parts[1], parts[2], file, null);
            }

            // github.com/Owner/Repo/blob/branch/File.cs  or  /tree/branch/File.cs
            if (url.Contains("/blob/", StringComparison.OrdinalIgnoreCase) ||
                url.Contains("/tree/", StringComparison.OrdinalIgnoreCase))
            {
                // path = Owner/Repo/blob/branch/File.cs  or  Owner/Repo/tree/branch/File.cs
                var path  = new Uri(url).AbsolutePath.TrimStart('/');
                var parts = path.Split('/');
                // parts[2] is "blob" or "tree", parts[3] is branch, parts[4+] is file path
                if (parts.Length < 5)
                    return (null, null, null, null, "URL must include branch and filename (e.g. /blob/main/Plugin.cs).");
                var file     = string.Join("/", parts[4..]);
                var fileName = Path.GetFileName(file);
                if (!fileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    return (null, null, null, null, $"File '{fileName}' is not a .cs file.");
                return (parts[0], parts[1], parts[3], fileName, null);
            }

            // github.com/Owner/Repo  — scan repo root for .cs files
            if (url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("http://github.com/",  StringComparison.OrdinalIgnoreCase))
            {
                var path  = new Uri(url).AbsolutePath.TrimStart('/').TrimEnd('/');
                var parts = path.Split('/');
                if (parts.Length < 2)
                    return (null, null, null, null, "GitHub URL must be: github.com/Owner/Repo");

                var owner = parts[0];
                var repo  = parts[1];

                // Use GitHub Contents API to find .cs files in root
                var apiUrl = $"https://api.github.com/repos/{owner}/{repo}/contents/";
                using var req = new HttpRequestMessage(HttpMethod.Get, apiUrl);
                req.Headers.Accept.ParseAdd("application/vnd.github+json");
                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    return (null, null, null, null, $"Could not access repo: GitHub returned {(int)resp.StatusCode}");

                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);

                var csFiles = doc.RootElement.EnumerateArray()
                    .Where(f => f.GetProperty("type").GetString() == "file" &&
                                f.GetProperty("name").GetString()!.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    .Select(f => f.GetProperty("name").GetString()!)
                    .ToList();

                if (csFiles.Count == 0)
                    return (null, null, null, null, $"No .cs files found in root of {owner}/{repo}.");

                // Prefer file matching repo name (WindowsGSM.Minecraft → Minecraft.cs)
                var pluginName = repo.StartsWith("WindowsGSM.", StringComparison.OrdinalIgnoreCase)
                    ? repo["WindowsGSM.".Length..] + ".cs"
                    : repo + ".cs";
                var best = csFiles.FirstOrDefault(f => f.Equals(pluginName, StringComparison.OrdinalIgnoreCase))
                        ?? csFiles[0];

                return (owner, repo, "master", best, null);
            }

            return (null, null, null, null, "Unsupported URL. Paste a GitHub repo, blob, or raw URL.");
        }

        public class SavePluginRequest
        {
            public string? FileName { get; set; }
            public string? Content  { get; set; }
        }

        public class InstallPluginRequest
        {
            public string? Owner     { get; set; }
            public string? Repo      { get; set; }
            public string? FileName  { get; set; }
            public string? Branch    { get; set; }
            public string? GithubUrl { get; set; }
        }
    }
}
