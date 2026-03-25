using System;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace WGSM.WebApi.Services
{
    /// <summary>
    /// Checks whether a TCP port is reachable within a 1-second timeout.
    /// Used to report game port and query port reachability on the dashboard.
    /// </summary>
    public class PortCheckService
    {
        private const int TimeoutMs = 1000;

        /// <summary>
        /// Returns true if a TCP connection to host:port succeeds within 1 second.
        /// Returns false on timeout, connection refused, or invalid input.
        /// </summary>
        public async Task<bool> IsReachableAsync(string host, string port)
        {
            if (string.IsNullOrWhiteSpace(host) || !int.TryParse(port, out var portNum))
                return false;

            if (portNum <= 0 || portNum > 65535)
                return false;

            try
            {
                using var client = new TcpClient();
                var connectTask = client.ConnectAsync(host, portNum);
                var timeoutTask = Task.Delay(TimeoutMs);

                var completed = await Task.WhenAny(connectTask, timeoutTask).ConfigureAwait(false);

                if (completed == timeoutTask)
                    return false;

                await connectTask.ConfigureAwait(false); // propagate any socket exception
                return client.Connected;
            }
            catch
            {
                return false;
            }
        }
    }
}
