using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using WGSM.Functions;
using WGSM.WebApi.Models;

namespace WGSM.WebApi.Controllers
{
    /// <summary>
    /// Exposes the game server's serverfiles directory for browsing, reading and writing.
    /// Also lets callers download backup archives.
    /// All paths are validated to stay inside the server's serverfiles root — no path traversal.
    /// </summary>
    [ApiController]
    [Route("api/servers/{id}/files")]
    public class FileController : ControllerBase
    {
        // ── helpers ────────────────────────────────────────────────────────────

        /// <summary>
        /// Resolves and validates a relative path inside the serverfiles root.
        /// Returns null if the resolved path escapes the root (path traversal attempt).
        /// </summary>
        private static string? SafeResolve(string serverId, string relativePath)
        {
            var root = ServerPath.GetServersServerFiles(serverId);
            Directory.CreateDirectory(root);

            // Normalise – strip leading slashes and back-traversals at the edge
            var clean  = relativePath.Replace('/', Path.DirectorySeparatorChar)
                                     .TrimStart(Path.DirectorySeparatorChar, '/');
            var full   = Path.GetFullPath(Path.Combine(root, clean));
            var rootFull = Path.GetFullPath(root);

            // Must stay inside root
            if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                return null;

            return full;
        }

        // ── GET /api/servers/{id}/files?path=. ───────────────────────────────
        // Lists files and folders at the given relative path (non-recursive).
        [HttpGet]
        public IActionResult ListFiles(string id, [FromQuery] string path = ".")
        {
            var dir = SafeResolve(id, path == "." ? "" : path);
            if (dir == null) return BadRequest(new { error = "Invalid path." });

            if (!Directory.Exists(dir))
                return NotFound(new { error = "Directory not found." });

            var dirs = Directory.GetDirectories(dir)
                .Select(d => new FileEntryDto
                {
                    Name  = Path.GetFileName(d),
                    Path  = Path.GetRelativePath(ServerPath.GetServersServerFiles(id), d).Replace('\\', '/'),
                    IsDir = true,
                    Size  = 0,
                })
                .OrderBy(e => e.Name)
                .ToList();

            var files = Directory.GetFiles(dir)
                .Select(f =>
                {
                    var fi = new FileInfo(f);
                    return new FileEntryDto
                    {
                        Name     = fi.Name,
                        Path     = Path.GetRelativePath(ServerPath.GetServersServerFiles(id), f).Replace('\\', '/'),
                        IsDir    = false,
                        Size     = fi.Length,
                        Modified = fi.LastWriteTimeUtc,
                    };
                })
                .OrderBy(e => e.Name)
                .ToList();

            return Ok(new { path = path == "." ? "" : path, entries = dirs.Concat(files).ToList() });
        }

        // ── GET /api/servers/{id}/files/read?path=server.cfg ─────────────────
        // Returns the text content of a file (max 2 MB).
        [HttpGet("read")]
        public async Task<IActionResult> ReadFile(string id, [FromQuery] string path = "")
        {
            if (string.IsNullOrWhiteSpace(path))
                return BadRequest(new { error = "path query parameter is required." });

            var full = SafeResolve(id, path);
            if (full == null) return BadRequest(new { error = "Invalid path." });

            if (!System.IO.File.Exists(full))
                return NotFound(new { error = "File not found." });

            var fi = new FileInfo(full);
            if (fi.Length > 2 * 1024 * 1024)
                return BadRequest(new { error = "File exceeds 2 MB text limit. Use the download endpoint instead." });

            var content = await System.IO.File.ReadAllTextAsync(full).ConfigureAwait(false);
            return Ok(new { path, content, sizeByes = fi.Length, modified = fi.LastWriteTimeUtc });
        }

        // ── PUT /api/servers/{id}/files/write ────────────────────────────────
        // Body: { "path": "server.cfg", "content": "..." }
        [HttpPut("write")]
        public async Task<IActionResult> WriteFile(string id, [FromBody] WriteFileRequest body)
        {
            if (string.IsNullOrWhiteSpace(body?.Path) || body.Content == null)
                return BadRequest(new { error = "path and content are required." });

            var full = SafeResolve(id, body.Path);
            if (full == null) return BadRequest(new { error = "Invalid path." });

            // Refuse to write binary-looking extensions
            var ext = Path.GetExtension(full).ToLowerInvariant();
            var textExts = new HashSet<string>
            {
                ".cfg", ".ini", ".json", ".yaml", ".yml", ".txt", ".conf", ".config",
                ".properties", ".xml", ".toml", ".env", ".sh", ".bat", ".cmd",
                ".log", ".csv", ".lua", ".py", ".js", ".ts", ".md", "",
            };
            if (!textExts.Contains(ext))
                return BadRequest(new { error = $"Extension '{ext}' is not an editable text type." });

            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await System.IO.File.WriteAllTextAsync(full, body.Content).ConfigureAwait(false);
            return Ok(new { ok = true, path = body.Path, message = "File saved." });
        }

        // ── GET /api/servers/{id}/files/download?path=server.cfg ─────────────
        // Downloads a file as a binary attachment (works for all file types).
        [HttpGet("download")]
        public IActionResult Download(string id, [FromQuery] string path = "")
        {
            if (string.IsNullOrWhiteSpace(path))
                return BadRequest(new { error = "path query parameter is required." });

            var full = SafeResolve(id, path);
            if (full == null) return BadRequest(new { error = "Invalid path." });

            if (!System.IO.File.Exists(full))
                return NotFound(new { error = "File not found." });

            var name = Path.GetFileName(full);
            return PhysicalFile(full, "application/octet-stream", name);
        }

        // ── GET /api/servers/{id}/backups/{fileName}/download ─────────────────
        // Downloads a backup ZIP file.
        [HttpGet("/api/servers/{id}/backups/{fileName}/download")]
        public IActionResult DownloadBackup(string id, string fileName)
        {
            // Reject directory traversal in fileName
            if (fileName.Contains('/') || fileName.Contains('\\') || fileName.Contains(".."))
                return BadRequest(new { error = "Invalid file name." });

            var dir  = ServerPath.GetBackups(id);
            var full = Path.Combine(dir, fileName);
            var rootFull = Path.GetFullPath(dir);
            if (!Path.GetFullPath(full).StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { error = "Invalid file name." });

            if (!System.IO.File.Exists(full))
                return NotFound(new { error = "Backup not found." });

            return PhysicalFile(full, "application/zip", fileName);
        }
    }

    public class WriteFileRequest
    {
        public string? Path    { get; set; }
        public string? Content { get; set; }
    }

    public class FileEntryDto
    {
        public string  Name     { get; set; } = "";
        public string  Path     { get; set; } = "";
        public bool    IsDir    { get; set; }
        public long    Size     { get; set; }
        public DateTime? Modified { get; set; }
    }
}
