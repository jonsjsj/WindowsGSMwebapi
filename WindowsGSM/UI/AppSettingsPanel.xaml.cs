using ControlzEx.Theming;
using MahApps.Metro.Controls;
using MahApps.Metro.Controls.Dialogs;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;
using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WindowsGSM.WebApi.Services;

namespace WindowsGSM.UI
{
    public partial class AppSettingsPanel : UserControl
    {
        private WebApiServer? _server;
        private string?       _pendingUpdateUrl;
        private bool          _loading;

        public AppSettingsPanel()
        {
            InitializeComponent();
            PopulateThemes();
            LoadFromRegistry();
        }

        // Called by MainWindow.InitializeWebApi() after _webApiServer is ready
        public void Initialize(WebApiServer? server)
        {
            _server = server;
        }

        // Exposed so MainWindow can read it (replaces the 4 flyout references)
        public bool IsOn_SendStatistics => Switch_SendStatistics.IsOn;

        // ── Helpers ──────────────────────────────────────────────────────────

        private static MetroWindow? MetroOwner =>
            Application.Current.MainWindow as MetroWindow;

        private static string RegistryGet(string name, string fallback = "")
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\WindowsGSM");
            return key?.GetValue(name)?.ToString() ?? fallback;
        }

        private static void RegistrySet(string name, string value)
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\WindowsGSM", true);
            key?.SetValue(name, value);
        }

        // ── Themes combobox ──────────────────────────────────────────────────

        private void PopulateThemes()
        {
            foreach (var name in ThemeManager.Current.Themes
                         .Select(t => System.IO.Path.GetExtension(t.Name).Trim('.'))
                         .Distinct()
                         .OrderBy(x => x))
            {
                ThemeComboBox.Items.Add(name);
            }
        }

        // ── Load from registry ────────────────────────────────────────────────

        private void LoadFromRegistry()
        {
            _loading = true;
            try
            {
                Switch_HardwareAcceleration.IsOn = RegistryGet("HardWareAcceleration", "True")  == "True";
                Switch_UIAnimation.IsOn           = RegistryGet("UIAnimation",          "True")  == "True";
                Switch_DarkTheme.IsOn             = RegistryGet("DarkTheme",            "False") == "True";
                Switch_StartOnBoot.IsOn           = RegistryGet("StartOnBoot",          "False") == "True";
                Switch_RestartOnCrash.IsOn        = RegistryGet("RestartOnCrash",       "False") == "True";
                Switch_SendStatistics.IsOn        = RegistryGet("SendStatistics",       "True")  == "True";

                bool donorActive = RegistryGet("DonorTheme", "False") == "True";
                Switch_DonorConnect.Toggled -= OnDonorConnect_Toggled;
                Switch_DonorConnect.IsOn = donorActive;
                Switch_DonorConnect.Toggled += OnDonorConnect_Toggled;

                bool lockEnabled = RegistryGet("ServerLockEnabled", "False") == "True";
                Switch_ServerLock.Toggled -= OnServerLock_Toggled;
                Switch_ServerLock.IsOn = lockEnabled;
                Switch_ServerLock.Toggled += OnServerLock_Toggled;
                SetPasswordButton.Visibility = lockEnabled ? Visibility.Visible : Visibility.Collapsed;

                ThemeComboBox.IsEnabled = donorActive;
                string savedColor = RegistryGet("DonorColor", MainWindow.DEFAULT_THEME);
                ThemeComboBox.SelectionChanged -= OnThemeSelectionChanged;
                ThemeComboBox.SelectedItem = ThemeComboBox.Items.Contains(savedColor) ? savedColor : MainWindow.DEFAULT_THEME;
                ThemeComboBox.SelectionChanged += OnThemeSelectionChanged;

                VersionLabel.Text = UpdateService.CurrentVersion;
            }
            finally { _loading = false; }
        }

        // ── Tab switching ─────────────────────────────────────────────────────

        private void OnTabChanged(object sender, RoutedEventArgs e)
        {
            if (PanelGeneral == null) return;
            PanelGeneral.Visibility = TabGeneral.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PanelStartup.Visibility = TabStartup.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PanelAbout.Visibility   = TabAbout.IsChecked   == true ? Visibility.Visible : Visibility.Collapsed;
            if (TabStartup.IsChecked == true) LoadAutoStartServers();
        }

        private void LoadAutoStartServers()
        {
            AutoStartServersList.Children.Clear();
            bool any = false;
            for (int i = 1; i <= MainWindow.MAX_SERVER; i++)
            {
                var cfg = new Functions.ServerConfig(i.ToString());
                if (string.IsNullOrEmpty(cfg.ServerGame)) continue;
                any = true;
                AutoStartServersList.Children.Add(BuildAutoStartRow(cfg));
            }
            AutoStartNoServersLabel.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        }

        private UIElement BuildAutoStartRow(Functions.ServerConfig cfg)
        {
            var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var mutedBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
            var label  = new TextBlock { Text = $"#{cfg.ServerID} — {cfg.ServerName}", Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)), FontSize = 13 };
            var hint   = new TextBlock { Text = cfg.ServerGame, Foreground = mutedBrush, FontSize = 11 };
            var stack  = new StackPanel();
            stack.Children.Add(label);
            stack.Children.Add(hint);

            var toggle = new ToggleSwitch
            {
                IsOn = cfg.AutoStart, OnContent = "", OffContent = "",
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0)
            };
            var capturedId = cfg.ServerID;
            toggle.Toggled += (s, _) =>
                Functions.ServerConfig.SetSetting(capturedId, Functions.ServerConfig.SettingName.AutoStart,
                    ((ToggleSwitch)s).IsOn ? "1" : "0");

            Grid.SetColumn(stack,  0);
            Grid.SetColumn(toggle, 1);
            grid.Children.Add(stack);
            grid.Children.Add(toggle);
            return grid;
        }

        // ── Display toggles ───────────────────────────────────────────────────

        private void OnDarkTheme_Toggled(object sender, EventArgs e)
        {
            if (_loading) return;
            RegistrySet("DarkTheme", Switch_DarkTheme.IsOn.ToString());
            ThemeHelper.Apply(Switch_DarkTheme.IsOn);
        }

        private void OnHardwareAcceleration_Toggled(object sender, EventArgs e)
        {
            if (_loading) return;
            RegistrySet("HardWareAcceleration", Switch_HardwareAcceleration.IsOn.ToString());
            if (Application.Current.MainWindow is MainWindow mw)
                mw.ApplyHardwareAcceleration(Switch_HardwareAcceleration.IsOn);
        }

        private void OnUIAnimation_Toggled(object sender, EventArgs e)
        {
            if (_loading) return;
            RegistrySet("UIAnimation", Switch_UIAnimation.IsOn.ToString());
            if (Application.Current.MainWindow is MetroWindow mw)
                mw.WindowTransitionsEnabled = Switch_UIAnimation.IsOn;
        }

        private void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || ThemeComboBox.SelectedItem == null) return;
            RegistrySet("DonorColor", ThemeComboBox.SelectedItem.ToString()!);
            ThemeHelper.Apply(Switch_DarkTheme.IsOn);
        }

        // ── Behavior toggles ──────────────────────────────────────────────────

        private void OnRestartOnCrash_Toggled(object sender, EventArgs e)
        {
            if (_loading) return;
            RegistrySet("RestartOnCrash", Switch_RestartOnCrash.IsOn.ToString());
        }

        private void OnSendStatistics_Toggled(object sender, EventArgs e)
        {
            if (_loading) return;
            RegistrySet("SendStatistics", Switch_SendStatistics.IsOn.ToString());
        }

        // ── Startup toggles ───────────────────────────────────────────────────

        private void OnStartOnBoot_Toggled(object sender, EventArgs e)
        {
            if (_loading) return;
            RegistrySet("StartOnBoot", Switch_StartOnBoot.IsOn.ToString());
            SetStartOnBoot(Switch_StartOnBoot.IsOn);
        }

        private static void SetStartOnBoot(bool enable)
        {
            try
            {
                var exePath = Process.GetCurrentProcess().MainModule!.FileName;
                if (enable)
                {
                    Process.Start(new ProcessStartInfo("schtasks",
                        $"/create /f /tn \"WindowsGSM\" /tr \"{exePath}\" /sc onlogon /rl highest")
                    { CreateNoWindow = true, UseShellExecute = false });
                }
                else
                {
                    Process.Start(new ProcessStartInfo("schtasks",
                        "/delete /f /tn \"WindowsGSM\"")
                    { CreateNoWindow = true, UseShellExecute = false });
                }
            }
            catch { /* ignore */ }
        }

        // ── Donor Connect ─────────────────────────────────────────────────────

        private async void OnDonorConnect_Toggled(object sender, EventArgs e)
        {
            if (_loading) return;

            if (!Switch_DonorConnect.IsOn)
            {
                // Disconnect
                RegistrySet("DonorTheme", "False");
                RegistrySet("DonorColor", MainWindow.DEFAULT_THEME);
                ThemeComboBox.IsEnabled = false;
                ThemeComboBox.SelectionChanged -= OnThemeSelectionChanged;
                ThemeComboBox.SelectedItem = ThemeComboBox.Items.Contains(MainWindow.DEFAULT_THEME)
                    ? MainWindow.DEFAULT_THEME : null;
                ThemeComboBox.SelectionChanged += OnThemeSelectionChanged;
                ThemeHelper.Apply(Switch_DarkTheme.IsOn);
                if (Application.Current.MainWindow is MainWindow mwDisconnect)
                    mwDisconnect.OnDonorActivated(string.Empty);
                return;
            }

            var owner = MetroOwner;
            if (owner == null) return;

            string savedKey = RegistryGet("DonorAuthKey");
            var dlgSettings = new MetroDialogSettings { AffirmativeButtonText = "Activate", DefaultText = savedKey };
            string? authKey = await owner.ShowInputAsync("Donor Connect (Patreon)", "Please enter the activation key.", dlgSettings);

            if (string.IsNullOrWhiteSpace(authKey))
            {
                _loading = true;
                Switch_DonorConnect.IsOn = false;
                _loading = false;
                return;
            }

            var progress = await owner.ShowProgressAsync("Authenticating...", "Please wait...");
            progress.SetIndeterminate();
            var (success, name) = await AuthenticateDonorAsync(authKey);
            await progress.CloseAsync();

            if (success)
            {
                RegistrySet("DonorTheme",  "True");
                RegistrySet("DonorAuthKey", authKey);
                ThemeComboBox.IsEnabled = true;
                await owner.ShowMessageAsync("Success!", $"Thanks for your donation {name}, your support helps us a lot!\nYou can choose any theme you like in Settings!");
            }
            else
            {
                RegistrySet("DonorTheme",  "False");
                RegistrySet("DonorAuthKey", string.Empty);
                await owner.ShowMessageAsync("Activation failed.", "Please visit https://windowsgsm.com/patreon/ to get the key.");
                _loading = true;
                Switch_DonorConnect.IsOn = false;
                _loading = false;
            }
        }

        private async Task<(bool success, string name)> AuthenticateDonorAsync(string authKey)
        {
            try
            {
                using var wc = new WebClient();
                string json = await wc.DownloadStringTaskAsync(
                    $"https://windowsgsm.com/patreon/patreonAuth.php?auth={authKey}");
                var obj = JObject.Parse(json);
                if (obj["success"]?.ToString() == "True")
                {
                    string name = obj["name"]?.ToString() ?? string.Empty;
                    string type = obj["type"]?.ToString() ?? string.Empty;
                    if (Application.Current.MainWindow is MainWindow mw)
                        mw.OnDonorActivated(type);
                    return (true, name);
                }
            }
            catch { /* ignore */ }
            ThemeHelper.Apply(Switch_DarkTheme.IsOn);
            return (false, string.Empty);
        }

        // ── Security (server lock) ────────────────────────────────────────────

        private async void OnServerLock_Toggled(object sender, EventArgs e)
        {
            if (_loading) return;
            if (Switch_ServerLock.IsOn)
            {
                string? pw = await PromptNewPassword();
                if (pw == null) { _loading = true; Switch_ServerLock.IsOn = false; _loading = false; return; }
                RegistrySet("ServerLockEnabled", "True");
                RegistrySet("ServerLockHash", HashPassword(pw));
                SetPasswordButton.Visibility = Visibility.Visible;
            }
            else
            {
                RegistrySet("ServerLockEnabled", "False");
                SetPasswordButton.Visibility = Visibility.Collapsed;
            }
        }

        private async void OnSetPassword(object sender, RoutedEventArgs e)
        {
            string? pw = await PromptNewPassword();
            if (pw == null) return;
            RegistrySet("ServerLockHash", HashPassword(pw));
            await (MetroOwner?.ShowMessageAsync("Password Updated", "Server lock password has been changed.") ?? Task.CompletedTask);
        }

        private async Task<string?> PromptNewPassword()
        {
            var owner = MetroOwner;
            if (owner == null) return null;
            string? pw1 = await owner.ShowInputAsync("Set Password", "Enter a new server lock password:", new MetroDialogSettings { AffirmativeButtonText = "Next" });
            if (string.IsNullOrEmpty(pw1)) return null;
            string? pw2 = await owner.ShowInputAsync("Confirm Password", "Re-enter the password to confirm:", new MetroDialogSettings { AffirmativeButtonText = "Set" });
            if (pw1 != pw2) { await owner.ShowMessageAsync("Mismatch", "Passwords do not match."); return null; }
            return pw1;
        }

        private static string HashPassword(string pw) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pw)));

        // ── App Update ────────────────────────────────────────────────────────

        private async void OnCheckUpdate(object sender, RoutedEventArgs e)
        {
            UpdateStatusLabel.Text      = "Checking...";
            ApplyUpdateButton.IsEnabled = false;
            _pendingUpdateUrl           = null;

            var svc = new UpdateService(
                _server?.ServerManager ?? new ServerManagerService(null!));
            var (hasUpdate, latestTag, downloadUrl, error) = await svc.CheckForUpdateAsync();

            if (error != null)
            {
                UpdateStatusLabel.Text = $"Check failed: {error}";
                return;
            }

            if (hasUpdate)
            {
                _pendingUpdateUrl           = downloadUrl;
                ApplyUpdateButton.IsEnabled = downloadUrl != null;
                UpdateStatusLabel.Text      = $"Update available: {latestTag}";
            }
            else
            {
                UpdateStatusLabel.Text = $"Up to date ({UpdateService.CurrentVersion})";
            }
        }

        private async void OnApplyUpdate(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_pendingUpdateUrl)) return;

            ApplyUpdateButton.IsEnabled = false;
            UpdateStatusLabel.Text      = "Downloading and applying update...";

            var svc = new UpdateService(
                _server?.ServerManager ?? new ServerManagerService(null!));
            var (success, message) = await svc.ApplyUpdateAsync(_pendingUpdateUrl);

            if (!success)
            {
                UpdateStatusLabel.Text      = "Update failed";
                ApplyUpdateButton.IsEnabled = true;
            }
            // On success the application shuts down — nothing more to do
        }

        // ── Links ─────────────────────────────────────────────────────────────

        private void OnLinkWebsite(object sender, RoutedEventArgs e) =>
            Process.Start(new ProcessStartInfo("https://windowsgsm.com") { UseShellExecute = true });

        private void OnLinkDiscord(object sender, RoutedEventArgs e) =>
            Process.Start(new ProcessStartInfo("https://discord.windowsgsm.com") { UseShellExecute = true });

        private void OnLinkPatreon(object sender, RoutedEventArgs e) =>
            Process.Start(new ProcessStartInfo("https://www.patreon.com/WindowsGSM") { UseShellExecute = true });
    }
}
