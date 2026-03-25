using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using WindowsGSM.WebApi.Models;

namespace WindowsGSM.WebApi.Controllers
{
    /// <summary>
    /// Provides AES-256-CBC encrypted export and import of the Web API config file
    /// (tokens, API keys, port, scope, etc.) so that settings survive a machine rebuild.
    ///
    /// Wire format for the .enc blob: [16 B salt][16 B IV][PKCS7-padded ciphertext]
    /// Key derivation: PBKDF2-SHA256, 100 000 iterations, 32-byte key.
    /// </summary>
    [ApiController]
    [Route("api/config")]
    public class ConfigController : ControllerBase
    {
        private static readonly string ConfigPath =
            WgsmPath.Combine("configs", "webapi.json");

        private readonly WebApiConfig _config;

        public ConfigController(WebApiConfig config) => _config = config;

        // GET /api/config/export
        // Header: X-Config-Password: <your-password>
        // Returns the encrypted config as a downloadable .enc file.
        [HttpGet("export")]
        public IActionResult Export()
        {
            string? password = Request.Headers["X-Config-Password"];
            if (string.IsNullOrWhiteSpace(password))
                return BadRequest(new ApiActionResult
                {
                    Success = false,
                    Message = "X-Config-Password header is required."
                });

            if (!System.IO.File.Exists(ConfigPath))
                return NotFound(new ApiActionResult
                {
                    Success = false,
                    Message = "Config file not found. Has the Web API been configured yet?"
                });

            var plaintext = System.IO.File.ReadAllBytes(ConfigPath);
            var encrypted = EncryptAes(plaintext, password);
            var fileName  = $"wgsm-config-{DateTime.UtcNow:yyyyMMdd-HHmmss}.enc";

            return File(encrypted, "application/octet-stream", fileName);
        }

        // POST /api/config/import
        // Header: X-Config-Password: <password-used-during-export>
        // Body:   multipart/form-data, field "file" = .enc blob
        // Writes the decrypted config to disk. Restart the Web API to apply it.
        [HttpPost("import")]
        public async Task<IActionResult> Import(IFormFile file)
        {
            string? password = Request.Headers["X-Config-Password"];
            if (string.IsNullOrWhiteSpace(password))
                return BadRequest(new ApiActionResult
                {
                    Success = false,
                    Message = "X-Config-Password header is required."
                });

            if (file == null || file.Length == 0)
                return BadRequest(new ApiActionResult
                {
                    Success = false,
                    Message = "No file received. Upload the .enc backup file as form field 'file'."
                });

            byte[] encrypted;
            using (var ms = new MemoryStream())
            {
                await file.CopyToAsync(ms);
                encrypted = ms.ToArray();
            }

            byte[] plaintext;
            try
            {
                plaintext = DecryptAes(encrypted, password);
            }
            catch
            {
                return BadRequest(new ApiActionResult
                {
                    Success = false,
                    Message = "Decryption failed — wrong password or corrupted/invalid file."
                });
            }

            // Validate that the decrypted bytes are a parseable WebApiConfig
            try
            {
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var test = JsonSerializer.Deserialize<WebApiConfig>(plaintext, opts);
                if (test == null)
                    throw new InvalidOperationException("Deserialised to null.");
            }
            catch
            {
                return BadRequest(new ApiActionResult
                {
                    Success = false,
                    Message = "Decrypted content is not a valid WebApiConfig — decryption succeeded but the JSON is malformed."
                });
            }

            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            await System.IO.File.WriteAllBytesAsync(ConfigPath, plaintext);

            return Ok(new ApiActionResult
            {
                Success = true,
                Message = "Config imported successfully. Stop and restart the Web API in WGSM to apply the restored tokens and settings."
            });
        }

        // ── Crypto helpers ───────────────────────────────────────────────────

        // Format: [16 B random salt | 16 B random IV | PKCS7 ciphertext]
        private static byte[] EncryptAes(byte[] plaintext, string password)
        {
            var salt = RandomNumberGenerator.GetBytes(16);
            var iv   = RandomNumberGenerator.GetBytes(16);
            var key  = DeriveKey(password, salt);

            using var aes = Aes.Create();
            aes.Key     = key;
            aes.IV      = iv;
            aes.Mode    = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var enc = aes.CreateEncryptor();
            var ct = enc.TransformFinalBlock(plaintext, 0, plaintext.Length);

            var result = new byte[16 + 16 + ct.Length];
            Buffer.BlockCopy(salt, 0, result, 0,  16);
            Buffer.BlockCopy(iv,   0, result, 16, 16);
            Buffer.BlockCopy(ct,   0, result, 32, ct.Length);
            return result;
        }

        private static byte[] DecryptAes(byte[] data, string password)
        {
            if (data.Length < 33)
                throw new ArgumentException("Data too short to be a valid backup file.");

            var salt = data[..16];
            var iv   = data[16..32];
            var ct   = data[32..];
            var key  = DeriveKey(password, salt);

            using var aes = Aes.Create();
            aes.Key     = key;
            aes.IV      = iv;
            aes.Mode    = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var dec = aes.CreateDecryptor();
            return dec.TransformFinalBlock(ct, 0, ct.Length);
        }

        private static byte[] DeriveKey(string password, byte[] salt)
        {
            using var kdf = new Rfc2898DeriveBytes(
                password, salt, 100_000, HashAlgorithmName.SHA256);
            return kdf.GetBytes(32);
        }
    }
}
