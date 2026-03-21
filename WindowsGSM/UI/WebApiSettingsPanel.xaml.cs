using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using WindowsGSM.WebApi.Models;
using WindowsGSM.WebApi.Services;

namespace WindowsGSM.UI
{
    public partial class WebApiSettingsPanel : UserControl
    {
        private WebApiServer? _server;
        private WebApiConfig _config = WebApiConfig.Load();

        public WebApiSettingsPanel()
        {
            InitializeComponent();
            LoadConfigToUi();
        }

        // Called by MainWindow after it has set up ServerManagerService
        public void Initialize(WebApiServer server)
        {
            _server = server;
            // Use the server's config instance so token changes are visible to middleware
            _config = _server.Config;
            LoadConfigToUi();
            _server.LogMessage += (_, msg) => AppendLog(msg);

            if (_config.AutoStart)
                _ = StartApiAsync();
        }

        // — Config → UI ——————————————————————————————————————————————————————

        private void LoadConfigToUi()
        {
            TokenBox.Password = _config.ApiToken;
            PortBox.Text = _config.Port.ToString();
            CertPathBox.Text = string.IsNullOrEmpty(_config.CertPath) ? "No certificate imported" : _config.CertPath;
            KeyPathBox.Text = string.IsNullOrEmpty(_config.KeyPath) ? "No key imported" : _config.KeyPath;
            HttpsCheckBox.IsChecked = _config.HttpsEnabled;
            AutoStartCheckBox.IsChecked = _config.AutoStart;

            ScopeLocal.IsChecked = _config.Scope == ConnectionScope.LocalOnly;
            ScopeLan.IsChecked = _config.Scope == ConnectionScope.LAN;
            ScopeExternal.IsChecked = _config.Scope == ConnectionScope.External;
        }

        // — Token ————————————————————————————————————————————————————————————

        private void OnGenerateToken(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(
                "Generate a new API token? Any connected clients will be disconnected.",
                "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            _config.ApiToken = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            _config.Save();
            TokenBox.Password = _config.ApiToken;
            AppendLog("New API token generated.");
            AppendLog($"API Token: {_config.ApiToken}");
        }

        private void OnRevokeToken(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(
                "Revoke the current token? All clients will lose access immediately.",
                "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            _config.ApiToken = string.Empty;
            _config.Save();
            TokenBox.Password = string.Empty;
            AppendLog("API token revoked.");
        }

        // — Port —————————————————————————————————————————————————————————————

        private void OnPortPreviewInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = !Regex.IsMatch(e.Text, @"^\d+$");
        }

        private void SavePort()
        {
            if (int.TryParse(PortBox.Text, out int port) && port is >= 1024 and <= 65535)
            {
                _config.Port = port;
                _config.Save();
            }
        }

        // — Scope ————————————————————————————————————————————————————————————

        private void OnScopeChanged(object sender, RoutedEventArgs e)
        {
            _config.Scope = ScopeLocal.IsChecked == true ? ConnectionScope.LocalOnly
                          : ScopeLan.IsChecked == true   ? ConnectionScope.LAN
                                                         : ConnectionScope.External;
            _config.Save();
        }

        // — HTTPS ————————————————————————————————————————————————————————————

        private void OnImportCert(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "PEM certificate|*.pem;*.crt|All files|*.*", Title = "Select certificate file" };
            if (dlg.ShowDialog() == true)
            {
                _config.CertPath = dlg.FileName;
                _config.Save();
                CertPathBox.Text = dlg.FileName;
            }
        }

        private void OnImportKey(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "PEM key|*.pem;*.key|All files|*.*", Title = "Select private key file" };
            if (dlg.ShowDialog() == true)
            {
                _config.KeyPath = dlg.FileName;
                _config.Save();
                KeyPathBox.Text = dlg.FileName;
            }
        }

        private void OnHttpsToggled(object sender, RoutedEventArgs e)
        {
            _config.HttpsEnabled = HttpsCheckBox.IsChecked == true;
            _config.Save();
        }

        // — Auto-start ————————————————————————————————————————————————————————

        private void OnAutoStartToggled(object sender, RoutedEventArgs e)
        {
            _config.AutoStart = AutoStartCheckBox.IsChecked == true;
            _config.Save();
        }

        // — Start / Stop ——————————————————————————————————————————————————————

