using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using WindowsGSM.WebApi.Models;

namespace WindowsGSM.WebApi.Controllers
{
    /// <summary>
    /// Handles the OAuth 2.0 authorization-code flow for Google Drive and OneDrive.
    ///
    /// Admin flow (one-time per provider):
    ///  1. POST /api/oauth/google/credentials  — save Client ID + Secret
    ///  2. GET  /api/oauth/google/start        — opens browser to Google consent screen
    ///  3. GET  /api/oauth/google/callback     — Google redirects here; stores refresh token
    ///
    /// The /start and /callback routes are public (no Bearer token) because they are
    /// navigated directly by a browser popup that cannot attach the API key header.
    /// All other routes (credentials, status, unlink) require a valid Bearer token.
    ///
    /// No extra NuGet packages — all token exchanges use HttpClient.
    /// </summary>
    [ApiController]
    [Route("api/oauth")]
    public class OAuthController : ControllerBase
    {
        // CSRF state store: state → (provider, expiry)
        private static readonly ConcurrentDictionary<string, (string provider, DateTime expiry)> _pending = new();

        private static readonly HttpClient _http = new();
        private readonly WebApiConfig _config;

        public OAuthController(WebApiConfig config) => _config = config;

        // ── Status ───────────────────────────────────────────────────────────

        // GET /api/oauth/status  (requires Bearer token)
        [HttpGet("status")]
        public IActionResult GetStatus() => Ok(new OAuthStatusDto
        {
            GoogleLinked   = !string.IsNullOrWhiteSpace(_config.GoogleRefreshToken),
            OneDriveLinked = !string.IsNullOrWhiteSpace(_config.OneDriveRefreshToken)
        });

        // ── Credentials (save Client ID + Secret) ────────────────────────────

        // POST /api/oauth/google/credentials  (requires Bearer token)
        [HttpPost("google/credentials")]
        public IActionResult SaveGoogleCredentials([FromBody] OAuthCredentialsRequest req)
        {
            if (string.IsNullOrWhiteSpace(req?.ClientId) || string.IsNullOrWhiteSpace(req.ClientSecret))
                return BadRequest(new ApiActionResult { Success = false, Message = "clientId and clientSecret are required." });

            _config.GoogleClientId     = req.ClientId.Trim();
            _config.GoogleClientSecret = req.ClientSecret.Trim();
            if (!string.IsNullOrWhiteSpace(req.FolderIdOrPath))
                _config.GoogleDriveFolderId = req.FolderIdOrPath.Trim();
            _config.Save();

            return Ok(new ApiActionResult
            {
                Success = true,
                Message = "Google credentials saved. Click 'Link Google Drive' to complete OAuth."
            });
        }

        // POST /api/oauth/onedrive/credentials  (requires Bearer token)
        [HttpPost("onedrive/credentials")]
        public IActionResult SaveOneDriveCredentials([FromBody] OAuthCredentialsRequest req)
        {
            if (string.IsNullOrWhiteSpace(req?.ClientId) || string.IsNullOrWhiteSpace(req.ClientSecret))
                return BadRequest(new ApiActionResult { Success = false, Message = "clientId and clientSecret are required." });

            _config.OneDriveClientId     = req.ClientId.Trim();
            _config.OneDriveClientSecret = req.ClientSecret.Trim();
            if (!string.IsNullOrWhiteSpace(req.FolderIdOrPath))
                _config.OneDriveFolderPath = req.FolderIdOrPath.Trim();
            _config.Save();

            return Ok(new ApiActionResult
            {
                Success = true,
                Message = "OneDrive credentials saved. Click 'Link OneDrive' to complete OAuth."
            });
        }

        // ── Unlink ───────────────────────────────────────────────────────────

        // DELETE /api/oauth/google/unlink  (requires Bearer token)
        [HttpDelete("google/unlink")]
        public IActionResult UnlinkGoogle()
        {
            _config.GoogleRefreshToken = null;
            _config.Save();
            return Ok(new ApiActionResult { Success = true, Message = "Google Drive unlinked." });
        }

