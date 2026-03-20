using System.IO;
using System.Text.Json;

namespace WindowsGSM.WebApi.Models
{
    public enum ConnectionScope
    {
        LocalOnly = 0,
        LAN = 1,
        External = 2
    }

    public class WebApiConfig
    {
        private static readonly string ConfigPath = Path.Combine(
            System.AppDomain.CurrentDomain.BaseDirectory, "configs", "webapi.json");

        public string ApiToken { get; set; } = string.Empty;
        public int Port { get; set; } = 7876;
        public ConnectionScope Scope { get; set; } = ConnectionScope.LocalOnly;
        public bool HttpsEnabled { get; set; } = false;
        public string CertPath { get; set; } = string.Empty;
        public string KeyPath { get; set; } = string.Empty;
        public bool AutoStart { get; set; } = false;

        public static WebApiConfig Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var json = File.ReadAllText(ConfigPath);
                    return JsonSerializer.Deserialize<WebApiConfig>(json) ?? new WebApiConfig();
                }
            }
            catch { /* fall through to default */ }
            return new WebApiConfig();
        }

        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
        }
    }
}
