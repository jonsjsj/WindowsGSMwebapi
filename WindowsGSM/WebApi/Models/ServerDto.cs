namespace WindowsGSM.WebApi.Models
{
    public class ServerDto
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Game { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;  // Started, Stopped, Starting, Stopping, Restarting
        public string ServerIp { get; set; } = string.Empty;
        public string ServerPort { get; set; } = string.Empty;
        public string QueryPort { get; set; } = string.Empty;
        public int? Pid { get; set; }
    }

    public class ApiActionResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public class TokenResponse
    {
        public string Token { get; set; } = string.Empty;
    }

    public class StatusResponse
    {
        public bool Running { get; set; }
        public string BindUrl { get; set; } = string.Empty;
        public string LocalUrl { get; set; } = string.Empty;
        public string LanUrl { get; set; } = string.Empty;
        public string PublicUrl { get; set; } = string.Empty;
        public ConnectionScope Scope { get; set; }
        public int Port { get; set; }
    }
}
