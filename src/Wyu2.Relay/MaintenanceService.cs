using Microsoft.Extensions.Options;

namespace Wyu2.Relay;

/// <summary>Periodically expires presence blobs and beacons, prunes dead accounts and flushes the snapshot.</summary>
public sealed class MaintenanceService(
    RelayStore store,
    IOptions<RelayOptions> options,
    ILogger<MaintenanceService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(10, options.Value.SnapshotIntervalSeconds));
        using var timer = new PeriodicTimer(interval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var (blobs, beacons, requests, accounts) = store.Prune();
                if (blobs + beacons + requests + accounts > 0)
                {
                    log.LogDebug(
                        "Pruned {Blobs} presence blobs, {Beacons} beacons, {Requests} requests, {Accounts} accounts",
                        blobs, beacons, requests, accounts);
                }

                store.Save();
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        finally
        {
            store.Save(force: true);
        }
    }
}
