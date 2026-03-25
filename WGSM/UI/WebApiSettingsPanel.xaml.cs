using System;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using WindowsGSM.WebApi.Models;
using WindowsGSM.WebApi.Services;

namespace WindowsGSM.UI
{
    public partial class WebApiSettingsPanel : UserControl
    {
        private WebApiServer? _server;
        private WebApiConfig  _config = WebApiConfig.Load();
        private bool          _loading;

        public WebApiSettingsPanel()
        {
            InitializeComponent();
            LoadConfigToUi();
        }

        // — Sub-nav tab switching ————————————————————————————————————————————

        private void OnTabChanged(object sender, RoutedEventArgs e)
        {
            if (PanelGeneral == null) return; // called before InitializeComponent completes
            PanelGeneral.Visibility    = TabGeneral.IsChecked    == true ? Visibility.Visible : Visibility.Collapsed;
            PanelKeys.Visibility       = TabKeys.IsChecked       == true ? Visibility.Visible : Visibility.Collapsed;
            PanelConnection.Visibility = TabConnection.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PanelBackup.Visibility     = TabBackup.IsChecked     == true ? Visibility.Visible : Visibility.Collapsed;
            PanelLog.Visibility        = TabLog.IsChecked        == true ? Visibility.Visible : Visibility.Collapsed;
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
            _loading = true;
            try
            {
                InstanceNameBox.Text      = _config.InstanceName;
                PortBox.Text              = _config.Port.ToString();
                BackupLocalPathBox.Text   = _config.BackupLocalPath;
                BackupOnedrivPathBox.Text = _config.BackupOnedrivePath;
                BackupGdrivePathBox.Text  = _config.BackupGdrivePath;
                CertPathBox.Text     = string.IsNullOrEmpty(_config.CertPath) ? "No certificate imported" : _config.CertPath;
                KeyPathBox.Text      = string.IsNullOrEmpty(_config.KeyPath)  ? "No key imported"          : _config.KeyPath;
                HttpsCheckBox.IsChecked    = _config.HttpsEnabled;
                AutoStartCheckBox.IsChecked = _config.AutoStart;

                ScopeLocal.IsChecked    = _config.Scope == ConnectionScope.LocalOnly;
                ScopeLan.IsChecked      = _config.Scope == ConnectionScope.LAN;
                ScopeExternal.IsChecked = _config.Scope == ConnectionScope.External;

                RefreshKeysList();
            }
            finally { _loading = false; }
        }

        // — Instance name ————————————————————————————————————————————————————

        private void OnSaveInstanceName(object sender, RoutedEventArgs e)
        {
            var name = InstanceNameBox.Text.Trim();
            if (string.IsNullOrEmpty(name)) return;
            _config.InstanceName = name;
            _config.Save();
            AppendLog($"Instance name saved: {name}");
        }

        // — API Keys —————————————————————————————————————————————————————————

