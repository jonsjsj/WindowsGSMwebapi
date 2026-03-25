using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace WindowsGSM.WebApi.Services
{
    /// <summary>
    /// Background service that periodically samples CPU and RAM usage per process.
    /// CPU% is computed by comparing TotalProcessorTime across a 1-second window.
    /// Results are cached so API calls return instantly without blocking.
    /// </summary>
    public class ResourceMonitorService : IDisposable
    {
        private readonly ConcurrentDictionary<int, double> _cpuCache = new();
        private readonly Timer _timer;
        private readonly int _cpuCount = Environment.ProcessorCount;

        // PIDs we are actively tracking (populated by the server manager via SetTrackedPids)
        private readonly ConcurrentDictionary<int, byte> _trackedPids = new();

        public ResourceMonitorService()
        {
            // Sample every 5 seconds
            _timer = new Timer(SampleAll, null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        }

        /// <summary>Registers a PID for background CPU monitoring.</summary>
        public void TrackPid(int pid) => _trackedPids[pid] = 0;

        /// <summary>Unregisters a PID.</summary>
        public void UntrackPid(int pid)
        {
            _trackedPids.TryRemove(pid, out _);
            _cpuCache.TryRemove(pid, out _);
        }

        /// <summary>Returns the last cached CPU% for the given PID, or null if not available.</summary>
        public double? GetCpuPercent(int? pid)
        {
            if (pid == null) return null;
            return _cpuCache.TryGetValue(pid.Value, out var v) ? v : (double?)null;
        }

        /// <summary>Returns current RAM usage in MB for the given PID, or null if not available.</summary>
        public double? GetRamMb(int? pid)
        {
            if (pid == null) return null;
            try
            {
                var proc = Process.GetProcessById(pid.Value);
                proc.Refresh();
                return Math.Round(proc.WorkingSet64 / (1024.0 * 1024.0), 1);
            }
            catch
            {
                return null;
            }
        }

        private void SampleAll(object? _)
        {
            foreach (var pid in _trackedPids.Keys)
            {
                try
                {
                    var proc = Process.GetProcessById(pid);
                    proc.Refresh();

                    var t1 = proc.TotalProcessorTime;
                    var w1 = Stopwatch.GetTimestamp();

                    Thread.Sleep(500);

                    proc.Refresh();
                    var t2 = proc.TotalProcessorTime;
                    var w2 = Stopwatch.GetTimestamp();

                    var elapsed = (w2 - w1) / (double)Stopwatch.Frequency;
                    var cpuUsed = (t2 - t1).TotalSeconds;
                    var cpuPercent = cpuUsed / (elapsed * _cpuCount) * 100.0;

                    _cpuCache[pid] = Math.Round(Math.Max(0, Math.Min(100 * _cpuCount, cpuPercent)), 1);
                }
                catch
                {
                    _cpuCache.TryRemove(pid, out double _);
                }
            }
        }

        public void Dispose()
        {
            _timer.Dispose();
        }
    }
}
