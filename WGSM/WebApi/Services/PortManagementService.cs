using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace WGSM.WebApi.Services
{
    /// <summary>
    /// Manages Windows Firewall rules for game server and API ports via netsh.
    /// All operations require the process to be running as Administrator.
    /// Manual rule names: "WGSM Port {port} {protocol}".
    /// Auto-managed rule names: "WGSM Auto {serverId} {port} {protocol}".
    /// </summary>
    public class PortManagementService
    {
        private static string RuleName(int port, string proto)     => $"WGSM Port {port} {proto.ToUpper()}";
        private static string AutoRuleName(string id, int port, string proto) => $"WGSM Auto {id} {port} {proto.ToUpper()}";

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

        // ── Auto-managed rules (tied to a specific server) ───────────────────

        /// <summary>
        /// Opens TCP + UDP inbound firewall rules for a server's game and query ports.
        /// Rule names include the serverId so they can be removed independently of manual rules.
        /// Silently ignores errors — firewall operations are best-effort.
        /// </summary>
        public void OpenPortsForServer(string serverId, int gamePort, int queryPort)
        {
            var ports = new HashSet<int> { gamePort };
            if (queryPort > 0 && queryPort != gamePort) ports.Add(queryPort);
            foreach (var port in ports)
                foreach (var proto in new[] { "TCP", "UDP" })
                {
                    var name = AutoRuleName(serverId, port, proto);
                    try
                    {
                        RunNetsh($"advfirewall firewall add rule name=\"{name}\" " +
                                 $"dir=in action=allow protocol={proto} localport={port}");
                    }
                    catch { /* best-effort */ }
                }
        }

        /// <summary>
        /// Removes the auto-managed inbound firewall rules created by OpenPortsForServer.
        /// Silently ignores errors — firewall operations are best-effort.
        /// </summary>
        public void ClosePortsForServer(string serverId, int gamePort, int queryPort)
        {
            var ports = new HashSet<int> { gamePort };
            if (queryPort > 0 && queryPort != gamePort) ports.Add(queryPort);
            foreach (var port in ports)
                foreach (var proto in new[] { "TCP", "UDP" })
                {
                    var name = AutoRuleName(serverId, port, proto);
                    try { RunNetsh($"advfirewall firewall delete rule name=\"{name}\" dir=in protocol={proto}"); }
                    catch { /* best-effort */ }
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
