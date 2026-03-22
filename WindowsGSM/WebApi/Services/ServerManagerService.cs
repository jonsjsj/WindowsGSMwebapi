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

        public async Task<(bool success, string message)> StartAsync(string serverId)
        {
            ServerTable? server = null;

            _mainWindow.Dispatcher.Invoke(() =>
            {
                foreach (ServerTable s in _mainWindow.ServerGrid.Items)
                    if (s.ID == serverId) { server = s; break; }
            });

            if (server == null)
                return (false, $"Server '{serverId}' not found.");

            var meta = _mainWindow.GetServerMetadata(serverId);
            if (meta?.ServerStatus == MainWindow.ServerStatus.Started ||
                meta?.ServerStatus == MainWindow.ServerStatus.Starting)
                return (false, "Server is already running or starting.");

            try
            {
                await _mainWindow.Dispatcher.InvokeAsync(async () =>
                    await _mainWindow.GameServer_StartById(serverId)).Task.Unwrap();
                return (true, "Start command sent.");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public async Task<(bool success, string message)> StopAsync(string serverId)
        {
            ServerTable? server = null;
            _mainWindow.Dispatcher.Invoke(() =>
            {
                foreach (ServerTable s in _mainWindow.ServerGrid.Items)
                    if (s.ID == serverId) { server = s; break; }
            });

            if (server == null) return (false, $"Server '{serverId}' not found.");

            var meta = _mainWindow.GetServerMetadata(serverId);
            if (meta?.ServerStatus == MainWindow.ServerStatus.Stopped ||
                meta?.ServerStatus == MainWindow.ServerStatus.Stopping)
                return (false, "Server is already stopped or stopping.");

            try
            {
                await _mainWindow.Dispatcher.InvokeAsync(async () =>
                    await _mainWindow.GameServer_StopById(serverId)).Task.Unwrap();
                return (true, "Stop command sent.");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public async Task<(bool success, string message)> RestartAsync(string serverId)
        {
            ServerTable? server = null;
            _mainWindow.Dispatcher.Invoke(() =>
            {
                foreach (ServerTable s in _mainWindow.ServerGrid.Items)
                    if (s.ID == serverId) { server = s; break; }
            });

            if (server == null) return (false, $"Server '{serverId}' not found.");

            try
            {
                await _mainWindow.Dispatcher.InvokeAsync(async () =>
                    await _mainWindow.GameServer_RestartById(serverId)).Task.Unwrap();
                return (true, "Restart command sent.");
            }
            catch (Exception ex) { return (false, ex.Message); }
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

        public async Task<(bool success, string message)> UpdateAsync(string serverId)
        {
            if (_mainWindow.GetServerMetadata(serverId) == null)
                return (false, $"Server '{serverId}' not found.");

            var meta = _mainWindow.GetServerMetadata(serverId);
            if (meta?.ServerStatus != MainWindow.ServerStatus.Stopped)
                return (false, "Server must be stopped before updating.");

            try
            {
                await _mainWindow.Dispatcher.InvokeAsync(async () =>
                    await _mainWindow.GameServer_UpdateById(serverId)).Task.Unwrap();
                return (true, "Update completed.");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        // ── Per-server backup ─────────────────────────────────────────────────

        public async Task<(bool success, string message)> BackupAsync(string serverId)
        {
            if (_mainWindow.GetServerMetadata(serverId) == null)
                return (false, $"Server '{serverId}' not found.");

            var meta = _mainWindow.GetServerMetadata(serverId);
            if (meta?.ServerStatus != MainWindow.ServerStatus.Stopped)
                return (false, "Server must be stopped before backup.");

            try
            {
                await _mainWindow.Dispatcher.InvokeAsync(async () =>
                    await _mainWindow.GameServer_BackupById(serverId)).Task.Unwrap();
                return (true, "Backup completed.");
            }
            catch (Exception ex) { return (false, ex.Message); }
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

        public async Task<(bool success, string message)> RestoreBackupAsync(string serverId, string backupFile)
        {
            if (_mainWindow.GetServerMetadata(serverId) == null)
                return (false, $"Server '{serverId}' not found.");

            var meta = _mainWindow.GetServerMetadata(serverId);
            if (meta?.ServerStatus != MainWindow.ServerStatus.Stopped)
                return (false, "Server must be stopped before restoring.");

            try
            {
                await _mainWindow.Dispatcher.InvokeAsync(async () =>
                    await _mainWindow.GameServer_RestoreBackupById(serverId, backupFile)).Task.Unwrap();
                return (true, "Restore completed.");
            }
            catch (Exception ex) { return (false, ex.Message); }
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

        public void ReloadPlugins()
        {
            _mainWindow.Dispatcher.InvokeAsync(() => _mainWindow.LoadPlugins());
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
