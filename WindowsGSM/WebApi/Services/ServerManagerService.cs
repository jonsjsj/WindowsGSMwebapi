using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using WindowsGSM.Functions;
using WindowsGSM.WebApi.Models;

namespace WindowsGSM.WebApi.Services
{
    /// <summary>
    /// Bridges the Web API into the existing WindowsGSM server list.
    /// Uses MainWindow._serverMetadata and ServerGrid to read server state,
    /// and dispatches Start/Stop/Restart onto the WPF UI thread via the
    /// MainWindow.Dispatcher so all existing locks and status tracking work.
    /// </summary>
    public class ServerManagerService
    {
        private readonly MainWindow _mainWindow;

        public ServerManagerService(MainWindow mainWindow)
        {
            _mainWindow = mainWindow;
        }

        public List<ServerDto> GetAllServers()
        {
            var result = new List<ServerDto>();

            _mainWindow.Dispatcher.Invoke(() =>
            {
                foreach (ServerTable server in _mainWindow.ServerGrid.Items)
                {
                    var meta = _mainWindow.GetServerMetadata(server.ID);
                    result.Add(new ServerDto
                    {
                        Id         = server.ID,
                        Name       = server.Name ?? server.ID,
                        Game       = server.Game ?? "Unknown",
                        Status     = meta?.ServerStatus.ToString() ?? server.Status ?? "Unknown",
                        ServerIp   = server.IP ?? "",
                        ServerPort = server.Port ?? "",
                        QueryPort  = server.QueryPort ?? "",
                        Map        = server.Defaultmap ?? "",
                        MaxPlayers = server.Maxplayers ?? "",
                        Pid        = (meta?.Process != null && !meta.Process.HasExited)
                                     ? meta.Process.Id : (int?)null
                    });
                }
            });

            return result;
        }

        /// <summary>Returns the last <paramref name="count"/> console log lines for a server.</summary>
        public List<string> GetServerLogs(string serverId, int count = 200)
        {
            var logs = new List<string>();
            try
            {
                _mainWindow.Dispatcher.Invoke(() =>
                {
                    var meta = _mainWindow.GetServerMetadata(serverId);
                    if (meta == null) return;

                    // Access ServerConsole via reflection — internal list on the metadata object
                    var prop = meta.GetType().GetProperty("ServerConsole");
                    if (prop?.GetValue(meta) is not System.Collections.IList console) return;

                    int start = Math.Max(0, console.Count - count);
                    for (int i = start; i < console.Count; i++)
                        if (console[i]?.ToString() is string line)
                            logs.Add(line);
                });
            }
            catch { /* server may not have console output yet */ }
            return logs;
        }

        public (bool success, string message) Start(string serverId)
        {
            if (_mainWindow.GetServerMetadata(serverId) == null)
                return (false, $"Server '{serverId}' not found.");

            var meta = _mainWindow.GetServerMetadata(serverId);
            if (meta?.ServerStatus == MainWindow.ServerStatus.Started ||
                meta?.ServerStatus == MainWindow.ServerStatus.Starting)
                return (false, "Server is already running or starting.");

            _ = _mainWindow.Dispatcher.InvokeAsync(async () =>
                await _mainWindow.GameServer_StartById(serverId));
            return (true, "Start command sent.");
        }

        public (bool success, string message) Stop(string serverId)
        {
            if (_mainWindow.GetServerMetadata(serverId) == null)
                return (false, $"Server '{serverId}' not found.");

            var meta = _mainWindow.GetServerMetadata(serverId);
            if (meta?.ServerStatus == MainWindow.ServerStatus.Stopped ||
                meta?.ServerStatus == MainWindow.ServerStatus.Stopping)
                return (false, "Server is already stopped or stopping.");

            _ = _mainWindow.Dispatcher.InvokeAsync(async () =>
                await _mainWindow.GameServer_StopById(serverId));
            return (true, "Stop command sent.");
        }

