using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace WindowsGSM.WebApi.Services
{
    /// <summary>
    /// Queries game servers using the A2S_INFO protocol (Source Engine query).
    /// Returns player counts for running game servers.
    /// </summary>
    public class A2SQueryService
    {
        // A2S_INFO challenge request packet
        private static readonly byte[] A2sInfoRequest = new byte[]
        {
            0xFF, 0xFF, 0xFF, 0xFF, 0x54,
            // "Source Engine Query\0"
            0x53, 0x6F, 0x75, 0x72, 0x63, 0x65, 0x20, 0x45, 0x6E, 0x67,
            0x69, 0x6E, 0x65, 0x20, 0x51, 0x75, 0x65, 0x72, 0x79, 0x00
        };

        private const int TimeoutMs = 2000;

        /// <summary>
        /// Sends an A2S_INFO query to the server's query port.
        /// Returns (currentPlayers, maxPlayers) or null on failure.
        /// </summary>
        public async Task<(int current, int max)?> QueryAsync(string host, string queryPort)
        {
            if (!int.TryParse(queryPort, out var port) || port <= 0 || port > 65535)
                return null;

            try
            {
                using var udp = new UdpClient();
                udp.Client.ReceiveTimeout = TimeoutMs;
                udp.Client.SendTimeout    = TimeoutMs;

                var endpoint = new IPEndPoint(IPAddress.Any, 0);

                // Send the request
                await udp.SendAsync(A2sInfoRequest, A2sInfoRequest.Length, host, port)
                          .ConfigureAwait(false);

                // Receive with timeout via Task.WhenAny
                var receiveTask = udp.ReceiveAsync();
                var timeoutTask = Task.Delay(TimeoutMs);

                if (await Task.WhenAny(receiveTask, timeoutTask).ConfigureAwait(false) == timeoutTask)
                    return null;

                var result = await receiveTask.ConfigureAwait(false);
                return ParseA2SInfo(result.Buffer);
            }
            catch
            {
                return null;
            }
        }

        private static (int current, int max)? ParseA2SInfo(byte[] data)
        {
            // Minimum valid A2S_INFO response is around 19 bytes
            // Header: 4 bytes (FF FF FF FF) + type byte (0x49)
            // Then: protocol(1), name(str), map(str), folder(str), game(str), appid(2),
            //       players(1), max(1), bots(1)...
            if (data == null || data.Length < 6) return null;

            // Check header: FF FF FF FF 49
            if (data[0] != 0xFF || data[1] != 0xFF || data[2] != 0xFF || data[3] != 0xFF)
                return null;

            // 0x49 = A2S_INFO response; 0x6D = obsolete GoldSrc response
            if (data[4] != 0x49 && data[4] != 0x6D) return null;

            try
            {
                int pos = 5;

                // Skip protocol byte
                pos++;

                // Skip null-terminated strings: Name, Map, Folder, Game
                for (int i = 0; i < 4; i++)
                    pos = SkipString(data, pos);

                // Skip AppID (2 bytes)
                pos += 2;

                if (pos + 2 > data.Length) return null;

                int currentPlayers = data[pos++];
                int maxPlayers     = data[pos++];

                return (currentPlayers, maxPlayers);
            }
            catch
            {
                return null;
            }
        }

        private static int SkipString(byte[] data, int pos)
        {
            while (pos < data.Length && data[pos] != 0x00)
                pos++;
            return pos + 1; // skip the null terminator
        }
    }
}
