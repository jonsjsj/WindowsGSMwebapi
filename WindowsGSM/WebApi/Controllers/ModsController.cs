using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using WindowsGSM.Functions;

namespace WindowsGSM.WebApi.Controllers
{
    [ApiController]
    public class ModsController : ControllerBase
    {
        private static readonly HttpClient _http = new HttpClient();
        
        // Proxy thunderstore API to bypass CORS
        [HttpGet("/api/mods/thunderstore")]
        public async Task<IActionResult> GetThunderstorePackages()
        {
            try
            {
                // Note: valheim.thunderstore.io returns a massive JSON file.
                // The frontend will cache this in memory.
                var json = await _http.GetStringAsync("https://valheim.thunderstore.io/api/v1/package/");
                return Content(json, "application/json");
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        [HttpGet("/api/servers/{id}/mods")]
        public IActionResult ListInstalledMods(string id)
        {
            var pluginsDir = Path.Combine(ServerPath.GetServersServerFiles(id), "BepInEx", "plugins");
            if (!Directory.Exists(pluginsDir)) 
                return Ok(new List<object>());

            var results = new List<object>();

            // List folders
            foreach(var dir in Directory.GetDirectories(pluginsDir))
            {
                var dirName = new DirectoryInfo(dir).Name;
                bool isEnabled = !dirName.EndsWith(".disabled");
                var cleanName = dirName.Replace(".disabled", "");
                
                results.Add(new {
                    Id = cleanName,
                    Name = cleanName,
                    Enabled = isEnabled,
                    Type = "Folder"
                });
            }

            // Also list floating .dll files just in case
            foreach(var file in Directory.GetFiles(pluginsDir))
            {
                var fileName = Path.GetFileName(file);
                if (fileName.EndsWith(".dll") || fileName.EndsWith(".dll.disabled"))
                {
                    bool isEnabled = !fileName.EndsWith(".disabled");
                    var cleanName = fileName.Replace(".disabled", "");
                    results.Add(new {
                        Id = cleanName,
                        Name = cleanName,
                        Enabled = isEnabled,
                        Type = "File"
                    });
                }
            }

            return Ok(results);
        }

        [HttpPost("/api/servers/{id}/mods/install")]
        public async Task<IActionResult> InstallMod(string id, [FromQuery] string downloadUrl)
        {
            if (string.IsNullOrWhiteSpace(downloadUrl)) 
                return BadRequest(new { Success = false, Message = "downloadUrl is required." });

            var serverFiles = ServerPath.GetServersServerFiles(id);
            var pluginsDir = Path.Combine(serverFiles, "BepInEx", "plugins");
            Directory.CreateDirectory(pluginsDir);

            try
            {
                var fileBytes = await _http.GetByteArrayAsync(downloadUrl);
                var tempFile = Path.GetTempFileName();
                System.IO.File.WriteAllBytes(tempFile, fileBytes);

                // e.g. https://.../denikson-BepInExPack_Valheim-5.4.2202.zip
                var uri = new Uri(downloadUrl);
                var fileName = Path.GetFileNameWithoutExtension(uri.LocalPath);
                
                // If the package is BepInEx itself, we extract differently (to root)
                if (fileName.Contains("BepInExPack"))
                {
                    // standard BepInEx zip has a nested BepInExPack_Valheim folder
                    // We extract to temp, then move contents to serverfiles
                    var extractTemp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
                    ZipFile.ExtractToDirectory(tempFile, extractTemp);
                    
                    var packPath = Path.Combine(extractTemp, "BepInExPack_Valheim");
                    if (Directory.Exists(packPath))
                    {
                        CopyDirectory(packPath, serverFiles);
                    }
                    Directory.Delete(extractTemp, true);
                }
                else
                {
                    // Normal plugin
                    var targetDir = Path.Combine(pluginsDir, fileName);
                    if (Directory.Exists(targetDir)) 
                        Directory.Delete(targetDir, true);
                    
                    ZipFile.ExtractToDirectory(tempFile, targetDir);
                }

                System.IO.File.Delete(tempFile);

                return Ok(new { Success = true, Message = "Mod installed successfully" });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Success = false, Message = ex.Message });
            }
        }

        [HttpPatch("/api/servers/{id}/mods/{modId}/toggle")]
        public IActionResult ToggleMod(string id, string modId, [FromQuery] bool enable)
        {
            var pluginsDir = Path.Combine(ServerPath.GetServersServerFiles(id), "BepInEx", "plugins");
            
            // Try Folder strategy
            var enabledDir = Path.Combine(pluginsDir, modId);
            var disabledDir = Path.Combine(pluginsDir, modId + ".disabled");

            if (enable && Directory.Exists(disabledDir))
            {
                Directory.Move(disabledDir, enabledDir);
                return Ok(new { Success = true, Message = "Enabled" });
            }
            if (!enable && Directory.Exists(enabledDir))
            {
                Directory.Move(enabledDir, disabledDir);
                return Ok(new { Success = true, Message = "Disabled" });
            }

            // Try File strategy
            var enabledFile = Path.Combine(pluginsDir, modId);
            var disabledFile = Path.Combine(pluginsDir, modId + ".disabled");

            if (enable && System.IO.File.Exists(disabledFile))
            {
                System.IO.File.Move(disabledFile, enabledFile);
                return Ok(new { Success = true, Message = "Enabled" });
            }
            if (!enable && System.IO.File.Exists(enabledFile))
            {
                System.IO.File.Move(enabledFile, disabledFile);
                return Ok(new { Success = true, Message = "Disabled" });
            }

            return BadRequest(new { Success = false, Message = "Mod not found or already in requested state" });
        }
        
        [HttpDelete("/api/servers/{id}/mods/{modId}")]
        public IActionResult DeleteMod(string id, string modId)
        {
            var pluginsDir = Path.Combine(ServerPath.GetServersServerFiles(id), "BepInEx", "plugins");
            var paths = new[] {
                Path.Combine(pluginsDir, modId),
                Path.Combine(pluginsDir, modId + ".disabled")
            };

            bool deleted = false;
            foreach(var path in paths)
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                    deleted = true;
                }
                else if (System.IO.File.Exists(path))
                {
                    System.IO.File.Delete(path);
                    deleted = true;
                }
            }

            return deleted 
                ? Ok(new { Success = true, Message = "Mod deleted" }) 
                : BadRequest(new { Success = false, Message = "Mod not found" });
        }

        private static void CopyDirectory(string sourceDir, string destinationDir)
        {
            var dir = new DirectoryInfo(sourceDir);
            Directory.CreateDirectory(destinationDir);

            foreach (FileInfo file in dir.GetFiles())
            {
                string targetFilePath = Path.Combine(destinationDir, file.Name);
                file.CopyTo(targetFilePath, overwrite: true);
            }

            foreach (DirectoryInfo subDir in dir.GetDirectories())
            {
                string newDestinationDir = Path.Combine(destinationDir, subDir.Name);
                CopyDirectory(subDir.FullName, newDestinationDir);
            }
        }
    }
}