        // DELETE /api/oauth/onedrive/unlink  (requires Bearer token)
        [HttpDelete("onedrive/unlink")]
        public IActionResult UnlinkOneDrive()
        {
            _config.OneDriveRefreshToken = null;
            _config.Save();
            return Ok(new ApiActionResult { Success = true, Message = "OneDrive unlinked." });
        }

        // ── Google OAuth flow (PUBLIC — no Bearer token) ─────────────────────

        // GET /api/oauth/google/start
        // Opens in a browser popup; redirects to Google's consent screen.
        [HttpGet("google/start")]
        public IActionResult GoogleStart()
        {
            if (string.IsNullOrWhiteSpace(_config.GoogleClientId))
                return Content(ErrorHtml("Google Client ID not configured. Save credentials first."), "text/html");

            var state = NewState("google");
            var redirectUri = BuildRedirectUri("google");

            var url = "https://accounts.google.com/o/oauth2/v2/auth" +
                $"?client_id={Uri.EscapeDataString(_config.GoogleClientId)}" +
                $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                $"&response_type=code" +
                $"&scope={Uri.EscapeDataString("https://www.googleapis.com/auth/drive.file")}" +
                $"&access_type=offline" +
                $"&prompt=consent" +
                $"&state={state}";

            return Redirect(url);
        }

        // GET /api/oauth/google/callback
        // Google redirects here after user consent. Exchanges code for tokens.
        [HttpGet("google/callback")]
        public async Task<IActionResult> GoogleCallback(
            [FromQuery] string? code, [FromQuery] string? state, [FromQuery] string? error)
        {
            if (!string.IsNullOrEmpty(error))
                return Content(ErrorHtml($"Google OAuth error: {error}"), "text/html");

            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state) || !ConsumeState(state, "google"))
                return Content(ErrorHtml("Invalid or expired OAuth state. Please try again."), "text/html");

            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["code"]          = code,
                ["client_id"]     = _config.GoogleClientId     ?? "",
                ["client_secret"] = _config.GoogleClientSecret ?? "",
                ["redirect_uri"]  = BuildRedirectUri("google"),
                ["grant_type"]    = "authorization_code"
            });

            var resp = await _http.PostAsync("https://oauth2.googleapis.com/token", form).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                return Content(ErrorHtml($"Token exchange failed ({(int)resp.StatusCode}): {json}"), "text/html");

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("refresh_token", out var rt) ||
                string.IsNullOrWhiteSpace(rt.GetString()))
                return Content(ErrorHtml("Google did not return a refresh token. " +
                    "Ensure the app is set to 'Web application' type and 'access_type=offline' was requested. " +
                    "If you previously authorized this app, revoke access at myaccount.google.com/permissions and try again."),
                    "text/html");

            _config.GoogleRefreshToken = rt.GetString();
            _config.Save();

            return Content(SuccessHtml("google", "Google Drive linked successfully!"), "text/html");
        }

        // ── OneDrive OAuth flow (PUBLIC — no Bearer token) ───────────────────

        // GET /api/oauth/onedrive/start
        [HttpGet("onedrive/start")]
        public IActionResult OneDriveStart()
        {
            if (string.IsNullOrWhiteSpace(_config.OneDriveClientId))
                return Content(ErrorHtml("OneDrive Client ID not configured. Save credentials first."), "text/html");

            var state = NewState("onedrive");
            var redirectUri = BuildRedirectUri("onedrive");

            var url = "https://login.microsoftonline.com/common/oauth2/v2.0/authorize" +
                $"?client_id={Uri.EscapeDataString(_config.OneDriveClientId)}" +
                $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                $"&response_type=code" +
                $"&scope={Uri.EscapeDataString("Files.ReadWrite offline_access")}" +
                $"&state={state}";

            return Redirect(url);
        }

        // GET /api/oauth/onedrive/callback
        [HttpGet("onedrive/callback")]
        public async Task<IActionResult> OneDriveCallback(
            [FromQuery] string? code, [FromQuery] string? state, [FromQuery] string? error)
        {
            if (!string.IsNullOrEmpty(error))
                return Content(ErrorHtml($"OneDrive OAuth error: {error}"), "text/html");

            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state) || !ConsumeState(state, "onedrive"))
                return Content(ErrorHtml("Invalid or expired OAuth state. Please try again."), "text/html");

            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["code"]          = code,
                ["client_id"]     = _config.OneDriveClientId     ?? "",
                ["client_secret"] = _config.OneDriveClientSecret ?? "",
                ["redirect_uri"]  = BuildRedirectUri("onedrive"),
                ["grant_type"]    = "authorization_code",
                ["scope"]         = "Files.ReadWrite offline_access"
            });

            var resp = await _http.PostAsync(
                "https://login.microsoftonline.com/common/oauth2/v2.0/token", form).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                return Content(ErrorHtml($"Token exchange failed ({(int)resp.StatusCode}): {json}"), "text/html");

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("refresh_token", out var rt) ||
                string.IsNullOrWhiteSpace(rt.GetString()))
                return Content(ErrorHtml("Microsoft did not return a refresh token. " +
                    "Ensure offline_access is in the scope and the app is configured as a confidential client."),
                    "text/html");

            _config.OneDriveRefreshToken = rt.GetString();
            _config.Save();

            return Content(SuccessHtml("onedrive", "OneDrive linked successfully!"), "text/html");
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private string BuildRedirectUri(string provider) =>
            $"{Request.Scheme}://{Request.Host}/api/oauth/{provider}/callback";

        private static string NewState(string provider)
        {
            // Purge expired states first
            var now = DateTime.UtcNow;
            foreach (var kv in _pending)
                if (kv.Value.expiry < now)
                    _pending.TryRemove(kv.Key, out _);

            var state = Convert.ToBase64String(
                System.Security.Cryptography.RandomNumberGenerator.GetBytes(18))
                .Replace("+", "-").Replace("/", "_").Replace("=", "");

            _pending[state] = (provider, now.AddMinutes(10));
            return state;
        }

        private static bool ConsumeState(string state, string expectedProvider)
        {
            if (!_pending.TryRemove(state, out var entry)) return false;
            if (DateTime.UtcNow > entry.expiry) return false;
            return entry.provider == expectedProvider;
        }

        private static string SuccessHtml(string provider, string message) => $@"<!DOCTYPE html>
