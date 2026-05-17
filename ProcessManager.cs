using Microsoft.AspNetCore.SignalR;
using ProcessDaemon.Hubs;
using ProcessDaemon.Models;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace ProcessDaemon.Services;

public class ProcessManager
{
    private readonly IHubContext<MonitorHub> _hubContext;
    private readonly ILogger<ProcessManager> _logger;
    private readonly ProcessConfigStore _configStore;
    private readonly ProcessPathService _pathService;
    private readonly ProcessLogService _logService;
    private readonly IConfiguration _configuration;
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private readonly int _processorCount = Environment.ProcessorCount;
    private readonly List<ProcessItem> _processes;

    public ProcessManager(
        IHubContext<MonitorHub> hubContext,
        ProcessConfigStore configStore,
        ProcessPathService pathService,
        ProcessLogService logService,
        IConfiguration configuration,
        ILogger<ProcessManager> logger)
    {
        _hubContext = hubContext;
        _configStore = configStore;
        _pathService = pathService;
        _logService = logService;
        _configuration = configuration;
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

    public async Task<ProcessConfigDto?> GetProcessConfigAsync(string id)
    {
        await _stateLock.WaitAsync();
        try
        {
            return FindProcess(id)?.ToConfigDto();
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
            _pathService.EnsureTargetDirectoryReady(normalized.DllPath);

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
            var targetDirectoryChanged = !_pathService.IsSameTargetDirectory(item.DllPath, normalized.DllPath);
            if (targetDirectoryChanged && item.IsRunning)
            {
                throw new ArgumentException("进程运行中不能修改部署目录，请先停止进程。");
            }

            _pathService.EnsureTargetDirectoryReady(normalized.DllPath);

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
            StopProcessCore(item, manualStop: true);
            _pathService.DeleteTargetDirectory(item.DllPath);
            _configStore.Save(nextConfigs);
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
            throw new ArgumentException("程序路径不能为空。");
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

            var entryPath = _pathService.ResolveEntryPath(item.DllPath);
            if (!File.Exists(entryPath))
            {
                throw new FileNotFoundException($"程序入口文件不存在：{entryPath}", entryPath);
            }

            var entryDirectory = Path.GetDirectoryName(entryPath) ?? Directory.GetCurrentDirectory();
            var useDotnetHost = IsDllEntry(entryPath);
            var dotnetPath = GetDotnetExecutablePath();

            if (!useDotnetHost)
            {
                TrySetLinuxExecutableMode(entryPath);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = useDotnetHost ? dotnetPath : entryPath,
                WorkingDirectory = entryDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            if (useDotnetHost)
            {
                startInfo.ArgumentList.Add(entryPath);
            }

            foreach (var argument in SplitCommandLineArguments(item.Arguments))
            {
                startInfo.ArgumentList.Add(argument);
            }

            var process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };

            try
            {
                process.Start();
            }
            catch (Win32Exception ex) when (useDotnetHost)
            {
                process.Dispose();
                throw new InvalidOperationException(
                    $"启动 {item.Name} 失败：未找到 dotnet 可执行文件 \"{dotnetPath}\"。请在 Linux 安装 .NET Runtime，或将被守护程序发布为 Self-contained + SingleFile 后直接配置可执行文件路径。",
                    ex);
            }
            catch (Win32Exception ex)
            {
                process.Dispose();
                throw new InvalidOperationException(
                    $"启动 {item.Name} 失败：无法执行程序入口文件 \"{entryPath}\"。请确认文件适用于当前 Linux 架构并具有执行权限。",
                    ex);
            }

            _ = ReadProcessOutputAsync(item, process.StandardOutput.BaseStream, "stdout", "Info");
            _ = ReadProcessOutputAsync(item, process.StandardError.BaseStream, "stderr", "Error");

            item.CurrentProcess = process;
            _logger.LogInformation(
                "已启动 {ProcessName}，PID: {Pid}，启动方式: {Runner}，工作目录: {WorkingDirectory}",
                item.Name,
                process.Id,
                useDotnetHost ? $"{dotnetPath} {entryPath}" : entryPath,
                entryDirectory);

            // 短暂等待后检测秒退：入口路径、参数或运行时依赖错误通常会快速退出。
            if (process.WaitForExit(500))
            {
                var exitCode = process.ExitCode;
                _logger.LogError(
                    "{ProcessName} 启动后立即退出，ExitCode={ExitCode}，请检查程序路径、启动参数和运行时依赖。",
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

    private string GetDotnetExecutablePath()
    {
        var dotnetPath = _configuration["ProcessRunner:DotnetPath"];
        return string.IsNullOrWhiteSpace(dotnetPath) ? "dotnet" : dotnetPath.Trim();
    }

    private void TrySetLinuxExecutableMode(string entryPath)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        try
        {
            var chmodStartInfo = new ProcessStartInfo
            {
                FileName = "chmod",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            chmodStartInfo.ArgumentList.Add("+x");
            chmodStartInfo.ArgumentList.Add(entryPath);

            using var chmod = Process.Start(chmodStartInfo);
            if (chmod == null)
            {
                _logger.LogWarning("无法启动 chmod，跳过设置执行权限：{EntryPath}", entryPath);
                return;
            }

            chmod.WaitForExit();
            if (chmod.ExitCode != 0)
            {
                var error = chmod.StandardError.ReadToEnd();
                _logger.LogWarning("chmod +x {EntryPath} 失败：{Error}", entryPath, error.Trim());
            }
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "设置程序入口执行权限失败，将继续尝试启动：{EntryPath}", entryPath);
        }
    }

    private static bool IsDllEntry(string entryPath)
    {
        return string.Equals(Path.GetExtension(entryPath), ".dll", StringComparison.OrdinalIgnoreCase);
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

    private async Task ReadProcessOutputAsync(ProcessItem item, Stream stream, string streamName, string level)
    {
        var buffer = new byte[4096];
        var line = new List<byte>(1024);

        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length));
                if (read == 0)
                {
                    break;
                }

                for (var index = 0; index < read; index++)
                {
                    var value = buffer[index];
                    if (value == (byte)'\n')
                    {
                        EmitLogLine(item, line, streamName, level);
                        line.Clear();
                        continue;
                    }

                    line.Add(value);
                }
            }

            EmitLogLine(item, line, streamName, level);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "读取 {ProcessName} 的 {StreamName} 日志流时结束。", item.Name, streamName);
        }
    }

