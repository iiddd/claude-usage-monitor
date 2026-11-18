using ClaudeUsageServer.Models;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Serilog;
using Serilog.Events;

namespace ClaudeUsageServer.Services;

/// <summary>Host wiring around the DI container: logging setup and the loopback-only guard.</summary>
internal static class HostSetup
{
    /// <summary>
    /// A Warning+ Event Log logger that captures startup failures before the full configuration
    /// from <see cref="ConfigureLogging"/> takes over.
    /// </summary>
    public static Serilog.ILogger CreateBootstrapLogger() => new LoggerConfiguration()
        .MinimumLevel.Warning()
        .WriteTo.EventLog("ClaudeUsageServer", manageEventSource: false)
        .CreateBootstrapLogger();

    /// <summary>
    /// Configures the rolling daily file sink and the Warning+ Event Log sink. Log sizing is read
    /// straight from configuration because Serilog is set up before the DI container is built;
    /// <see cref="LogCleanupService"/> reads the same options from DI for its total-size sweep.
    /// <c>DiagnosticLogging</c> lowers the service's own minimum level to Debug so the
    /// correlation-tagged credential events appear; framework categories stay at Warning.
    /// </summary>
    public static void ConfigureLogging(HostBuilderContext context, LoggerConfiguration cfg)
    {
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ClaudeUsageServer", "logs");
        Directory.CreateDirectory(logDir);

        var section = context.Configuration.GetSection(ServerOptions.SectionName);
        var retentionDays = section.GetValue<int>("LogRetentionDays", 14);
        var maxFileSizeMB = section.GetValue<int>("LogMaxFileSizeMB", 10);
        var diagnostic = section.GetValue<bool>("DiagnosticLogging", false);

        cfg.MinimumLevel.Is(diagnostic ? LogEventLevel.Debug : LogEventLevel.Information)
           .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
           .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
           .Enrich.FromLogContext()
           .WriteTo.File(
               path: Path.Combine(logDir, "claudeusageserver-.log"),
               rollingInterval: RollingInterval.Day,
               retainedFileCountLimit: retentionDays,
               fileSizeLimitBytes: (long)maxFileSizeMB * 1024L * 1024L,
               rollOnFileSizeLimit: true,
               outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
           .WriteTo.EventLog(
               source: "ClaudeUsageServer",
               logName: "Application",
               manageEventSource: false,
               restrictedToMinimumLevel: LogEventLevel.Warning);
    }

    /// <summary>
    /// Once the server has started, stops the application if Kestrel bound to anything other than
    /// loopback: the proxy holds a live bearer token and must never be reachable from the network.
    /// </summary>
    public static void StopUnlessLoopbackOnly(WebApplication app)
    {
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            var server = app.Services.GetRequiredService<IServer>();
            var feature = server.Features.Get<IServerAddressesFeature>();
            if (feature is null) return;

            foreach (var addr in feature.Addresses)
            {
                if (!addr.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase) &&
                    !addr.StartsWith("http://[::1]", StringComparison.OrdinalIgnoreCase))
                {
                    Log.Fatal("Non-loopback binding detected — stopping service immediately");
                    app.Lifetime.StopApplication();
                    return;
                }
            }

            Log.Information("ClaudeUsageServer listening on {Addresses}",
                string.Join(", ", feature.Addresses));
        });
    }
}
