using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowsGSM.WebApi.Models
{
    public enum ConnectionScope { LocalOnly = 0, LAN = 1, External = 2 }

    public class ApiKey
    {
        public string Id    { get; set; } = Guid.NewGuid().ToString("N")[..8];
        public string Name  { get; set; } = "Default";
        public string Token { get; set; } = string.Empty;
    }

    public class WebApiConfig
    {
        private static readonly string ConfigPath = Path.Combine(
            System.AppDomain.CurrentDomain.BaseDirectory, "configs", "webapi.json");

        public string       InstanceName { get; set; } = "My WGSM Instance";
        public List<ApiKey> ApiKeys      { get; set; } = new List<ApiKey>();

        // Kept only for migrating old single-token configs — cleared on Save()
        [JsonPropertyName("apiToken")]
        public string? LegacyApiToken { get; set; }

        public int             Port         { get; set; } = 7876;
        public ConnectionScope Scope        { get; set; } = ConnectionScope.LocalOnly;
        public bool            HttpsEnabled { get; set; } = false;
        public string          CertPath     { get; set; } = string.Empty;
        public string          KeyPath      { get; set; } = string.Empty;
        public bool            AutoStart    { get; set; } = false;

        // Backup destinations — leave empty to skip that destination
        public string BackupLocalPath   { get; set; } = string.Empty;
        public string BackupOnedrivePath { get; set; } = string.Empty;
        public string BackupGdrivePath  { get; set; } = string.Empty;

        // Convenience accessor — maps to the first key token so existing code keeps working
        [JsonIgnore]
        public string ApiToken
        {
            get => ApiKeys.FirstOrDefault()?.Token ?? string.Empty;
            set
            {
                if (ApiKeys.Count == 0)
                    ApiKeys.Add(new ApiKey { Name = "Default", Token = value });
                else
                    ApiKeys[0].Token = value;
            }
        }

        public static WebApiConfig Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var cfg  = JsonSerializer.Deserialize<WebApiConfig>(File.ReadAllText(ConfigPath), opts)
                               ?? new WebApiConfig();

                    // Migrate old single-token format
                    if (!string.IsNullOrEmpty(cfg.LegacyApiToken) && cfg.ApiKeys.Count == 0)
                    {
                        cfg.ApiKeys.Add(new ApiKey { Name = "Default", Token = cfg.LegacyApiToken });
                        cfg.LegacyApiToken = null;
                    }
                    return cfg;
                }
            }
            catch { }
            return new WebApiConfig();
        }

        public void Save()
        {
            LegacyApiToken = null; // never persist the legacy field
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, opts));
        }
    }
}
