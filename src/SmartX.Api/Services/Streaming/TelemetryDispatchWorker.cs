using SmartX.Core.Streaming;

namespace SmartX.Api.Services.Streaming;

/// <summary>
/// The single consumer of the dispatch queue. It takes packets one at a time -
/// critical lane first, always - and runs each through the shared pipeline.
/// <para>
/// <b>Why one consumer.</b> Two workers would process two packets from the same
/// node in parallel, and the second could be stored before the first. One worker
/// keeps each lane's order end to end. The cost is that a critical packet arriving
/// mid-packet waits for that one packet to finish - at most the configured
/// processing budget - and never for anything still queued behind it.
/// </para>
/// </summary>
public sealed class TelemetryDispatchWorker(
    DualLaneDispatchQueue<QueuedPacket> queue,
    IServiceScopeFactory scopeFactory,
    CommandStreamStore store,
    ILogger<TelemetryDispatchWorker> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var budget = TimeSpan.FromMilliseconds(store.Options.SimulatedProcessingMs);

        logger.LogInformation(
            "Dispatch worker running with a {Budget} ms per-packet budget.", budget.TotalMilliseconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            Dispatched<QueuedPacket> dispatched;

            try
            {
                dispatched = await queue.DequeueAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                if (budget > TimeSpan.Zero)
                {
                    // The modelled cost of validating and persisting the packet on a
                    // constrained gateway. See StreamOptions.SimulatedProcessingMs.
                    await Task.Delay(budget, stoppingToken);
                }

                using var scope = scopeFactory.CreateScope();
                scope.ServiceProvider.GetRequiredService<TelemetryPipeline>().ProcessQueued(dispatched);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception error)
            {
                // One bad packet must never stop the stream behind it.
                logger.LogError(error, "Failed to process packet {Sequence} from {Mac}.",
                    dispatched.Sequence, dispatched.Item.Mac);
            }
        }
    }
}
