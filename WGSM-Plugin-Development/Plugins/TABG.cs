using System;
using System.Diagnostics;
using System.Threading.Tasks;
using WGSM.Functions;
using WGSM.GameServer.Query;
using WGSM.GameServer.Engine;
using System.IO;
using Newtonsoft.Json;
using System.Text;

namespace WGSM.Plugins
{
    public class TABG : SteamCMDAgent
    {
        // - Plugin Details
        public Plugin Plugin = new Plugin
        {
            name = "WGSM.TABG", // WGSM.XXXX
            author = "raziel7893",
            description = "WGSM plugin for supporting TABG Dedicated Server",
            version = "1.0.0",
            url = "https://github.com/Raziel7893/WGSM.TABG", // Github repository link (Best practice) TODO
            color = "#34FFeb" // Color Hex
        };

        // - Settings properties for SteamCMD installer
        public override bool loginAnonymous => true;
        public override string AppId => "2311970"; // Game server appId Steam

        // - Standard Constructor and properties
        public TABG(ServerConfig serverData) : base(serverData) => base.serverData = _serverData = serverData;
        private readonly ServerConfig _serverData;


        // - Game server Fixed variables
        //public override string StartPath => "TABGServer.exe"; // Game server start path
        public override string StartPath => "TABG.exe";
        public string FullName = "TABG Dedicated Server"; // Game server FullName
        public bool AllowsEmbedConsole = true;  // Does this server support output redirect?
        public int PortIncrements = 1; // This tells WGSM how many ports should skip after installation

        // - Game server default values
        public string Port = "7777"; // Default port

        public string Additional = ""; // Additional server start parameter

        // TODO: Following options are not supported yet, as ther is no documentation of available options
        public string Maxplayers = "16"; // Default maxplayers        
        public string QueryPort = "7777"; // Default query port. This is the port specified in the Server Manager in the client UI to establish a server connection.
        // TODO: Unsupported option
        public string Defaultmap = "Dedicated"; // Default map name
        // TODO: Undisclosed method
        public object QueryMethod = new A2S(); // Query method should be use on current server type. Accepted value: null or new A2S() or new FIVEM() or new UT3()
        public string ConfigFile = "game_settings.txt";


        // - Create a default cfg for the game server after installation
        public async void CreateServerCFG()
        {
            string configFile = Functions.ServerPath.GetServersServerFiles(_serverData.ServerID, ConfigFile);
            StringBuilder sb = new StringBuilder();

            StreamReader sr = new StreamReader(configFile);
            var line = sr.ReadLine();
            while (line != null)
            {
                if (line.Contains("ServerName="))
                    sb.AppendLine($"ServerName={serverData.ServerName}");
                else if (line.Contains("Port="))
                    sb.AppendLine($"Port={serverData.ServerPort}");
                else if (line.Contains("MaxPlayers="))
                    sb.AppendLine($"MaxPlayers={serverData.ServerMaxPlayer}");
                else
                    sb.AppendLine(line);
                line = sr.ReadLine();
            }
            sr.Close();

            File.WriteAllText(configFile, sb.ToString());
        }

        // - Start server function, return its Process to WGSM
        public async Task<Process> Start()
        {
            string shipExePath = Functions.ServerPath.GetServersServerFiles(_serverData.ServerID, StartPath);
            if (!File.Exists(shipExePath))
            {
                Error = $"{Path.GetFileName(shipExePath)} not found ({shipExePath})";
                return null;
            }

            // Prepare Process
            var p = new Process
            {
                StartInfo =
                {
                    CreateNoWindow = false,
                    WorkingDirectory = ServerPath.GetServersServerFiles(_serverData.ServerID),
                    FileName = shipExePath,
                    Arguments = "",
                    WindowStyle = ProcessWindowStyle.Minimized,
                    UseShellExecute = false
                },
                EnableRaisingEvents = true
            };

            // Set up Redirect Input and Output to WGSM Console if EmbedConsole is on
            if (_serverData.EmbedConsole)
            {
                p.StartInfo.RedirectStandardInput = true;
                p.StartInfo.RedirectStandardOutput = true;
                p.StartInfo.RedirectStandardError = true;
                p.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                p.StartInfo.CreateNoWindow = true;
                var serverConsole = new ServerConsole(_serverData.ServerID);
                p.OutputDataReceived += serverConsole.AddOutput;
                p.ErrorDataReceived += serverConsole.AddOutput;
            }

            // Start Process
            try
            {
                p.Start();
                if (_serverData.EmbedConsole)
                {
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                }
                return p;
            }
            catch (Exception e)
            {
                Error = e.Message;
                return null; // return null if fail to start
            }
        }

        // - Stop server function
        public async Task Stop(Process p)
        {
            await Task.Run(() =>
            {
                Functions.ServerConsole.SetMainWindow(p.MainWindowHandle);
                Functions.ServerConsole.SendWaitToMainWindow("^c");
                p.WaitForExit(2000);
                if (!p.HasExited)
                    p.Kill();
            });
        }
    }
}
