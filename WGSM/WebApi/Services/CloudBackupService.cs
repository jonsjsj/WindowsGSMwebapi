using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WGSM.WebApi;
using WGSM.WebApi.Models;

namespace WGSM.WebApi.Services
{
    /// <summary>
    /// Handles cloud backup jobs: creates a per-server ZIP archive and uploads it to
    /// Google Drive (resumable upload API) or OneDrive (Microsoft Graph upload session).
    /// No extra NuGet packages required — everything uses HttpClient and System.Text.Json.
    ///
    /// OAuth tokens are read from WebApiConfig and refreshed automatically.
    /// </summary>
    public class CloudBackupService
    {
        private static readonly HttpClient _http = new();
        private readonly WebApiConfig _config;

        /// <summary>Active and recently-completed backup jobs, keyed by jobId.</summary>
        public ConcurrentDictionary<string, CloudBackupJobDto> Jobs { get; } = new();

        public CloudBackupService(WebApiConfig config) => _config = config;

        // ── Job management ───────────────────────────────────────────────────

        /// <summary>
        /// Starts a background cloud backup job for the given server and provider.
        /// Returns the jobId immediately; poll GET /api/cloud-backup/jobs/{jobId} for progress.
        /// </summary>
        public string StartJob(string serverId, string provider)
        {
            var jobId = Guid.NewGuid().ToString("N")[..12];
            var job   = new CloudBackupJobDto
            {
                JobId    = jobId,
                ServerId = serverId,
                Provider = provider,
                Status   = "running",
                Progress = 0,
                Message  = "Preparing…"
            };
            Jobs[jobId] = job;

            _ = Task.Run(() => RunJobAsync(job));
            return jobId;
        }

        private async Task RunJobAsync(CloudBackupJobDto job)
        {
            string? tempZip = null;
            try
            {
                // Phase 1 (0–40%): Create ZIP from server directory
                var serverDir  = WgsmPath.Combine("servers", job.ServerId);
                var timestamp  = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                var fileName   = $"cloud-backup-{job.ServerId}-{timestamp}.zip";
                tempZip        = Path.Combine(Path.GetTempPath(), fileName);

                job.Message  = "Creating ZIP archive…";
                job.Progress = 5;

                await Task.Run(() => CreateServerZip(serverDir, tempZip)).ConfigureAwait(false);
                job.Progress = 40;

                // Phase 2 (40–100%): Upload to cloud provider
                var providerName = job.Provider == "gdrive" ? "Google Drive" : "OneDrive";
                job.Message = $"Uploading to {providerName}…";

                void OnProgress(int pct) => job.Progress = 40 + pct * 60 / 100;

                if (job.Provider == "gdrive")
                    job.CloudFileId = await UploadToGoogleDriveAsync(tempZip, fileName, OnProgress).ConfigureAwait(false);
                else
                    job.CloudFileId = await UploadToOneDriveAsync(tempZip, fileName, OnProgress).ConfigureAwait(false);

                job.Status   = "done";
                job.Progress = 100;
                job.Message  = $"Uploaded to {providerName} successfully.";
            }
            catch (Exception ex)
            {
                job.Status  = "failed";
                job.Message = ex.Message;
            }
            finally
            {
                if (tempZip != null && File.Exists(tempZip))
                    try { File.Delete(tempZip); } catch { /* best-effort */ }
            }
        }

        private static void CreateServerZip(string serverDir, string destZip)
        {
            using var zip = ZipFile.Open(destZip, ZipArchiveMode.Create);
            AddDirToZip(zip, Path.Combine(serverDir, "serverfiles"), "serverfiles");
            AddDirToZip(zip, Path.Combine(serverDir, "configs"),     "configs");
        }

        private static void AddDirToZip(ZipArchive zip, string sourceDir, string prefix)
        {
            if (!Directory.Exists(sourceDir)) return;
            foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var rel   = Path.GetRelativePath(sourceDir, file);
                var entry = Path.Combine(prefix, rel).Replace('\\', '/');
                zip.CreateEntryFromFile(file, entry);
            }
        }

        // ── Google Drive (resumable upload API) ──────────────────────────────

