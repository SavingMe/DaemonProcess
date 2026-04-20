using ProcessDaemon.Services;

namespace ProcessDaemon.Workers;

public class DaemonWorker : BackgroundService
{
    private readonly ProcessManager _processManager;
    public DaemonWorker(ProcessManager processManager) => _processManager = processManager;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _processManager.StartAllSequentialAsync(stoppingToken);
        await _processManager.MonitorLoopAsync(stoppingToken);
    }
}