    private void EmitLogLine(ProcessItem item, List<byte> line, string streamName, string level)
    {
        if (line.Count == 0)
        {
            return;
        }

        if (line[^1] == (byte)'\r')
        {
            line.RemoveAt(line.Count - 1);
        }

        if (line.Count == 0)
        {
            return;
        }

        HandleLogOutput(item, DecodeProcessOutput(line.ToArray()), streamName, level);
    }

    private void HandleLogOutput(ProcessItem item, string? log, string stream, string level)
    {
        if (string.IsNullOrWhiteSpace(log))
        {
            return;
        }

        var timestamp = DateTimeOffset.Now;
        var displayMessage = string.Equals(level, "Error", StringComparison.OrdinalIgnoreCase)
            ? $"[ERROR] {log}"
            : log;
        var timeLog = $"[{timestamp:HH:mm:ss}] {displayMessage}";
        item.RecentLogs.Enqueue(timeLog);
        _logService.Enqueue(new ProcessLogEntryDto
        {
            ProcessId = item.Id,
            ProcessName = item.Name,
            Timestamp = timestamp,
            Stream = stream,
            Level = level,
            Message = log,
            RawLine = timeLog
        });

        while (item.RecentLogs.Count > 100)
        {
            item.RecentLogs.TryDequeue(out _);
        }

        _ = _hubContext.Clients.Group($"log_{item.Id}").SendAsync("ReceiveLog", item.Id, timeLog);
    }