        /// <summary>Exchanges the stored refresh token for a short-lived access token.</summary>
        public async Task<string> GetGoogleAccessTokenAsync()
        {
            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"]     = _config.GoogleClientId     ?? throw new InvalidOperationException("Google Client ID not configured."),
                ["client_secret"] = _config.GoogleClientSecret ?? throw new InvalidOperationException("Google Client Secret not configured."),
                ["refresh_token"] = _config.GoogleRefreshToken ?? throw new InvalidOperationException("Google Drive not linked — complete OAuth first."),
                ["grant_type"]    = "refresh_token"
            });

            var resp = await _http.PostAsync("https://oauth2.googleapis.com/token", form).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("access_token").GetString()!;
        }

        /// <summary>
        /// Uploads a file to Google Drive using the resumable upload API (handles arbitrarily
        /// large files via 10 MB chunks).  Returns the Drive file ID of the uploaded file.
        /// </summary>
        public async Task<string> UploadToGoogleDriveAsync(
            string filePath, string fileName,
            Action<int>? onProgress      = null,
            CancellationToken ct         = default)
        {
            var accessToken = await GetGoogleAccessTokenAsync().ConfigureAwait(false);
            var fileInfo    = new FileInfo(filePath);
            long totalBytes = fileInfo.Length;

            // Build JSON metadata; optionally pin to a folder
            var meta = _config.GoogleDriveFolderId is { Length: > 0 } fid
                ? $"{{\"name\":\"{JsonEscape(fileName)}\",\"parents\":[\"{fid}\"]}}"
                : $"{{\"name\":\"{JsonEscape(fileName)}\"}}";

            // 1. Initiate resumable upload session → get upload URI
            using var initReq = new HttpRequestMessage(HttpMethod.Post,
                "https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable");
            initReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            initReq.Headers.TryAddWithoutValidation("X-Upload-Content-Type",   "application/zip");
            initReq.Headers.TryAddWithoutValidation("X-Upload-Content-Length", totalBytes.ToString());
            initReq.Content = new StringContent(meta, Encoding.UTF8, "application/json");

            var initResp = await _http.SendAsync(initReq, ct).ConfigureAwait(false);
            initResp.EnsureSuccessStatusCode();
            var uploadUri = initResp.Headers.Location!;

            // 2. Upload in 10 MB chunks
            const int chunkSize = 10 * 1024 * 1024;
            var buffer  = new byte[chunkSize];
            long offset = 0;
            string? fileId = null;

            using var fs = File.OpenRead(filePath);
            while (offset < totalBytes)
            {
                ct.ThrowIfCancellationRequested();
                int read = await fs.ReadAsync(buffer, 0, chunkSize, ct).ConfigureAwait(false);
                if (read == 0) break;

                long end = offset + read - 1;
                using var chunkReq    = new HttpRequestMessage(HttpMethod.Put, uploadUri);
                var       chunkBody   = new ByteArrayContent(buffer, 0, read);
                chunkBody.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
                chunkBody.Headers.TryAddWithoutValidation("Content-Range",
                    $"bytes {offset}-{end}/{totalBytes}");
                chunkReq.Content = chunkBody;

                var chunkResp = await _http.SendAsync(chunkReq, ct).ConfigureAwait(false);

                // 200/201 = upload complete; 308 = chunk accepted, continue
                if (chunkResp.StatusCode == System.Net.HttpStatusCode.OK ||
                    chunkResp.StatusCode == System.Net.HttpStatusCode.Created)
                {
                    var respJson = await chunkResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(respJson);
                    fileId = doc.RootElement.GetProperty("id").GetString();
                }
                else if ((int)chunkResp.StatusCode != 308)
                {
                    var err = await chunkResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    throw new Exception($"Google Drive chunk upload failed at byte {offset}: HTTP {(int)chunkResp.StatusCode} — {err}");
                }

                offset += read;
                onProgress?.Invoke((int)(offset * 100 / totalBytes));
            }

            return fileId ?? throw new Exception("Google Drive upload completed but no file ID was returned.");
        }

        // ── OneDrive (Microsoft Graph upload session) ────────────────────────

        /// <summary>Exchanges the stored OneDrive refresh token for a short-lived access token.</summary>
        public async Task<string> GetOneDriveAccessTokenAsync()
        {
            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"]     = _config.OneDriveClientId     ?? throw new InvalidOperationException("OneDrive Client ID not configured."),
                ["client_secret"] = _config.OneDriveClientSecret ?? throw new InvalidOperationException("OneDrive Client Secret not configured."),
                ["refresh_token"] = _config.OneDriveRefreshToken ?? throw new InvalidOperationException("OneDrive not linked — complete OAuth first."),
                ["grant_type"]    = "refresh_token",
                ["scope"]         = "Files.ReadWrite offline_access"
            });

            var resp = await _http.PostAsync(
                "https://login.microsoftonline.com/common/oauth2/v2.0/token", form).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("access_token").GetString()!;
        }

        /// <summary>
        /// Uploads a file to OneDrive using the Microsoft Graph large-file upload session
        /// (10 MB chunks, conflict behaviour = rename).  Returns the OneDrive item ID.
        /// </summary>
        public async Task<string> UploadToOneDriveAsync(
            string filePath, string fileName,
            Action<int>? onProgress = null,
            CancellationToken ct    = default)
        {
            var accessToken = await GetOneDriveAccessTokenAsync().ConfigureAwait(false);
            var fileInfo    = new FileInfo(filePath);
            long totalBytes = fileInfo.Length;

            var folder   = string.IsNullOrWhiteSpace(_config.OneDriveFolderPath)
                           ? "WGSM Backups"
                           : _config.OneDriveFolderPath.Trim('/');
            var itemPath = $"{folder}/{fileName}";

            // 1. Create upload session
            using var sessionReq = new HttpRequestMessage(HttpMethod.Post,
                $"https://graph.microsoft.com/v1.0/me/drive/root:/{Uri.EscapeDataString(itemPath)}:/createUploadSession");
            sessionReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            sessionReq.Content = new StringContent(
                "{\"item\":{\"@microsoft.graph.conflictBehavior\":\"rename\"}}",
                Encoding.UTF8, "application/json");

            var sessionResp = await _http.SendAsync(sessionReq, ct).ConfigureAwait(false);
            sessionResp.EnsureSuccessStatusCode();
            var sessionJson = await sessionResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var sessionDoc = JsonDocument.Parse(sessionJson);
            var uploadUrl = sessionDoc.RootElement.GetProperty("uploadUrl").GetString()!;

            // 2. Upload in 10 MB chunks (Microsoft max: 60 MB per chunk)
            const int chunkSize = 10 * 1024 * 1024;
            var buffer  = new byte[chunkSize];
            long offset = 0;
            string? itemId = null;

            using var fs = File.OpenRead(filePath);
            while (offset < totalBytes)
            {
                ct.ThrowIfCancellationRequested();
                int read = await fs.ReadAsync(buffer, 0, chunkSize, ct).ConfigureAwait(false);
                if (read == 0) break;

                long end = offset + read - 1;
                using var chunkReq  = new HttpRequestMessage(HttpMethod.Put, uploadUrl);
                var       chunkBody = new ByteArrayContent(buffer, 0, read);
                chunkBody.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                chunkBody.Headers.TryAddWithoutValidation("Content-Range",
                    $"bytes {offset}-{end}/{totalBytes}");
                chunkReq.Content = chunkBody;

                var chunkResp = await _http.SendAsync(chunkReq, ct).ConfigureAwait(false);

                // 200/201 = complete; 202 = accepted, continue
                if (chunkResp.StatusCode == System.Net.HttpStatusCode.OK ||
                    chunkResp.StatusCode == System.Net.HttpStatusCode.Created)
                {
                    var respJson = await chunkResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(respJson);
                    itemId = doc.RootElement.GetProperty("id").GetString();
                }
                else if (chunkResp.StatusCode != System.Net.HttpStatusCode.Accepted)
                {
                    var err = await chunkResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    throw new Exception($"OneDrive chunk upload failed at byte {offset}: HTTP {(int)chunkResp.StatusCode} — {err}");
                }

                offset += read;
                onProgress?.Invoke((int)(offset * 100 / totalBytes));
            }

            return itemId ?? throw new Exception("OneDrive upload completed but no item ID was returned.");
        }

        // ── helpers ──────────────────────────────────────────────────────────

        private static string JsonEscape(string s) =>
            s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