        private async void OnStartApi(object sender, RoutedEventArgs e) => await StartApiAsync();
        private async void OnStopApi(object sender, RoutedEventArgs e)  => await StopApiAsync();

        private async Task StartApiAsync()
        {
            if (_server == null) return;
            SavePort();

            StartButton.IsEnabled = false;
            StatusLabel.Text = "Starting...";

            try
            {
                await _server.StartAsync();
                SetRunningState(true);
                await RefreshUrlsAsync();

                // Print token and port status to log
                AppendLog($"API Token: {_config.ApiToken}");
                AppendLog($"API port {_config.Port}: {(IsPortListening(_config.Port) ? "OK - listening" : "WARNING - not listening")}");
                await CheckGameServerPortsAsync();

                // Auto-open browser per issue #207 spec
                var scheme = _config.HttpsEnabled ? "https" : "http";
                Process.Start(new ProcessStartInfo($"{scheme}://localhost:{_config.Port}/ui") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppendLog($"Failed to start: {ex.Message}");
                SetRunningState(false);
            }
        }

        private async Task StopApiAsync()
        {
            if (_server == null) return;
            StopButton.IsEnabled = false;

            try { await _server.StopAsync(); }
            catch (Exception ex) { AppendLog($"Stop error: {ex.Message}"); }

            SetRunningState(false);
            ClearUrls();
        }

        private void SetRunningState(bool running)
        {
            StartButton.IsEnabled = !running;
            StopButton.IsEnabled = running;
            StatusLabel.Text = running ? "Running" : "Not running";
            StatusLabel.Foreground = running
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4E, 0xC9, 0xB0))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88));
        }

        // — URLs —————————————————————————————————————————————————————————————

        private async Task RefreshUrlsAsync()
        {
            if (_server == null) return;
            var network = _server.Network;
            var scheme = _config.HttpsEnabled ? "https" : "http";
            var port = _config.Port;

            LocalUrlLabel.Text = $"{scheme}://localhost:{port}/ui";
            LanUrlLabel.Text = $"{scheme}://{network.GetLanIp()}:{port}/ui";
            PublicUrlLabel.Text = $"{scheme}://{await network.GetPublicIpAsync()}:{port}/ui";
        }

        private void ClearUrls()
        {
            LocalUrlLabel.Text = "—";
            LanUrlLabel.Text = "—";
            PublicUrlLabel.Text = "—";
        }

        private void OnUrlClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is TextBlock tb && tb.Text != "—")
                Process.Start(new ProcessStartInfo(tb.Text) { UseShellExecute = true });
        }

        // — Port checks ——————————————————————————————————————————————————————

        private static bool IsPortListening(int port)
        {
            try
            {
                var props = IPGlobalProperties.GetIPGlobalProperties();
                bool tcp = props.GetActiveTcpListeners().Any(ep => ep.Port == port);
                bool udp = props.GetActiveUdpListeners().Any(ep => ep.Port == port);
                return tcp || udp;
            }
            catch { return false; }
        }

        private static bool IsPortListeningTcp(int port)
        {
            try { return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(ep => ep.Port == port); }
            catch { return false; }
        }

        private async Task CheckGameServerPortsAsync()
        {
            if (_server == null) return;

            var servers = await Task.Run(() => _server.ServerManager.GetAllServers());
            var running = servers.Where(s => s.Status == "Started").ToList();

            if (!running.Any()) return;

            AppendLog("--- Game server ports ---");
            foreach (var s in running)
            {
                var parts = new List<string>();

                if (int.TryParse(s.ServerPort, out int gamePort) && gamePort > 0)
                    parts.Add($"game:{gamePort} {(IsPortListening(gamePort) ? "OK" : "not listening")}");

                if (int.TryParse(s.QueryPort, out int queryPort) && queryPort > 0 && queryPort != gamePort)
                    parts.Add($"query:{queryPort} {(IsPortListening(queryPort) ? "OK" : "not listening")}");

                if (parts.Any())
                    AppendLog($"  [{s.Name}] {string.Join("  ", parts)}");
            }
            AppendLog("------------------------");
        }

        // — Log ——————————————————————————————————————————————————————————————

        private void AppendLog(string message)
        {
            Dispatcher.Invoke(() =>
            {
                LogBox.AppendText(message + "\n");
                LogBox.ScrollToEnd();
            });
        }
    }
}
