using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Events;

namespace KikuCaption.Infrastructure.Logging;

/// <summary>
/// Configures the Serilog rolling file sink used by the app (PROJECT.md 15).
/// Logs go to <c>logs/app-yyyyMMdd.log</c>, roll daily and are retained for the
/// configured number of days. Full captions, translations, PCM and Authorization
/// headers must never be written here.
/// </summary>
public static class SerilogConfigurator
{
    public static void Configure(LoggerConfiguration loggerConfiguration, IConfiguration configuration, string logDirectory)
    {
        Directory.CreateDirectory(logDirectory);

        var retainedDays = configuration.GetValue<int?>("Storage:LogRetentionDays") ?? 14;
        if (retainedDays < 1)
        {
            retainedDays = 1;
        }

        loggerConfiguration
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(
                path: Path.Combine(logDirectory, "app-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: retainedDays,
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.Console();
    }
}
