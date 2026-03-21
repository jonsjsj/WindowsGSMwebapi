using System;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WindowsGSM.WebApi.Controllers;
using WindowsGSM.WebApi.Middleware;
using WindowsGSM.WebApi.Models;
using WindowsGSM.WebApi.Services;

namespace WindowsGSM.WebApi.Services
{
    /// <summary>
    /// Hosts the ASP.NET Core Kestrel server inside the existing WPF process.
    /// Call StartAsync() to bring the API up, StopAsync() to tear it down.
    /// A single instance is owned by WebApiSettingsPanel and referenced from MainWindow.
    /// </summary>
    public class WebApiServer : IAsyncDisposable
    {
        private IHost? _host;
        private CancellationTokenSource? _cts;

        public WebApiConfig Config { get; }
        public NetworkInfoService Network { get; }
        public ServerManagerService ServerManager { get; }
        public ApiLogger Logger { get; } = new ApiLogger();

        public bool IsRunning => _host != null;

        public event EventHandler<string>? LogMessage;

        public WebApiServer(
            WebApiConfig config,
            NetworkInfoService network,
            ServerManagerService serverManager)
        {
            Config = config;
            Network = network;
            ServerManager = serverManager;

            // Forward ApiLogger entries to the LogMessage event (→ UI log box)
            Logger.OnLog += msg => LogMessage?.Invoke(this, msg);
        }

        public async Task StartAsync()
        {
            if (_host != null)
                throw new InvalidOperationException("Web API server is already running.");

            _cts = new CancellationTokenSource();

            var bindAddress = Network.BuildBindAddress(Config.Scope, Config.Port);
            Log($"Starting Web API on {bindAddress}");

            _host = Host.CreateDefaultBuilder()
                .ConfigureWebHostDefaults(web =>
                {
                    web.UseKestrel(options => ConfigureKestrel(options))
                       .UseContentRoot(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WebApi"))
                       .UseWebRoot("wwwroot")
                       .ConfigureServices(ConfigureServices)
                       .Configure(ConfigureApp);
                })
                .Build();

            await _host.StartAsync(_cts.Token);
            Log($"Web API running. UI at: http://localhost:{Config.Port}/ui");
        }

        public async Task StopAsync()
        {
            if (_host == null) return;
            Log("Stopping Web API...");
            _cts?.Cancel();
            await _host.StopAsync(TimeSpan.FromSeconds(5));
            _host.Dispose();
            _host = null;
            _cts = null;
            Log("Web API stopped.");
        }

        // — Kestrel configuration ————————————————————————————————————

        private void ConfigureKestrel(KestrelServerOptions options)
        {
            // Always listen on HTTP at the configured port
            if (Config.Scope == ConnectionScope.LocalOnly)
                options.ListenLocalhost(Config.Port);
            else
                options.ListenAnyIP(Config.Port);

            // HTTPS on port+1 if enabled and cert exists
            if (Config.HttpsEnabled && File.Exists(Config.CertPath) && File.Exists(Config.KeyPath))
            {
                var httpsPort = Config.Port + 1;
                options.ListenAnyIP(httpsPort, listenOptions =>
                {
                    listenOptions.UseHttps(LoadCertificate());
                });
                Log($"HTTPS enabled on port {httpsPort}");
            }
        }

        private X509Certificate2 LoadCertificate()
        {
            // Load PEM cert + key files (supported natively in .NET 5+)
            return X509Certificate2.CreateFromPemFile(Config.CertPath, Config.KeyPath);
        }

        // — DI services ——————————————————————————————————————————————

        private void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton(Config);
            services.AddSingleton(Network);
            services.AddSingleton(ServerManager);
            services.AddSingleton(Logger);   // shared ApiLogger
            services.AddSingleton<A2SQueryService>();
            services.AddSingleton<ResourceMonitorService>();
            services.AddSingleton<PortCheckService>();
            services.AddSingleton<PortManagementService>();
            services.AddSingleton<BackupService>();
            services.AddSingleton<UpdateService>();
            services.AddControllers()
                    .AddJsonOptions(o =>
                    {
                        o.JsonSerializerOptions.PropertyNamingPolicy =
                            System.Text.Json.JsonNamingPolicy.CamelCase;
                    });
            services.AddCors(o => o.AddDefaultPolicy(p =>
                p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
        }

        // — ASP.NET pipeline ————————————————————————————————————————

        private void ConfigureApp(IApplicationBuilder app)
        {
            app.UseCors();

            // Log every request first so nothing is missed
            app.UseMiddleware<RequestLoggingMiddleware>();

            // HTTPS redirect when cert is loaded
            if (Config.HttpsEnabled && File.Exists(Config.CertPath))
                app.UseHttpsRedirection();

            // Scope enforcement (defence-in-depth, Kestrel bind already handles this)
            app.UseMiddleware<ScopeBindingMiddleware>();

            // Token authentication
            app.UseMiddleware<TokenAuthMiddleware>();

            // Serve wwwroot static files (app.js, favicon, etc.)
            app.UseStaticFiles();

            app.UseRouting();
            app.UseEndpoints(e => e.MapControllers());
        }

        private void Log(string message) =>
            Logger.Log(message);

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
        }
    }
}
