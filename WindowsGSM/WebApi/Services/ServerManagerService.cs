using System;
using System.Collections.Generic;
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
                        Id = server.ID,
                        Name = server.ID,   // ServerTable uses ID as display key; name comes from config
                        Game = server.Game ?? "Unknown",
                        Status = meta?.ServerStatus.ToString() ?? server.Status ?? "Unknown",
                        ServerIp = server.IP ?? "",
                        ServerPort = server.Port ?? "",
                        QueryPort = server.QueryPort ?? "",
                        Pid = (meta?.Process != null && !meta.Process.HasExited)
                              ? meta.Process.Id : (int?)null
                    });
                }
            });

            return result;
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
    }
}
