namespace MTKChat.Server.Services;

public sealed class SiteUserSyncWorker(ChatDatabase database, ChatState state, ILogger<SiteUserSyncWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!database.IsConfigured) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { state.SynchronizeSiteUsers(database.ReadSiteAccounts()); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Site user synchronization failed");
            }
        }
    }
}