        private void RefreshKeysList()
        {
            KeysPanel.Children.Clear();

            if (_config.ApiKeys.Count == 0)
            {
                KeysPanel.Children.Add(new TextBlock
                {
                    Text       = "No API keys. Click 'Add key' to create one.",
                    Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                    Margin     = new Thickness(8, 6, 8, 6),
                    FontSize   = 12
                });
                return;
            }

            foreach (var key in _config.ApiKeys.ToList())
            {
                var row = new Grid { Margin = new Thickness(4, 3, 4, 3) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var nameLabel = new TextBlock
                {
                    Text               = key.Name,
                    Foreground         = Brushes.White,
                    VerticalAlignment  = VerticalAlignment.Center,
                    FontWeight         = FontWeights.Medium
                };

                var preview = key.Token.Length > 16
                    ? key.Token[..8] + "···" + key.Token[^4..]
                    : key.Token;
                var tokenLabel = new TextBlock
                {
                    Text              = preview,
                    Foreground        = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                    FontFamily        = new FontFamily("Consolas"),
                    FontSize          = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin            = new Thickness(8, 0, 8, 0),
                    ToolTip           = key.Token
                };

                var copyBtn = new Button
                {
                    Content = "Copy",
                    Style   = (Style)FindResource("GhostBtn"),
                    Margin  = new Thickness(0, 0, 4, 0),
                    Tag     = key.Token
                };
                copyBtn.Click += (_, _) =>
                {
                    Clipboard.SetText(key.Token);
                    AppendLog($"Token copied to clipboard: {key.Name}");
                };

                var removeBtn = new Button
                {
                    Content = "Remove",
                    Style   = (Style)FindResource("DangerBtn"),
                    Tag     = key
                };
                removeBtn.Click += (_, _) =>
                {
                    _config.ApiKeys.Remove(key);
                    _config.Save();
                    RefreshKeysList();
                    AppendLog($"API key removed: {key.Name}");
                };

                Grid.SetColumn(nameLabel, 0);
                Grid.SetColumn(tokenLabel, 1);
                Grid.SetColumn(copyBtn, 2);
                Grid.SetColumn(removeBtn, 3);

                row.Children.Add(nameLabel);
                row.Children.Add(tokenLabel);
                row.Children.Add(copyBtn);
                row.Children.Add(removeBtn);

                KeysPanel.Children.Add(row);
            }
        }

        private void OnAddKey(object sender, RoutedEventArgs e)
        {
            var name = NewKeyNameBox.Text.Trim();
            if (string.IsNullOrEmpty(name)) name = "Key " + (_config.ApiKeys.Count + 1);

            var key = new ApiKey
            {
                Name  = name,
                Token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N")
            };
            _config.ApiKeys.Add(key);
            _config.Save();
            RefreshKeysList();
            AppendLog($"API key added: {key.Name}");
            AppendLog($"Token ({key.Name}): {key.Token}");
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
            if (_loading) return;
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
            if (_loading) return;
            _config.HttpsEnabled = HttpsCheckBox.IsChecked == true;
            _config.Save();
        }

        // — Auto-start ————————————————————————————————————————————————————————

        private void OnAutoStartToggled(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
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

                // Log all active tokens
                foreach (var key in _config.ApiKeys.Where(k => !string.IsNullOrEmpty(k.Token)))
                    AppendLog($"Token ({key.Name}): {key.Token}");

                AppendLog($"API port {_config.Port}: {(IsPortListening(_config.Port) ? "OK - listening" : "WARNING - not listening")}");
                await CheckGameServerPortsAsync();

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
            StopButton.IsEnabled  = running;
            StatusLabel.Text      = running ? "Running" : "Not running";
            StatusLabel.Foreground = running
                ? new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0))
                : new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        }

        // — URLs —————————————————————————————————————————————————————————————

        private async Task RefreshUrlsAsync()
        {
            if (_server == null) return;
            var network = _server.Network;
            var scheme  = _config.HttpsEnabled ? "https" : "http";
            var port    = _config.Port;

            LocalUrlLabel.Text  = $"{scheme}://localhost:{port}/ui";
            LanUrlLabel.Text    = $"{scheme}://{network.GetLanIp()}:{port}/ui";
            PublicUrlLabel.Text = $"{scheme}://{await network.GetPublicIpAsync()}:{port}/ui";
        }

        private void ClearUrls()
        {
            LocalUrlLabel.Text  = "—";
            LanUrlLabel.Text    = "—";
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
                return props.GetActiveTcpListeners().Any(ep => ep.Port == port)
                    || props.GetActiveUdpListeners().Any(ep => ep.Port == port);
            }
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
                var parts = new System.Collections.Generic.List<string>();
                if (int.TryParse(s.ServerPort, out int gp) && gp > 0)
                    parts.Add($"game:{gp} {(IsPortListening(gp) ? "OK" : "not listening")}");
                if (int.TryParse(s.QueryPort, out int qp) && qp > 0 && qp != gp)
                    parts.Add($"query:{qp} {(IsPortListening(qp) ? "OK" : "not listening")}");
                if (parts.Any())
                    AppendLog($"  [{s.Name}] {string.Join("  ", parts)}");
            }
            AppendLog("------------------------");
        }

        // — Backup ————————————————————————————————————————————————————————————

        private void OnBrowseBackupLocal(object sender, RoutedEventArgs e)
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select backup destination folder"
            };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                BackupLocalPathBox.Text = dlg.SelectedPath;
        }

        private void OnSaveBackupPaths(object sender, RoutedEventArgs e)
        {
            _config.BackupLocalPath    = BackupLocalPathBox.Text.Trim();
            _config.BackupOnedrivePath = BackupOnedrivPathBox.Text.Trim();
            _config.BackupGdrivePath   = BackupGdrivePathBox.Text.Trim();
            _config.Save();
            AppendLog("Backup paths saved.");
        }

        private async void OnCreateBackup(object sender, RoutedEventArgs e)
        {
            if (_server == null) return;
            AppendLog("Creating backup...");
            var svc = new WGSM.WebApi.Services.BackupService(_config);
            var (success, message, _) = await Task.Run(() => svc.CreateBackup());
            AppendLog(message);
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
