using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using WindowsGSM.WebApi;

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

        // Use the real exe path (Process.MainModule), not Assembly.Location.
        // For PublishSingleFile builds Assembly.GetExecutingAssembly().Location
        // returns "" (empty) because the assembly is extracted to a temp dir,
        // so FileVersionInfo would read nothing and always return "0.0.0.0".
        public static string CurrentVersion =>
            FileVersionInfo.GetVersionInfo(
                Process.GetCurrentProcess().MainModule!.FileName).FileVersion ?? "0.0.0.0";

        /// <summary>
        /// Checks GitHub for the latest release using the HTML redirect — no API key,
        /// no rate limit.  GET /releases/latest → 302 → /releases/tag/vX.Y.Z
        /// Download URL is constructed directly from the tag name.
        /// Returns (hasUpdate, latestTag, downloadUrl, errorMessage).
        /// </summary>
        public async Task<(bool hasUpdate, string latestTag, string? downloadUrl, string? error)>
            CheckForUpdateAsync()
        {
            try
            {
                // Use the web URL; GitHub redirects to the actual tag page with no rate limit.
                var url = $"https://github.com/{GitHubOwner}/{GitHubRepo}/releases/latest";
                using var req  = new HttpRequestMessage(HttpMethod.Get, url);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead)
                                           .ConfigureAwait(false);

                // Follow the redirect manually to extract the tag from the final URL
                var finalUrl = resp.RequestMessage?.RequestUri?.ToString()
                            ?? resp.Headers.Location?.ToString()
                            ?? string.Empty;

                // finalUrl ends with  /releases/tag/v1.0.36
                var tagStart = finalUrl.LastIndexOf("/tag/", StringComparison.Ordinal);
                if (tagStart < 0)
                    return (false, string.Empty, null, "Could not parse release tag from redirect URL");

                var tagName    = finalUrl.Substring(tagStart + 5); // strip "/tag/"
                var latestVer  = tagName.TrimStart('v');
                var downloadUrl = $"https://github.com/{GitHubOwner}/{GitHubRepo}/releases/download/{tagName}/{AssetName}";

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
            var exeDir    = WgsmPath.AppDir;
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
                Application.Current.Dispatcher.Invoke(() =>
                {
                    var servers = _serverManager.GetAllServers();
                    foreach (var s in servers)
                        if (s.Status == "Started" || s.Status == "Starting")
                            _serverManager.Stop(s.Id);
                });

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
