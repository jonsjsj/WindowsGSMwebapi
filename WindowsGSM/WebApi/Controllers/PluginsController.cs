using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using WindowsGSM.WebApi;
using WindowsGSM.WebApi.Services;

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

        private readonly ServerManagerService _manager;
        public PluginsController(ServerManagerService manager) => _manager = manager;

        // ── Plugin registry (embedded JSON) ──────────────────────────────────
        private static readonly Lazy<List<RegistryEntry>> _registry = new(LoadRegistry);

        private static List<RegistryEntry> LoadRegistry()
        {
            try
            {
                var asm  = Assembly.GetExecutingAssembly();
                var name = asm.GetManifestResourceNames()
                              .FirstOrDefault(n => n.EndsWith("plugins-registry.json", StringComparison.OrdinalIgnoreCase));
                if (name == null) return new List<RegistryEntry>();
                using var stream = asm.GetManifestResourceStream(name)!;
                using var doc    = JsonDocument.Parse(stream);
                return doc.RootElement.EnumerateArray()
                    .Select(e => new RegistryEntry
                    {
                        Owner      = e.GetProperty("owner").GetString()      ?? "",
                        Repo       = e.GetProperty("repo").GetString()       ?? "",
                        PluginName = e.GetProperty("pluginName").GetString() ?? "",
                        FileName   = e.GetProperty("fileName").GetString()   ?? "",
                        Branch     = e.GetProperty("branch").GetString()     ?? "master",
                        FilePath   = e.GetProperty("filePath").GetString()   ?? "",
                    })
                    .ToList();
            }
            catch { return new List<RegistryEntry>(); }
        }

        private static RegistryEntry? FindInRegistry(string owner, string repo)
            => _registry.Value.FirstOrDefault(e =>
                e.Owner.Equals(owner, StringComparison.OrdinalIgnoreCase) &&
                e.Repo .Equals(repo,  StringComparison.OrdinalIgnoreCase));

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
        // Returns the curated plugin registry (filtered by optional query string).
        [HttpGet("api/plugins/available")]
        public IActionResult GetAvailable([FromQuery] string q = "")
        {
            Directory.CreateDirectory(PluginsDir);
            var installedFolders = Directory.GetDirectories(PluginsDir, "*.cs", SearchOption.TopDirectoryOnly)
                .Select(d => Path.GetFileName(d))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var all = _registry.Value;

            IEnumerable<RegistryEntry> filtered = all;
            if (!string.IsNullOrWhiteSpace(q))
            {
                var lower = q.ToLowerInvariant();
                filtered = all.Where(e =>
                    e.PluginName.ToLowerInvariant().Contains(lower) ||
                    e.Description.ToLowerInvariant().Contains(lower));
            }

            var items = filtered.Select(e => new
            {
                owner       = e.Owner,
                repo        = e.Repo,
                pluginName  = e.PluginName,
                fileName    = e.FileName,
                branch      = e.Branch,
                description = e.Description,
                stars       = e.Stars,
                htmlUrl     = $"https://github.com/{e.Owner}/{e.Repo}",
                installed   = installedFolders.Contains(e.FileName),
                rawUrl      = $"https://raw.githubusercontent.com/{e.Owner}/{e.Repo}/{e.Branch}/{e.FilePath}",
            }).ToList();

            return Ok(new { totalCount = items.Count, items });
        }

        // ── POST /api/plugins/install ─────────────────────────────────────────
        // Accepts either:
        //   { owner, repo, fileName, branch }  — standard GitHub install
        //   { githubUrl }                       — any GitHub URL (repo, blob, or raw)
        [HttpPost("api/plugins/install")]
        public async Task<IActionResult> InstallPlugin([FromBody] InstallPluginRequest body)
        {
            if (body == null) return BadRequest(new { error = "Request body required." });

            // ── raw URL fast-path: download directly, no resolution needed ──────
            // Handles deep paths like Owner/Repo/branch/dir/File.cs correctly.
            if (!string.IsNullOrWhiteSpace(body.GithubUrl) &&
                body.GithubUrl.TrimStart().StartsWith("https://raw.githubusercontent.com/", StringComparison.OrdinalIgnoreCase))
            {
                var rawUrl  = body.GithubUrl.Trim();
                var rawFile = rawUrl.Split('?')[0].Split('/').Last();
                if (!rawFile.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    return BadRequest(new { error = $"File '{rawFile}' is not a .cs file." });

                try
                {
                    var dlResp = await _http.GetAsync(rawUrl).ConfigureAwait(false);
                    if (!dlResp.IsSuccessStatusCode)
                        return StatusCode(502, new { error = $"Could not download {rawFile} (HTTP {(int)dlResp.StatusCode})." });

                    var content  = await dlResp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var destDir  = Path.Combine(PluginsDir, rawFile);
                    var destFile = Path.Combine(destDir, rawFile);
                    Directory.CreateDirectory(destDir);
                    await System.IO.File.WriteAllTextAsync(destFile, content).ConfigureAwait(false);

                    var reload = await _manager.ReloadPluginsAsync();
                    var fail   = reload.Failed.FirstOrDefault(f =>
                        f.FileName.Equals(rawFile, StringComparison.OrdinalIgnoreCase));
                    return Ok(new
                    {
                        ok      = fail == null,
                        message = fail == null
                            ? $"{rawFile} installed and loaded successfully."
                            : $"{rawFile} installed but failed to compile: {fail.Error}",
                        reload,
                    });
                }
                catch (Exception ex) { return StatusCode(500, new { error = ex.Message }); }
            }

            // ── resolve from a github.com blob/repo URL ───────────────────────
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
                var branch = string.IsNullOrWhiteSpace(body.Branch) ? "master" : body.Branch;

                // Check registry for the known file path first
                var regEntry = FindInRegistry(body.Owner!, body.Repo!);
                string downloadUrl;
                if (regEntry != null)
                {
                    downloadUrl = $"https://raw.githubusercontent.com/{regEntry.Owner}/{regEntry.Repo}/{regEntry.Branch}/{regEntry.FilePath}";
                }
                else
                {
                    downloadUrl = $"https://raw.githubusercontent.com/{body.Owner}/{body.Repo}/{branch}/{body.FileName}";
                }

                HttpResponseMessage? dlResp = await _http.GetAsync(downloadUrl).ConfigureAwait(false);

                if (dlResp == null || !dlResp.IsSuccessStatusCode)
                {
                    // Fallback: try repo-named subfolder, then full recursive tree search
                    dlResp = await _http.GetAsync(
                        $"https://raw.githubusercontent.com/{body.Owner}/{body.Repo}/{branch}/{body.Repo}/{body.FileName}")
                        .ConfigureAwait(false);
                }

                if (dlResp == null || !dlResp.IsSuccessStatusCode)
                {
                    var treeUrl = $"https://api.github.com/repos/{body.Owner}/{body.Repo}/git/trees/{branch}?recursive=1";
                    using var treeReq = new HttpRequestMessage(HttpMethod.Get, treeUrl);
                    treeReq.Headers.Accept.ParseAdd("application/vnd.github+json");
                    using var treeResp = await _http.SendAsync(treeReq).ConfigureAwait(false);
                    if (treeResp.IsSuccessStatusCode)
                    {
                        var treeJson = await treeResp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        using var treeDoc = JsonDocument.Parse(treeJson);
                        var match = treeDoc.RootElement.GetProperty("tree").EnumerateArray()
                            .Where(n => n.TryGetProperty("type", out var t) && t.GetString() == "blob")
                            .Select(n => n.GetProperty("path").GetString() ?? "")
                            .FirstOrDefault(p => Path.GetFileName(p).Equals(body.FileName, StringComparison.OrdinalIgnoreCase));
                        if (match != null)
                            dlResp = await _http.GetAsync(
                                $"https://raw.githubusercontent.com/{body.Owner}/{body.Repo}/{branch}/{match}")
                                .ConfigureAwait(false);
                    }
                }

                if (dlResp == null || !dlResp.IsSuccessStatusCode)
                    return StatusCode(502, new { error = $"Could not find {body.FileName} anywhere in {body.Owner}/{body.Repo}." });

                var content  = await dlResp.Content.ReadAsStringAsync().ConfigureAwait(false);

                var destDir  = Path.Combine(PluginsDir, body.FileName);
                var destFile = Path.Combine(destDir, body.FileName);
                Directory.CreateDirectory(destDir);
                await System.IO.File.WriteAllTextAsync(destFile, content).ConfigureAwait(false);

                var reload = await _manager.ReloadPluginsAsync();
                var thisPlugin = reload.Failed.FirstOrDefault(f =>
                    f.FileName.Equals(body.FileName, StringComparison.OrdinalIgnoreCase));
                return Ok(new
                {
                    ok      = thisPlugin == null,
                    message = thisPlugin == null
                        ? $"{body.FileName} installed and loaded successfully."
                        : $"{body.FileName} installed but failed to compile: {thisPlugin.Error}",
                    reload,
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
            var reload = await _manager.ReloadPluginsAsync();
            var thisPlugin = reload.Failed.FirstOrDefault(f =>
                f.FileName.Equals(body.FileName, StringComparison.OrdinalIgnoreCase));
            return Ok(new
            {
                ok      = thisPlugin == null,
                message = thisPlugin == null
                    ? $"{body.FileName} installed and loaded successfully."
                    : $"{body.FileName} installed but failed to compile: {thisPlugin.Error}",
                reload,
            });
        }

        // ── DELETE /api/plugins/{fileName} ────────────────────────────────────
        [HttpDelete("api/plugins/{fileName}")]
        public async Task<IActionResult> UninstallPlugin(string fileName)
        {
            if (!fileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { error = "fileName must end with .cs" });

            var destDir = Path.Combine(PluginsDir, fileName);
            if (!Directory.Exists(destDir))
                return NotFound(new { error = "Plugin not found." });

            Directory.Delete(destDir, recursive: true);
            await _manager.ReloadPluginsAsync();
            return Ok(new { ok = true, message = $"{fileName} removed and unloaded." });
        }

        // ── POST /api/plugins/reload ──────────────────────────────────────────
        // Recompiles all installed plugins without restarting WGSM
        [HttpPost("api/plugins/reload")]
        public async Task<IActionResult> Reload()
        {
            var result = await _manager.ReloadPluginsAsync();
            return Ok(new
            {
                ok      = result.Failed.Count == 0,
                message = $"Reload complete. Loaded: {result.Loaded.Count}, Failed: {result.Failed.Count}",
                result,
            });
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

        private class RegistryEntry
        {
            public string Owner       { get; set; } = "";
            public string Repo        { get; set; } = "";
            public string PluginName  { get; set; } = "";
            public string FileName    { get; set; } = "";
            public string Branch      { get; set; } = "master";
            public string FilePath    { get; set; } = "";
            public string Description { get; set; } = "";
            public int    Stars       { get; set; }
        }
    }
}