<html lang=""en""><head><meta charset=""UTF-8""><title>Linked</title>
<style>body{{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;
background:#1a1a1a;color:#e0e0e0;display:flex;align-items:center;
justify-content:center;height:100vh;margin:0;text-align:center}}
.box{{background:#252525;border:1px solid #3a3a3a;border-radius:8px;padding:32px 40px}}
h2{{color:#4ec9b0;margin:0 0 12px}}p{{color:#888;font-size:13px;margin:0}}</style></head>
<body><div class=""box""><h2>✓ {message}</h2>
<p>This window will close automatically…</p>
<script>
if(window.opener){{
  window.opener.postMessage({{type:'oauth-complete',provider:'{provider}'}}, '*');
  setTimeout(()=>window.close(), 1200);
}}
</script></div></body></html>";

        private static string HtmlEnc(string s) =>
            s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

        private static string ErrorHtml(string message) => $@"<!DOCTYPE html>
<html lang=""en""><head><meta charset=""UTF-8""><title>OAuth Error</title>
<style>body{{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;
background:#1a1a1a;color:#e0e0e0;display:flex;align-items:center;
justify-content:center;height:100vh;margin:0;text-align:center}}
.box{{background:#252525;border:1px solid #3a3a3a;border-radius:8px;padding:32px 40px;max-width:480px}}
h2{{color:#f14c4c;margin:0 0 12px}}p{{color:#888;font-size:13px;margin:0}}</style></head>
<body><div class=""box""><h2>OAuth Error</h2>
<p>{HtmlEnc(message)}</p>
<p style=""margin-top:12px""><a href=""javascript:window.close()"" style=""color:#0078d4"">Close this window</a></p>
</div></body></html>";
    }
}
