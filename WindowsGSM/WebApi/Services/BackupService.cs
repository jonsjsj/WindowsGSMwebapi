using System;
using System.IO;
using System.IO.Compression;
using WindowsGSM.WebApi.Models;

namespace WindowsGSM.WebApi.Services
{
    /// <summary>
    /// Creates ZIP backups of WindowsGSM config/server files and copies them
    /// to a local path, an OneDrive sync folder, and/or a Google Drive sync folder.
    /// </summary>
    public class BackupService
    {
        private readonly WebApiConfig _config;
        private static readonly string BaseDir = AppDomain.CurrentDomain.BaseDirectory;

        public BackupService(WebApiConfig config)
        {
            _config = config;
        }

        /// <summary>
        /// Creates a timestamped ZIP of configs/ and servers/ and copies it to
        /// every configured destination. Returns the path of the created ZIP.
        /// </summary>
        public (bool success, string message, string? zipPath) CreateBackup()
        {
            try
            {
                var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                var fileName  = $"wgsm-backup-{timestamp}.zip";
                var tempZip   = Path.Combine(Path.GetTempPath(), fileName);

                using (var zip = ZipFile.Open(tempZip, ZipArchiveMode.Create))
                {
                    AddDirectoryToZip(zip, Path.Combine(BaseDir, "configs"), "configs");
                    AddDirectoryToZip(zip, Path.Combine(BaseDir, "servers"), "servers");
                }

                var destinations = new[]
                {
                    _config.BackupLocalPath,
                    _config.BackupOnedrivePath,
                    _config.BackupGdrivePath,
                };

                int copied = 0;
                foreach (var dest in destinations)
                {
                    if (string.IsNullOrWhiteSpace(dest)) continue;
                    try
                    {
                        Directory.CreateDirectory(dest);
                        File.Copy(tempZip, Path.Combine(dest, fileName), overwrite: true);
                        copied++;
                    }
                    catch { /* non-fatal: log individually if needed */ }
                }

                // If no destinations configured, keep the zip in BaseDir
                string finalPath = tempZip;
                if (copied == 0)
                {
                    finalPath = Path.Combine(BaseDir, "backups", fileName);
                    Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
                    File.Move(tempZip, finalPath, overwrite: true);
                }
                else
                {
                    File.Delete(tempZip);
                }

                return (true,
                    $"Backup created: {fileName} — copied to {copied} destination(s).",
                    finalPath);
            }
            catch (Exception ex)
            {
                return (false, $"Backup failed: {ex.Message}", null);
            }
        }

        /// <summary>Lists recent backup ZIPs stored in the local backups/ folder.</summary>
        public string[] ListLocalBackups()
        {
            var dir = Path.Combine(BaseDir, "backups");
            if (!Directory.Exists(dir)) return Array.Empty<string>();
            return Directory.GetFiles(dir, "wgsm-backup-*.zip");
        }

        // ── helpers ─────────────────────────────────────────────────────────

        private static void AddDirectoryToZip(ZipArchive zip, string sourceDir, string entryPrefix)
        {
            if (!Directory.Exists(sourceDir)) return;

            foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceDir, file);
                zip.CreateEntryFromFile(file, Path.Combine(entryPrefix, relative).Replace('\\', '/'));
            }
        }
    }
}
