using Microsoft.AspNetCore.SignalR;
using ProcessDaemon.Hubs;
using ProcessDaemon.Models;
using System.Diagnostics;

namespace ProcessDaemon.Services;

public class ProcessManager
{
    private readonly IHubContext<MonitorHub> _hubContext;
    private readonly ILogger<ProcessManager> _logger;
    private readonly ProcessConfigStore _configStore;
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private readonly int _processorCount = Environment.ProcessorCount;
    private readonly List<ProcessItem> _processes;

    public ProcessManager(IHubContext<MonitorHub> hubContext, ProcessConfigStore configStore, ILogger<ProcessManager> logger)
    {
        _hubContext = hubContext;
        _configStore = configStore;
        _logger = logger;
        _processes = _configStore.Load().Select(CreateProcessItem).ToList();
    }

    public async Task StartAllSequentialAsync(CancellationToken stoppingToken)
    {
        List<(string Id, string Name, int StartupDelayMs)> startupQueue;

        await _stateLock.WaitAsync(stoppingToken);
        try
        {
            startupQueue = _processes
                .Where(p => !p.IsManuallyStopped && !p.IsCircuitBroken && !p.IsRunning)
                .Select(p => (p.Id, p.Name, p.StartupDelayMs))
                .ToList();
        }
        finally
        {
            _stateLock.Release();
        }

        foreach (var startupItem in startupQueue)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            var startResult = await StartProcessAsync(startupItem.Id);
            if (startResult)
            {
                await WaitUntilReadyAsync(startupItem.Name, startupItem.StartupDelayMs, stoppingToken);
            }
        }
    }

    private async Task WaitUntilReadyAsync(string processName, int startupDelayMs, CancellationToken stoppingToken)
    {
        _logger.LogInformation("等待 {ProcessName} 就绪，延迟 {StartupDelayMs}ms。", processName, startupDelayMs);
        await Task.Delay(startupDelayMs, stoppingToken);
    }

    public async Task MonitorLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            List<ProcessStatusDto> statuses;

            await _stateLock.WaitAsync(stoppingToken);
            try
            {
                foreach (var item in _processes)
                {
                    if (item.IsRunning)
                    {
                        CalculateResourceUsage(item);
                        continue;
                    }

                    if (item.IsManuallyStopped || item.IsCircuitBroken)
                    {
                        continue;
                    }

                    item.CrashHistory.RemoveAll(t => t < DateTime.UtcNow.AddSeconds(-60));
                    item.CrashHistory.Add(DateTime.UtcNow);

                    if (item.CrashHistory.Count >= 5)
                    {
                        item.IsCircuitBroken = true;
                        _logger.LogError("[熔断] {ProcessName} 崩溃过于频繁，已停止自动重启。", item.Name);
                        _ = SendEmergencyAlertAsync(item.Name, "1分钟内连续崩溃5次");
                    }
                    else
                    {
                        _logger.LogWarning("检测到 {ProcessName} 已停止，准备自动重启。", item.Name);
                        var started = StartProcessCore(item);
                        if (!started)
                        {
                            item.CrashHistory.Add(DateTime.UtcNow);
                            if (item.CrashHistory.Count >= 5)
                            {
                                item.IsCircuitBroken = true;
                                _logger.LogError("[熔断] {ProcessName} 启动后反复秒退，已停止自动重启。", item.Name);
                                _ = SendEmergencyAlertAsync(item.Name, "启动后反复秒退");
                            }
                        }
                    }
                }

                statuses = _processes.Select(ToStatusDto).ToList();
            }
            finally
            {
                _stateLock.Release();
            }

            await _hubContext.Clients.All.SendAsync("ReceiveStatuses", statuses, stoppingToken);
            await Task.Delay(3000, stoppingToken);
        }
    }

    public async Task<IReadOnlyList<ProcessConfigDto>> GetProcessConfigsAsync()
    {
        await _stateLock.WaitAsync();
        try
        {
            return _processes.Select(p => p.ToConfigDto()).ToList();
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task<IReadOnlyList<ProcessStatusDto>> GetStatusSnapshotsAsync()
    {
        await _stateLock.WaitAsync();
        try
        {
            return _processes.Select(ToStatusDto).ToList();
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task<string[]> GetRecentLogsAsync(string id)
    {
        await _stateLock.WaitAsync();
        try
        {
            var item = FindProcess(id);
            return item?.RecentLogs.ToArray() ?? Array.Empty<string>();
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task<ProcessConfigDto> CreateProcessAsync(ProcessConfigDto config)
    {
        var normalized = NormalizeConfig(config);

        await _stateLock.WaitAsync();
        try
        {
            ValidateConfig(normalized);

            var nextConfigs = _processes.Select(p => p.ToConfigDto()).ToList();
            nextConfigs.Add(normalized);
            _configStore.Save(nextConfigs);

            var item = CreateProcessItem(normalized);
            item.IsManuallyStopped = true;
            _processes.Add(item);
        }
        finally
        {
            _stateLock.Release();
        }

        await BroadcastStatusAsync();
        return normalized;
    }

    public async Task<ProcessSaveResult> UpdateProcessAsync(ProcessConfigDto config)
    {
        var normalized = NormalizeConfig(config);
        ProcessSaveResult result;

        await _stateLock.WaitAsync();
        try
        {
            var item = FindProcess(normalized.Id) ?? throw new KeyNotFoundException("未找到对应的进程配置。");
            ValidateConfig(normalized, item.Id);

            var nextConfigs = _processes.Select(p => p.ToConfigDto()).ToList();
            var index = nextConfigs.FindIndex(p => string.Equals(p.Id, item.Id, StringComparison.OrdinalIgnoreCase));
            nextConfigs[index] = normalized;
            _configStore.Save(nextConfigs);

            item.ApplyConfiguration(normalized);
            result = new ProcessSaveResult
            {
                Process = normalized,
                RestartRequired = item.IsRunning
            };
        }
        finally
        {
            _stateLock.Release();
        }

        await BroadcastStatusAsync();
        return result;
    }

    public async Task<bool> DeleteProcessAsync(string id)
    {
        var deleted = false;

        await _stateLock.WaitAsync();
        try
        {
            var item = FindProcess(id);
            if (item == null)
            {
                return false;
            }

            var nextConfigs = _processes
                .Where(p => !string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.ToConfigDto())
                .ToList();
            _configStore.Save(nextConfigs);

            StopProcessCore(item, manualStop: true);
            _processes.Remove(item);
            deleted = true;
        }
        finally
        {
            _stateLock.Release();
        }

        if (deleted)
        {
            await BroadcastStatusAsync();
        }

        return deleted;
    }

    public async Task<bool> StartProcessAsync(string id)
    {
        bool started;

        await _stateLock.WaitAsync();
        try
        {
            var item = FindProcess(id);
            if (item == null)
            {
                return false;
            }

            item.CrashHistory.Clear();
            started = StartProcessCore(item);
        }
        finally
        {
            _stateLock.Release();
        }

        await BroadcastStatusAsync();
        return started;
    }

    public async Task<bool> StopProcessAsync(string id)
    {
        var stopped = false;

        await _stateLock.WaitAsync();
        try
        {
            var item = FindProcess(id);
            if (item == null)
            {
                return false;
            }

            StopProcessCore(item, manualStop: true);
            stopped = true;
        }
        finally
        {
            _stateLock.Release();
        }

        if (stopped)
        {
            await BroadcastStatusAsync();
        }

        return stopped;
    }

    public async Task BroadcastStatusAsync()
    {
        var statusData = await GetStatusSnapshotsAsync();
        await _hubContext.Clients.All.SendAsync("ReceiveStatuses", statusData);
    }

    private ProcessItem? FindProcess(string id)
    {
        return _processes.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    private static ProcessItem CreateProcessItem(ProcessConfigDto config)
    {
        var item = new ProcessItem();
        item.ApplyConfiguration(config);
        return item;
    }

    private static ProcessStatusDto ToStatusDto(ProcessItem item)
    {
        return new ProcessStatusDto
        {
            Id = item.Id,
            Name = item.Name,
            Pid = item.Pid,
            IsRunning = item.IsRunning,
            IsManuallyStopped = item.IsManuallyStopped,
            IsCircuitBroken = item.IsCircuitBroken,
            CpuUsage = item.CpuUsage,
            MemoryMb = item.MemoryMb
        };
    }

    private static ProcessConfigDto NormalizeConfig(ProcessConfigDto config)
    {
        return new ProcessConfigDto
        {
            Id = config.Id.Trim(),
            Name = config.Name.Trim(),
            DllPath = config.DllPath.Trim(),
            Arguments = config.Arguments.Trim(),
            StartupDelayMs = config.StartupDelayMs
        };
    }

    private void ValidateConfig(ProcessConfigDto config, string? existingId = null)
    {
        if (string.IsNullOrWhiteSpace(config.Id))
        {
            throw new ArgumentException("ID 不能为空。");
        }

        if (string.IsNullOrWhiteSpace(config.Name))
        {
            throw new ArgumentException("名称不能为空。");
        }

        if (string.IsNullOrWhiteSpace(config.DllPath))
        {
            throw new ArgumentException("DLL 路径不能为空。");
        }

        if (config.StartupDelayMs < 0)
        {
            throw new ArgumentException("启动延迟不能为负数。");
        }

        var duplicated = _processes.Any(p =>
            string.Equals(p.Id, config.Id, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(p.Id, existingId, StringComparison.OrdinalIgnoreCase));

        if (duplicated)
        {
            throw new ArgumentException("ID 已存在，请使用不同的 ID。");
        }
    }

    /// <summary>
    /// 启动进程并进行启动后验证。返回 true 表示进程启动成功且仍在运行，
    /// 返回 false 表示进程秒退（启动失败）。
    /// </summary>
    private bool StartProcessCore(ProcessItem item)
    {
        try
        {
            CleanupProcessHandle(item);

            item.IsManuallyStopped = false;
            item.IsCircuitBroken = false;
            item.CpuUsage = 0;
            item.MemoryMb = 0;
            item.LastMonitorTime = default;
            item.LastTotalProcessorTime = default;

            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = string.IsNullOrWhiteSpace(item.Arguments)
                    ? item.DllPath
                    : $"{item.DllPath} {item.Arguments}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };

            var process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };

            process.OutputDataReceived += (_, e) => HandleLogOutput(item, e.Data);
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    HandleLogOutput(item, $"[ERROR] {e.Data}");
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            item.CurrentProcess = process;
            _logger.LogInformation("已启动 {ProcessName}，PID: {Pid}", item.Name, process.Id);

            // 短暂等待后检测秒退：DLL 不存在、路径错误等导致进程瞬间退出的情况
            if (process.WaitForExit(500))
            {
                var exitCode = process.ExitCode;
                _logger.LogError(
                    "{ProcessName} 启动后立即退出，ExitCode={ExitCode}，请检查 DLL 路径和启动参数。",
                    item.Name, exitCode);

                process.Dispose();
                item.CurrentProcess = null;
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            item.CurrentProcess = null;
            _logger.LogError(ex, "启动 {ProcessName} 失败。", item.Name);
            return false;
        }
    }

    private void StopProcessCore(ProcessItem item, bool manualStop)
    {
        item.IsManuallyStopped = manualStop;
        item.IsCircuitBroken = false;
        item.CpuUsage = 0;
        item.MemoryMb = 0;
        item.LastMonitorTime = default;
        item.LastTotalProcessorTime = default;

        var process = item.CurrentProcess;
        item.CurrentProcess = null;

        if (process != null)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "停止 {ProcessName} 时忽略了一个状态异常。", item.Name);
            }
            finally
            {
                process.Dispose();
            }
        }

        _logger.LogInformation(manualStop ? "已手动停止 {ProcessName}" : "已停止 {ProcessName}", item.Name);
    }

    private void CleanupProcessHandle(ProcessItem item)
    {
        if (item.CurrentProcess == null)
        {
            return;
        }

        try
        {
            if (!item.CurrentProcess.HasExited)
            {
                item.CurrentProcess.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "清理 {ProcessName} 的旧进程句柄时忽略了一个异常。", item.Name);
        }
        finally
        {
            item.CurrentProcess.Dispose();
            item.CurrentProcess = null;
        }
    }

    private void CalculateResourceUsage(ProcessItem item)
    {
        try
        {
            var process = item.CurrentProcess;
            if (process == null || process.HasExited)
            {
                return;
            }

            item.MemoryMb = Math.Round(process.WorkingSet64 / 1024.0 / 1024.0, 1);

            var currentTime = DateTime.UtcNow;
            var currentCpuTime = process.TotalProcessorTime;

            if (item.LastMonitorTime != default)
            {
                var cpuUsedMs = (currentCpuTime - item.LastTotalProcessorTime).TotalMilliseconds;
                var totalMsPassed = (currentTime - item.LastMonitorTime).TotalMilliseconds;

                if (totalMsPassed > 0)
                {
                    var cpuUsageBase = cpuUsedMs / (_processorCount * totalMsPassed);
                    item.CpuUsage = Math.Round(cpuUsageBase * 100, 1);
                }
            }

            item.LastTotalProcessorTime = currentCpuTime;
            item.LastMonitorTime = currentTime;
        }
        catch
        {
            // 忽略进程刚好退出的瞬时异常
        }
    }

    private void HandleLogOutput(ProcessItem item, string? log)
    {
        if (string.IsNullOrWhiteSpace(log))
        {
            return;
        }

        var timeLog = $"[{DateTime.Now:HH:mm:ss}] {log}";
        item.RecentLogs.Enqueue(timeLog);

        while (item.RecentLogs.Count > 100)
        {
            item.RecentLogs.TryDequeue(out _);
        }

        _ = _hubContext.Clients.Group($"log_{item.Id}").SendAsync("ReceiveLog", item.Id, timeLog);
    }

    private Task SendEmergencyAlertAsync(string processName, string reason)
    {
        _logger.LogCritical("!!! 发送告警: 进程 {ProcessName} 发生严重故障: {Reason}", processName, reason);
        return Task.CompletedTask;
    }
}
