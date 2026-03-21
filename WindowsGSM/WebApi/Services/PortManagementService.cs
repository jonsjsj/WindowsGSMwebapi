using System;
using System.Diagnostics;

namespace WindowsGSM.WebApi.Services
{
    /// <summary>
    /// Manages Windows Firewall rules for game server and API ports via netsh.
    /// All operations require the process to be running as Administrator.
    /// Rule names follow the pattern "WGSM Port {port} {protocol}".
    /// </summary>
    public class PortManagementService
    {
        private static string RuleName(int port, string proto) => $"WGSM Port {port} {proto.ToUpper()}";

        /// <summary>
        /// Checks whether an inbound Windows Firewall allow-rule exists for the given port.
        /// Returns (ruleExists, isEnabled) — both false on error or missing rule.
        /// </summary>
        public (bool ruleExists, bool isEnabled) GetFirewallStatus(int port, string protocol = "TCP")
        {
            var name = RuleName(port, protocol);
            try
            {
                var output = RunNetsh($"advfirewall firewall show rule name=\"{name}\" dir=in");
                if (!output.Contains("Rule Name:"))
                    return (false, false);

                // The output contains "Enabled: Yes" when the rule is active
                bool enabled = output.Contains("Enabled:") &&
                               output.IndexOf("Yes", output.IndexOf("Enabled:"),
                                   StringComparison.OrdinalIgnoreCase) >= 0;
                return (true, enabled);
            }
            catch
            {
                return (false, false);
            }
        }

        /// <summary>
        /// Adds an inbound Windows Firewall allow-rule for the port.
        /// No-ops (success) if the rule already exists.
        /// </summary>
        public (bool success, string message) OpenPort(int port, string protocol = "TCP")
        {
            var name = RuleName(port, protocol);
            var (exists, _) = GetFirewallStatus(port, protocol);
            if (exists)
                return (true, $"Firewall rule for port {port}/{protocol} already exists.");

            try
            {
                RunNetsh($"advfirewall firewall add rule name=\"{name}\" " +
                         $"dir=in action=allow protocol={protocol.ToUpper()} localport={port}");
                return (true, $"Firewall rule added: port {port}/{protocol} is now open.");
            }
            catch (Exception ex)
            {
                return (false, $"Failed to add firewall rule: {ex.Message}");
            }
        }

        /// <summary>
        /// Removes the WGSM inbound allow-rule for the port.
        /// No-ops (success) if no matching rule exists.
        /// </summary>
        public (bool success, string message) ClosePort(int port, string protocol = "TCP")
        {
            var name = RuleName(port, protocol);
            var (exists, _) = GetFirewallStatus(port, protocol);
            if (!exists)
                return (true, $"No firewall rule found for port {port}/{protocol}.");

            try
            {
                RunNetsh($"advfirewall firewall delete rule name=\"{name}\" dir=in protocol={protocol.ToUpper()}");
                return (true, $"Firewall rule removed: port {port}/{protocol} is now blocked.");
            }
            catch (Exception ex)
            {
                return (false, $"Failed to remove firewall rule: {ex.Message}");
            }
        }

        // ── helpers ─────────────────────────────────────────────────────────

        private static string RunNetsh(string args)
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName               = "netsh",
                    Arguments              = args,
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true
                }
            };
            proc.Start();
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);
            return output;
        }
    }
}
