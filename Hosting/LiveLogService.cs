using Microsoft.Extensions.Options;
using SampleLogGenerator.Configuration;

namespace SampleLogGenerator.Hosting;

/// <summary>Drives the live generator from the wall clock.</summary>
public sealed class LiveLogService(SimulationHost host, IOptions<LogGeneratorOptions> options, ILogger<LiveLogService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Live log generation {State}. Writing to {LogsFile}",
            host.IsRunning ? "started" : "paused (AutoStart=false)", host.LogsPath);

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(50, options.Value.TickMilliseconds)));
        try
        {
            do
            {
                try
                {
                    host.Tick(DateTime.UtcNow);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Log generation tick failed");
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        host.Shutdown();
    }
}