    private string DecodeProcessOutput(byte[] bytes)
    {
        var mode = (_configuration["ProcessOutput:Encoding"] ?? "Auto").Trim();
        if (mode.Equals("GBK", StringComparison.OrdinalIgnoreCase) ||
            mode.Equals("GB2312", StringComparison.OrdinalIgnoreCase) ||
            mode.Equals("936", StringComparison.OrdinalIgnoreCase))
        {
            return GetGbkEncoding().GetString(bytes);
        }

        if (mode.Equals("UTF-8", StringComparison.OrdinalIgnoreCase) ||
            mode.Equals("UTF8", StringComparison.OrdinalIgnoreCase) ||
            mode.Equals("65001", StringComparison.OrdinalIgnoreCase))
        {
            return Encoding.UTF8.GetString(bytes);
        }

        return DecodeProcessOutputAuto(bytes);
    }

    private static string DecodeProcessOutputAuto(byte[] bytes)
    {
        var candidates = new List<string>();
        if (TryDecodeUtf8Strict(bytes, out var utf8Text))
        {
            candidates.Add(utf8Text);
            if (TryRepairUtf8DecodedAsGbk(utf8Text, out var repaired))
            {
                candidates.Add(repaired);
            }
        }

        try
        {
            var gbkText = GetGbkEncoding().GetString(bytes);
            candidates.Add(gbkText);
        }
        catch (DecoderFallbackException)
        {
            // Ignore invalid GBK candidate.
        }

        if (candidates.Count == 0)
        {
            return Encoding.UTF8.GetString(bytes);
        }

        return candidates
            .OrderBy(GetMojibakeScore)
            .ThenByDescending(text => text.Count(IsCjkUnifiedIdeograph))
            .First();
    }

    private static bool TryDecodeUtf8Strict(byte[] bytes, out string text)
    {
        try
        {
            text = new UTF8Encoding(false, true).GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
    }

    private static bool TryRepairUtf8DecodedAsGbk(string text, out string repaired)
    {
        try
        {
            var bytes = GetGbkEncoding().GetBytes(text);
            return TryDecodeUtf8Strict(bytes, out repaired);
        }
        catch (EncoderFallbackException)
        {
            repaired = string.Empty;
            return false;
        }
    }

    private static Encoding GetGbkEncoding()
    {
        return Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    private static int GetMojibakeScore(string text)
    {
        const string suspiciousChars = "宸插惎惧姝ゅ崟鎺愭枃浠堕噺鍙傛暟鏈嶅姟娴嬭瘯缂栫爜涓嶅彲";

        var score = 0;
        foreach (var ch in text)
        {
            if (ch == '\uFFFD')
            {
                score += 30;
            }
            else if (char.IsControl(ch) && ch is not '\t')
            {
                score += 20;
            }
            else if (suspiciousChars.Contains(ch))
            {
                score += 6;
            }
            else if (ch is 'Ã' or 'Â' or '¤' or '€')
            {
                score += 6;
            }
        }

        if (text.Contains("$\\", StringComparison.Ordinal) || text.Contains("\\x", StringComparison.OrdinalIgnoreCase))
        {
            score += 20;
        }

        return score;
    }

    private static bool IsCjkUnifiedIdeograph(char ch)
    {
        return ch >= '\u4E00' && ch <= '\u9FFF';
    }

    private static IEnumerable<string> SplitCommandLineArguments(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            yield break;
        }

        var current = new System.Text.StringBuilder();
        var quoteChar = '\0';

        for (var index = 0; index < arguments.Length; index++)
        {
            var ch = arguments[index];

            if ((ch == '"' || ch == '\'') && quoteChar == '\0')
            {
                quoteChar = ch;
                continue;
            }

            if (ch == quoteChar)
            {
                quoteChar = '\0';
                continue;
            }

            if (char.IsWhiteSpace(ch) && quoteChar == '\0')
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }

                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    private Task SendEmergencyAlertAsync(string processName, string reason)
    {
        _logger.LogCritical("!!! 发送告警: 进程 {ProcessName} 发生严重故障: {Reason}", processName, reason);
        return Task.CompletedTask;
    }
}
