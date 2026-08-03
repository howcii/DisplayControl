namespace DisplayControl.Server.Services;

/// <summary>
/// 定时维护命令生命周期，避免网页看到长期停留在“已发送”的陈旧状态。
/// </summary>
public sealed partial class CommandMaintenanceService(
    CommandCoordinator commands,
    ILogger<CommandMaintenanceService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                commands.RunMaintenance();
            }
            catch (Exception ex)
            {
                LogMaintenanceFailed(logger, ex);
            }
        }
    }

    [LoggerMessage(
        EventId = 3001,
        Level = LogLevel.Warning,
        Message = "Command maintenance failed.")]
    private static partial void LogMaintenanceFailed(ILogger logger, Exception exception);
}
