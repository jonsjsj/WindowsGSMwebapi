using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using WindowsGSM.WebApi.Models;

namespace WindowsGSM.WebApi.Services
{
    /// <summary>
    /// Checks GitHub Releases for a newer version of WindowsGSM+WebAPI
    /// and performs a seamless self-patch: stops all servers, downloads the
    /// new exe, writes a swap batch script, and restarts the application.
    /// </summary>
    public class UpdateService
    {
        private const string GitHubOwner = "jonsjsj";
        private const string GitHubRepo  = "WindowsGSMwebapi";
        private const string AssetName   = "WindowsGSM.exe";

        private readonly ServerManagerService _serverManager;

        // Lazily-created HttpClient (one per service lifetime)
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        static UpdateService()
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd($"WindowsGSM-WebAPI/{CurrentVersion}");
        }

        public UpdateService(ServerManagerService serverManager)
        {
            _serverManager = serverManager;
        }

        // ── Public API ───────────────────────────────────────────────────────

        public static string CurrentVersion =>
            FileVersionInfo.GetVersionInfo(
                Assembly.GetExecutingAssembly().Location).FileVersion ?? "0.0.0.0";

        /// <summary>
        /// Checks GitHub for the latest release.
        /// Returns (hasUpdate, latestTag, downloadUrl, errorMessage).
        /// </summary>
        public async Task<(bool hasUpdate, string latestTag, string? downloadUrl, string? error)>
            CheckForUpdateAsync()
        {
            try
            {
                var url     = $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Accept.ParseAdd("application/vnd.github+json");

                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    return (false, string.Empty, null, $"GitHub returned {(int)resp.StatusCode}");

                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);

                var root      = doc.RootElement;
                var tagName   = root.GetProperty("tag_name").GetString() ?? "";
                var latestVer = tagName.TrimStart('v');

                // Find the raw exe asset
                string? downloadUrl = null;
                if (root.TryGetProperty("assets", out var assets))
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        if (asset.TryGetProperty("name", out var nameProp) &&
                            nameProp.GetString() == AssetName &&
                            asset.TryGetProperty("browser_download_url", out var urlProp))
                        {
                            downloadUrl = urlProp.GetString();
                            break;
                        }
                    }
                }

                bool hasUpdate = IsNewer(latestVer, CurrentVersion);
                return (hasUpdate, tagName, downloadUrl, null);
            }
            catch (Exception ex)
            {
                return (false, string.Empty, null, ex.Message);
            }
        }

        /// <summary>
        /// Downloads the new exe, stops all servers, and swaps the binary via
        /// a helper batch script — then exits the current process.
        /// </summary>
        public async Task<(bool success, string message)> ApplyUpdateAsync(string downloadUrl)
        {
            var exeDir    = AppDomain.CurrentDomain.BaseDirectory;
            var exePath   = Path.Combine(exeDir, "WindowsGSM.exe");
            var updateExe = Path.Combine(exeDir, "WindowsGSM.update.exe");
            var batchPath = Path.Combine(exeDir, "wgsm_update.bat");

            try
            {
                // 1. Download new exe
                using var resp = await _http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead)
                                           .ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();

                await using (var fs = File.Create(updateExe))
                    await resp.Content.CopyToAsync(fs).ConfigureAwait(false);

                // 2. Stop all running servers via the UI dispatcher
                await Application.Current.Dispatcher.InvokeAsync(async () =>
                {
                    var servers = _serverManager.GetAllServers();
                    foreach (var s in servers)
                        if (s.Status == "Started" || s.Status == "Starting")
                            await _serverManager.StopAsync(s.Id).ConfigureAwait(false);
                }).Task.Unwrap().ConfigureAwait(false);

                // 3. Write swap batch
                File.WriteAllText(batchPath,
                    "@echo off\r\n" +
                    "timeout /t 3 /nobreak >nul\r\n" +
                    $"copy /y \"{updateExe}\" \"{exePath}\"\r\n" +
                    $"del \"{updateExe}\"\r\n" +
                    $"start \"\" \"{exePath}\"\r\n" +
                    $"del \"%~f0\"\r\n");

                // 4. Launch batch and exit
                Process.Start(new ProcessStartInfo
                {
                    FileName        = "cmd.exe",
                    Arguments       = $"/c \"{batchPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow  = true,
                });

                Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
                return (true, "Update started — application will restart automatically.");
            }
            catch (Exception ex)
            {
                return (false, $"Update failed: {ex.Message}");
            }
        }

        // ── helpers ─────────────────────────────────────────────────────────

        /// <summary>Returns true if <paramref name="latest"/> is strictly newer than <paramref name="current"/>.</summary>
        private static bool IsNewer(string latest, string current)
        {
            if (!Version.TryParse(PadVersion(latest),  out var lv)) return false;
            if (!Version.TryParse(PadVersion(current), out var cv)) return false;
            return lv > cv;
        }

        private static string PadVersion(string v)
        {
            // Ensure at least 2 components so Version.TryParse works
            var parts = v.Split('.');
            return parts.Length >= 2 ? v : v + ".0";
        }
    }
}
