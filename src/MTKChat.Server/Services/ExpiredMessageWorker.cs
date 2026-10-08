namespace MTKChat.Server.Services;

public sealed class ExpiredMessageWorker(ChatState state, ILogger<ExpiredMessageWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var count = state.RemoveExpired();
                if (count > 0) logger.LogInformation("{Count} expired encrypted messages removed", count);
                var statusCount = state.RemoveExpiredStatuses();
                if (statusCount > 0) logger.LogInformation("{Count} expired encrypted statuses removed", statusCount);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested && exception is not OutOfMemoryException)
            {
                // A temporary snapshot/database failure must not stop the host.
                // Status cleanup rolls back, and the next tick can safely retry.
                // Log only a type, never ciphertext, audience ids or connection data.
                logger.LogWarning("Expiration cleanup postponed after {ErrorType}; retrying next tick", exception.GetType().Name);
            }
        }
    }
}