        public (bool success, string message) Restart(string serverId)
        {
            if (_mainWindow.GetServerMetadata(serverId) == null)
                return (false, $"Server '{serverId}' not found.");

            _ = _mainWindow.Dispatcher.InvokeAsync(async () =>
                await _mainWindow.GameServer_RestartById(serverId));
            return (true, "Restart command sent.");
        }

        // ── Send console command ──────────────────────────────────────────────

        public async Task<(bool success, string message)> SendCommandAsync(string serverId, string command, int waitMs = 0)
        {
            ServerTable? server = null;
            _mainWindow.Dispatcher.Invoke(() =>
            {
                foreach (ServerTable s in _mainWindow.ServerGrid.Items)
                    if (s.ID == serverId) { server = s; break; }
            });
            if (server == null) return (false, $"Server '{serverId}' not found.");

            var meta = _mainWindow.GetServerMetadata(serverId);
            if (meta?.ServerStatus != MainWindow.ServerStatus.Started)
                return (false, "Server is not running.");

            try
            {
                var result = await _mainWindow.Dispatcher.InvokeAsync(async () =>
                    await _mainWindow.SendCommandAsync(server, command, waitMs)).Task.Unwrap();
                return (true, result ?? "Sent.");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        // ── Update game server ────────────────────────────────────────────────

        public (bool success, string message) Update(string serverId)
        {
            if (_mainWindow.GetServerMetadata(serverId) == null)
                return (false, $"Server '{serverId}' not found.");

            var meta = _mainWindow.GetServerMetadata(serverId);
            if (meta?.ServerStatus != MainWindow.ServerStatus.Stopped)
                return (false, "Server must be stopped before updating.");

            _ = _mainWindow.Dispatcher.InvokeAsync(async () =>
                await _mainWindow.GameServer_UpdateById(serverId));
            return (true, "Update started.");
        }

        // ── Per-server backup ─────────────────────────────────────────────────

        public (bool success, string message) Backup(string serverId)
        {
            if (_mainWindow.GetServerMetadata(serverId) == null)
                return (false, $"Server '{serverId}' not found.");

            var meta = _mainWindow.GetServerMetadata(serverId);
            if (meta?.ServerStatus != MainWindow.ServerStatus.Stopped)
                return (false, "Server must be stopped before backup.");

            _ = _mainWindow.Dispatcher.InvokeAsync(async () =>
                await _mainWindow.GameServer_BackupById(serverId));
            return (true, "Backup started.");
        }

        public List<object> ListBackupsForServer(string serverId)
        {
            try
            {
                var dir = ServerPath.GetBackups(serverId);
                if (!Directory.Exists(dir)) return new List<object>();
                return Directory.GetFiles(dir, "*.zip")
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.LastWriteTime)
                    .Select(f => (object)new { name = f.Name, sizeMb = Math.Round(f.Length / 1_048_576.0, 2), created = f.LastWriteTime })
                    .ToList();
            }
            catch { return new List<object>(); }
        }

        public (bool success, string message) RestoreBackup(string serverId, string backupFile)
        {
            if (_mainWindow.GetServerMetadata(serverId) == null)
                return (false, $"Server '{serverId}' not found.");

            var meta = _mainWindow.GetServerMetadata(serverId);
            if (meta?.ServerStatus != MainWindow.ServerStatus.Stopped)
                return (false, "Server must be stopped before restoring.");

            _ = _mainWindow.Dispatcher.InvokeAsync(async () =>
                await _mainWindow.GameServer_RestoreBackupById(serverId, backupFile));
            return (true, "Restore started.");
        }

        // ── Server config ─────────────────────────────────────────────────────

        public ServerConfigDto? GetConfig(string serverId)
        {
            try
            {
                var c = new ServerConfig(serverId);
                return new ServerConfigDto
                {
                    ServerId      = serverId,
                    ServerGame    = c.ServerGame    ?? "",
                    ServerName    = c.ServerName    ?? "",
                    ServerIp      = c.ServerIP      ?? "",
                    ServerPort    = c.ServerPort    ?? "",
                    QueryPort     = c.ServerQueryPort ?? "",
                    ServerMap     = c.ServerMap     ?? "",
                    MaxPlayers    = c.ServerMaxPlayer ?? "",
                    ServerParam   = c.ServerParam   ?? "",
                    ServerGslt    = c.ServerGSLT    ?? "",
                    AutoRestart   = c.AutoRestart,
                    AutoStart     = c.AutoStart,
                    AutoUpdate    = c.AutoUpdate,
                    UpdateOnStart = c.UpdateOnStart,
                    BackupOnStart = c.BackupOnStart,
                    RestartCrontab = c.RestartCrontab,
                    CrontabFormat = c.CrontabFormat ?? "",
                    CPUPriority   = c.CPUPriority   ?? "",
                    CPUAffinity   = c.CPUAffinity   ?? "",
                    EmbedConsole  = c.EmbedConsole,
                    DiscordAlert   = c.DiscordAlert,
                    DiscordWebhook = c.DiscordWebhook ?? "",
                };
            }
            catch { return null; }
        }

        // ── Plugin hot-reload ─────────────────────────────────────────────────

        /// <summary>
        /// Recompiles and reloads all plugins on the WPF UI thread.
        /// Returns a summary of what loaded successfully.
        /// </summary>
        public async Task<PluginReloadResult> ReloadPluginsAsync()
        {
            await _mainWindow.Dispatcher.InvokeAsync(async () =>
                await _mainWindow.LoadPlugins()).Task.Unwrap();

            var loaded  = _mainWindow.PluginsList.Where(p => p.IsLoaded).Select(p => p.FileName).ToList();
            var failed  = _mainWindow.PluginsList.Where(p => !p.IsLoaded)
                            .Select(p => new PluginError { FileName = p.FileName, Error = p.Error ?? "Unknown error" })
                            .ToList();
            return new PluginReloadResult { Loaded = loaded, Failed = failed };
        }

        public (bool success, string message) SaveConfig(string serverId, UpdateServerConfigRequest req)
        {
            try
            {
                void Set(string key, string? val) { if (val != null) ServerConfig.SetSetting(serverId, key, val); }
                void SetBool(string key, bool? val) { if (val.HasValue) ServerConfig.SetSetting(serverId, key, val.Value ? "1" : "0"); }

                Set(ServerConfig.SettingName.ServerName,      req.ServerName);
                Set(ServerConfig.SettingName.ServerIP,        req.ServerIp);
                Set(ServerConfig.SettingName.ServerPort,      req.ServerPort);
                Set(ServerConfig.SettingName.ServerQueryPort, req.QueryPort);
                Set(ServerConfig.SettingName.ServerMap,       req.ServerMap);
                Set(ServerConfig.SettingName.ServerMaxPlayer, req.MaxPlayers);
                Set(ServerConfig.SettingName.ServerParam,     req.ServerParam);
                Set(ServerConfig.SettingName.ServerGSLT,      req.ServerGslt);
                Set(ServerConfig.SettingName.CrontabFormat,   req.CrontabFormat);
                Set(ServerConfig.SettingName.CPUPriority,     req.CPUPriority);
                Set(ServerConfig.SettingName.CPUAffinity,     req.CPUAffinity);
                Set(ServerConfig.SettingName.DiscordWebhook,  req.DiscordWebhook);
                SetBool(ServerConfig.SettingName.AutoRestart,   req.AutoRestart);
                SetBool(ServerConfig.SettingName.AutoStart,     req.AutoStart);
                SetBool(ServerConfig.SettingName.AutoUpdate,    req.AutoUpdate);
                SetBool(ServerConfig.SettingName.UpdateOnStart, req.UpdateOnStart);
                SetBool(ServerConfig.SettingName.BackupOnStart, req.BackupOnStart);
                SetBool(ServerConfig.SettingName.RestartCrontab,req.RestartCrontab);
                SetBool(ServerConfig.SettingName.DiscordAlert,  req.DiscordAlert);
                SetBool(ServerConfig.SettingName.EmbedConsole,  req.EmbedConsole);

                return (true, "Config saved.");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }
    }
}
