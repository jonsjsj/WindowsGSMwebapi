using System.Collections.Generic;

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
        public string Map        { get; set; } = string.Empty;
        public string MaxPlayers { get; set; } = string.Empty;
        public int?    Pid                { get; set; }
        public int?    PlayersCurrent     { get; set; }
        public int?    PlayersMax         { get; set; }
        public double? CpuPercent         { get; set; }
        public double? RamMb              { get; set; }
        public bool?   GamePortReachable         { get; set; }
        public bool?   QueryPortReachable        { get; set; }
        public bool?   GamePortInternetReachable { get; set; }
    }

    public class ServerConfigDto
    {
        public string ServerId      { get; set; } = string.Empty;
        public string ServerGame    { get; set; } = string.Empty;
        public string ServerName    { get; set; } = string.Empty;
        public string ServerIp      { get; set; } = string.Empty;
        public string ServerPort    { get; set; } = string.Empty;
        public string QueryPort     { get; set; } = string.Empty;
        public string ServerMap     { get; set; } = string.Empty;
        public string MaxPlayers    { get; set; } = string.Empty;
        public string ServerParam   { get; set; } = string.Empty;
        public string ServerGslt    { get; set; } = string.Empty;
        public bool   AutoRestart   { get; set; }
        public bool   AutoStart     { get; set; }
        public bool   AutoUpdate    { get; set; }
        public bool   UpdateOnStart { get; set; }
        public bool   BackupOnStart { get; set; }
        public bool   RestartCrontab { get; set; }
        public string CrontabFormat  { get; set; } = string.Empty;
        public string CPUPriority    { get; set; } = string.Empty;
        public string CPUAffinity    { get; set; } = string.Empty;
        public bool   EmbedConsole   { get; set; }
        public bool   DiscordAlert   { get; set; }
        public string DiscordWebhook { get; set; } = string.Empty;
    }

    public class UpdateServerConfigRequest
    {
        public string? ServerName    { get; set; }
        public string? ServerIp      { get; set; }
        public string? ServerPort    { get; set; }
        public string? QueryPort     { get; set; }
        public string? ServerMap     { get; set; }
        public string? MaxPlayers    { get; set; }
        public string? ServerParam   { get; set; }
        public string? ServerGslt    { get; set; }
        public bool?   AutoRestart   { get; set; }
        public bool?   AutoStart     { get; set; }
        public bool?   AutoUpdate    { get; set; }
        public bool?   UpdateOnStart { get; set; }
        public bool?   BackupOnStart { get; set; }
        public bool?   RestartCrontab { get; set; }
        public string? CrontabFormat  { get; set; }
        public string? CPUPriority    { get; set; }
        public string? CPUAffinity    { get; set; }
        public bool?   EmbedConsole   { get; set; }
        public bool?   DiscordAlert   { get; set; }
        public string? DiscordWebhook { get; set; }
    }

    public class SendCommandRequest
    {
        public string Command { get; set; } = string.Empty;
        public int    WaitMs  { get; set; } = 0;
    }

    public class RestoreBackupRequest
    {
        public string FileName { get; set; } = string.Empty;
    }

    public class PluginError
    {
        public string FileName { get; set; } = string.Empty;
        public string Error    { get; set; } = string.Empty;
    }

    public class PluginReloadResult
    {
        public List<string>      Loaded  { get; set; } = new();
        public List<PluginError> Failed  { get; set; } = new();
    }

    public class ResourcesSummaryDto
    {
        public int    TotalServers      { get; set; }
        public int    OnlineServers     { get; set; }
        public double TotalCpuPercent   { get; set; }
        public double TotalRamMb        { get; set; }
        public double SystemTotalRamMb  { get; set; }
        public double DiskTotalGb       { get; set; }
        public double DiskUsedGb        { get; set; }
        public double DiskFreeGb        { get; set; }
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

    public class PortReachabilityDto
    {
        public int    Port      { get; set; }
        public string Protocol  { get; set; } = string.Empty;
        public bool   Reachable { get; set; }
        public string PublicIp  { get; set; } = string.Empty;
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
