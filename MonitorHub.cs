using Microsoft.AspNetCore.SignalR;
using ProcessDaemon.Services;

namespace ProcessDaemon.Hubs;

public class MonitorHub : Hub
{
    private readonly ProcessManager _processManager;

    public MonitorHub(ProcessManager processManager)
    {
        _processManager = processManager;
    }

    public override async Task OnConnectedAsync()
    {
        var statuses = await _processManager.GetStatusSnapshotsAsync();
        await Clients.Caller.SendAsync("ReceiveStatuses", statuses);
        await base.OnConnectedAsync();
    }

    public async Task StopProcess(string id)
    {
        await _processManager.StopProcessAsync(id);
    }

    public async Task StartProcess(string id)
    {
        await _processManager.StartProcessAsync(id);
    }

    public async Task SubscribeLogs(string id)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"log_{id}");
        var logs = await _processManager.GetRecentLogsAsync(id);
        await Clients.Caller.SendAsync("ReceiveHistoryLogs", id, logs);
    }

    public async Task UnsubscribeLogs(string id)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"log_{id}");
    }
}
