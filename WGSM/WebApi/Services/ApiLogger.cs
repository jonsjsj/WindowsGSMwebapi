using System;

namespace WindowsGSM.WebApi.Services
{
    /// <summary>
    /// Shared logging sink that middleware and services write to.
    /// WebApiServer subscribes and forwards entries to the UI log box.
    /// </summary>
    public class ApiLogger
    {
        public event Action<string>? OnLog;

        public void Log(string message) =>
            OnLog?.Invoke($"[{DateTime.Now:HH:mm:ss}] {message}");
    }
}
