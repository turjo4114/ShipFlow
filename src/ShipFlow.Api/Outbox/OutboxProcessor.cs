using Microsoft.Extensions.Options;
using ShipFlow.Api.Infrastructure;
using ShipFlow.Api.Resilience;

namespace ShipFlow.Api.Outbox;

/// <summary>
/// Out-of-band worker: drains the persistent outbox and pushes to the carrier.
/// Survives restarts (state lives in SQL), is safe to run on many instances (READPAST claim),
/// and never lets one poison message stall the queue (per-message failure + backoff + dead letter).
/// </summary>
public sealed class OutboxProcessor(
    IOutboxStore store,
    ICarrierGateway carrier,
    IOptions<OutboxOptions> options,
    ILogger<OutboxProcessor> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var o = options.Value;
        log.LogInformation("Outbox processor started (poll {Poll}s, batch {Batch}).", o.PollSeconds, o.BatchSize);

        while (!stop.IsCancellationRequested)
        {
            var claimed = 0;
            try
            {
                var batch = await store.ClaimAsync(o.BatchSize, o.LockMinutes, stop);
                claimed = batch.Count;

                foreach (var msg in batch)
                {
                    try
                    {
                        await carrier.SendAsync(msg.OutboxMessageId, msg.TenantId, msg.MessageType, msg.Payload, stop);
                        await store.CompleteAsync(msg.OutboxMessageId, stop);
                    }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested)
                    {
                        return; // shutting down; the claim expires and the row is re-claimed later
                    }
                    catch (Exception ex)
                    {
                        // Includes BrokenCircuitException when the breaker is open: back off, don't hammer the carrier.
                        log.LogWarning("Outbox message {Id} attempt {Attempt} failed: {Error}", msg.OutboxMessageId, msg.Attempts, ex.Message);
                        await store.FailAsync(msg.OutboxMessageId, msg.Attempts, o.MaxAttempts, ex.Message, stop);
                    }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                log.LogError(ex, "Outbox poll failed; will retry.");
            }

            // Full batch => more probably waiting, go again immediately. Otherwise sleep.
            if (claimed < o.BatchSize)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(o.PollSeconds), stop); }
                catch (OperationCanceledException) { return; }
            }
        }
    }
}
