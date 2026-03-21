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
        public int?    Pid                { get; set; }
        public int?    PlayersCurrent     { get; set; }
        public int?    PlayersMax         { get; set; }
        public double? CpuPercent         { get; set; }
        public double? RamMb              { get; set; }
        public bool?   GamePortReachable  { get; set; }
        public bool?   QueryPortReachable { get; set; }
    }

    public class ResourcesSummaryDto
    {
        public int    TotalServers    { get; set; }
        public int    OnlineServers   { get; set; }
        public double TotalCpuPercent { get; set; }
        public double TotalRamMb      { get; set; }
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

    public class FirewallStatusDto
    {
        public int    Port       { get; set; }
        public string Protocol   { get; set; } = string.Empty;
        public bool   RuleExists { get; set; }
        public bool   IsEnabled  { get; set; }
    }

    public class BackupResultDto
    {
        public bool    Success  { get; set; }
        public string  Message  { get; set; } = string.Empty;
        public string? FileName { get; set; }
    }

    public class UpdateCheckDto
    {
        public string  CurrentVersion { get; set; } = string.Empty;
        public string  LatestTag      { get; set; } = string.Empty;
        public bool    HasUpdate      { get; set; }
        public string? DownloadUrl    { get; set; }
    }

    public class ApplyUpdateRequest
    {
        public string? DownloadUrl { get; set; }
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
