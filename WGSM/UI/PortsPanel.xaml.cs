using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WindowsGSM.UI
{
    public partial class PortsPanel : UserControl
    {
        private int _webApiPort = 7876;

        public PortsPanel()
        {
            InitializeComponent();
        }

        public void Initialize(int webApiPort)
        {
            _webApiPort = webApiPort;
        }

        // Called when the panel becomes visible
        public async void RefreshAll()
        {
            await RunChecks();
        }

        private async void OnRefresh(object sender, RoutedEventArgs e)
        {
            await RunChecks();
        }

        private async Task RunChecks()
        {
            PublicIpLabel.Text = "Public IP: detecting…";

            // Build port rows
            var gameRows   = BuildGameServerRows();
            var webApiRows = BuildWebApiRows();

            GameServerPortsList.ItemsSource = new ObservableCollection<PortRowViewModel>(gameRows);
            WebApiPortsList.ItemsSource     = new ObservableCollection<PortRowViewModel>(webApiRows);
            NoGameServersLabel.Visibility   = gameRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // Local checks (synchronous, fast)
            foreach (var row in gameRows.Concat(webApiRows))
                row.LocalStatus = CheckLocalPort(row.Port, row.Protocol) ? PortStatus.Open : PortStatus.Closed;

            // Docker (process call, slightly async)
            var dockerRows = await Task.Run(GetDockerRows);
            DockerPortsList.ItemsSource = new ObservableCollection<PortRowViewModel>(dockerRows);
            DockerStatusLabel.Text = dockerRows.Count == 0
                ? "Docker not detected or no containers running."
                : $"{dockerRows.Count} port{(dockerRows.Count == 1 ? "" : "s")} found.";
            foreach (var row in dockerRows)
                row.LocalStatus = CheckLocalPort(row.Port, row.Protocol) ? PortStatus.Open : PortStatus.Closed;

            // External checks (async, network)
            string? publicIp = await GetPublicIpAsync();
            PublicIpLabel.Text = publicIp != null ? $"Public IP: {publicIp}" : "Public IP: unavailable";

            var allRows = gameRows.Concat(webApiRows).Concat(dockerRows).ToList();
            if (publicIp != null)
                await CheckExternalPortsAsync(publicIp, allRows);
            else
                foreach (var row in allRows)
                    row.ExternalStatus = PortStatus.Unknown;
        }

        // ── Row builders ──────────────────────────────────────────────────────

        private List<PortRowViewModel> BuildGameServerRows()
        {
            var rows = new List<PortRowViewModel>();
            for (int i = 1; i <= MainWindow.MAX_SERVER; i++)
            {
                var cfg = new Functions.ServerConfig(i.ToString());
                if (string.IsNullOrEmpty(cfg.ServerGame)) continue;
                string label = $"#{cfg.ServerID} {cfg.ServerName}";

                if (int.TryParse(cfg.ServerPort, out int port) && port > 0)
                    rows.Add(new PortRowViewModel($"{label} (game)", port, "UDP"));
                if (int.TryParse(cfg.ServerQueryPort, out int qport) && qport > 0 && qport != port)
                    rows.Add(new PortRowViewModel($"{label} (query)", qport, "UDP"));
            }
            return rows;
        }

        private List<PortRowViewModel> BuildWebApiRows() =>
            new() { new PortRowViewModel("WGSM Web API", _webApiPort, "TCP") };

        private static List<PortRowViewModel> GetDockerRows()
        {
            try
            {
                var psi = new ProcessStartInfo("docker",
                    "ps --format \"{{.Names}}\\t{{.Ports}}\"")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true
                };
                using var p = Process.Start(psi);
                if (p == null) return [];
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);

                var rows = new List<PortRowViewModel>();
                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = line.Split('\t');
                    if (parts.Length < 2) continue;
                    string name = parts[0].Trim();
                    foreach (Match m in Regex.Matches(parts[1], @":(\d+)->(\d+)/(tcp|udp)"))
                    {
                        if (int.TryParse(m.Groups[1].Value, out int hostPort))
                            rows.Add(new PortRowViewModel(name, hostPort, m.Groups[3].Value.ToUpperInvariant()));
                    }
                }
                return rows;
            }
            catch { return []; }
        }

        // ── Port checks ───────────────────────────────────────────────────────

        private static bool CheckLocalPort(int port, string protocol)
        {
            try
            {
                var props = IPGlobalProperties.GetIPGlobalProperties();
                if (protocol == "UDP")
                    return props.GetActiveUdpListeners().Any(ep => ep.Port == port);
                return props.GetActiveTcpListeners().Any(ep => ep.Port == port);
            }
            catch { return false; }
        }

        private static async Task<string?> GetPublicIpAsync()
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                return (await client.GetStringAsync("https://api.ipify.org")).Trim();
            }
            catch { return null; }
        }

        private static async Task CheckExternalPortsAsync(string publicIp, IReadOnlyList<PortRowViewModel> rows)
        {
            // Group by TCP vs UDP; portchecker.co supports TCP checks
            var tcpRows = rows.Where(r => r.Protocol == "TCP").ToList();
            var udpRows = rows.Where(r => r.Protocol == "UDP").ToList();

            if (tcpRows.Count > 0)
                await CheckViaPorcheckerAsync(publicIp, tcpRows);

            // UDP external check: not reliably detectable via remote API — mark as unknown
            foreach (var row in udpRows)
                row.ExternalStatus = PortStatus.Unknown;
        }

        private static async Task CheckViaPorcheckerAsync(string publicIp, List<PortRowViewModel> rows)
        {
            try
            {
                var ports   = rows.Select(r => r.Port).Distinct().ToList();
                var payload = $"{{\"host\":\"{publicIp}\",\"ports\":[{string.Join(",", ports)}]}}";
                using var client  = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                var content = new StringContent(payload, Encoding.UTF8, "application/json");
                var resp    = await client.PostAsync("https://portchecker.co/api/v1/query", content);
                if (!resp.IsSuccessStatusCode) { SetAllUnknown(rows); return; }

                var obj = JObject.Parse(await resp.Content.ReadAsStringAsync());
                var portStatuses = obj["ports"]?.ToDictionary(
                    t => t["port"]!.Value<int>(),
                    t => t["status"]!.Value<string>() == "open") ?? new();

                foreach (var row in rows)
                    row.ExternalStatus = portStatuses.TryGetValue(row.Port, out bool open)
                        ? (open ? PortStatus.Open : PortStatus.Closed)
                        : PortStatus.Unknown;
            }
            catch { SetAllUnknown(rows); }
        }

        private static void SetAllUnknown(IEnumerable<PortRowViewModel> rows)
        {
            foreach (var row in rows) row.ExternalStatus = PortStatus.Unknown;
        }
    }

    // ── ViewModel ─────────────────────────────────────────────────────────────

    public enum PortStatus { Checking, Open, Closed, Unknown }

    public class PortRowViewModel : INotifyPropertyChanged
    {
        private static readonly Brush CheckingBrush;
        private static readonly Brush OpenBrush;
        private static readonly Brush ClosedBrush;
        private static readonly Brush UnknownBrush;

        static PortRowViewModel()
        {
            CheckingBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)));
            OpenBrush     = Freeze(new SolidColorBrush(Color.FromRgb(0x00, 0xD4, 0xAA)));
            ClosedBrush   = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x4D, 0x4D)));
            UnknownBrush  = Freeze(new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)));
        }

        private static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

        public string Label    { get; }
        public int    Port     { get; }
        public string Protocol { get; }

        private PortStatus _local    = PortStatus.Checking;
        private PortStatus _external = PortStatus.Checking;

        public PortStatus LocalStatus
        {
            get => _local;
            set { _local = value; OnPC(); OnPC(nameof(LocalBrush)); OnPC(nameof(LocalText)); }
        }

        public PortStatus ExternalStatus
        {
            get => _external;
            set { _external = value; OnPC(); OnPC(nameof(ExternalBrush)); OnPC(nameof(ExternalText)); }
        }

        public Brush LocalBrush    => _local    == PortStatus.Open ? OpenBrush : _local    == PortStatus.Closed ? ClosedBrush : CheckingBrush;
        public Brush ExternalBrush => _external == PortStatus.Open ? OpenBrush : _external == PortStatus.Closed ? ClosedBrush : UnknownBrush;
        public string LocalText    => _local    == PortStatus.Checking ? "Checking" : _local    == PortStatus.Open ? "Listening" : "Closed";
        public string ExternalText => _external == PortStatus.Checking ? "Checking" : _external == PortStatus.Open ? "Reachable" : _external == PortStatus.Closed ? "Blocked" : "N/A (UDP)";

        public PortRowViewModel(string label, int port, string protocol = "TCP")
        {
            Label = label; Port = port; Protocol = protocol;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPC([CallerMemberName] string? n = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
