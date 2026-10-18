using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SecRandom.Core.Services.Config;

namespace SecRandom.Services.Verification;

/// <summary>
///     Keeps a fresh beacon pulse in <see cref="BeaconEntropyProvider" />'s single-pulse cache while the periodic
///     prefetch switch is on, so a draw that starts inside the tick interval consumes it with no network wait.
///     It only calls <see cref="BeaconEntropyProvider.Prefetch" />, which fetches the pulse and reserves nothing:
///     the per-pulse sequence is still reserved inside <see cref="BeaconEntropyProvider.ReserveAsync" />, so a
///     minute that ends without a draw consumes no sequence and leaves no gap in the proof chain. The provider
///     discards a cached pulse older than one period, so a tick nothing consumed can never feed a later draw.
///     Everything here is best-effort: a failed fetch is logged at debug level and retried on the next tick,
///     because the draw fetches again on its own and must fail loudly rather than inherit a background failure.
/// </summary>
public sealed class BeaconPrefetchScheduler(
    MainConfigHandler configHandler,
    BeaconEntropyProvider beaconEntropyProvider,
    ILogger<BeaconPrefetchScheduler> logger) : BackgroundService
{
    /// <summary>
    ///     One pulse period. A tick therefore always leaves a cached pulse younger than the age limit, and a draw
    ///     anywhere inside the minute between two ticks still finds the current period's pulse in the cache.
    /// </summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (IsAutoPrefetchEnabled())
                    beaconEntropyProvider.Prefetch();
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "信标脉冲自动预取失败，下一分钟重试。");
            }

            try
            {
                await Task.Delay(TickInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    // Both switches are required: the periodic prefetch is the background half of the same feature, so enabling
    // it alone must not start fetching pulses for a user who turned the feature off.
    private bool IsAutoPrefetchEnabled() =>
        configHandler.Data.General.Verification.BeaconPrefetchEnabled &&
        configHandler.Data.General.Verification.BeaconAutoPrefetchEnabled;
}
