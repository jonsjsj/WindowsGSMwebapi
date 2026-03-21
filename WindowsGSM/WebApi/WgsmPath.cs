using System.Diagnostics;
using System.IO;
using System.Linq;

namespace WindowsGSM.WebApi
{
    /// <summary>
    /// Provides the real executable directory.
    /// <para>
    /// For PublishSingleFile apps, <c>AppDomain.CurrentDomain.BaseDirectory</c> returns
    /// the temporary extraction folder, NOT the folder that contains WindowsGSM.exe.
    /// All persistent data (configs, backups, update staging) must be written relative
    /// to the directory of the running executable, which this helper exposes.
    /// </para>
    /// </summary>
    public static class WgsmPath
    {
        /// <summary>Directory that contains WindowsGSM.exe.</summary>
        public static readonly string AppDir =
            Path.GetDirectoryName(Process.GetCurrentProcess().MainModule!.FileName)!;

        /// <summary>Combines <see cref="AppDir"/> with additional path segments.</summary>
        public static string Combine(params string[] parts) =>
            Path.Combine(new[] { AppDir }.Concat(parts).ToArray());
    }
}
