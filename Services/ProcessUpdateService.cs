using ProcessDaemon.Models;
using System.Diagnostics;
using System.Text.Json;

namespace ProcessDaemon.Services;

public sealed class ProcessUpdateService
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip",
        ".rar"
    };

    private readonly ProcessManager _processManager;
    private readonly IWebHostEnvironment _environment;
    private readonly IConfiguration _configuration;
    private readonly ProcessPathService _pathService;
    private readonly SevenZipService _sevenZipService;
    private readonly ILogger<ProcessUpdateService> _logger;
    private readonly SemaphoreSlim _updateLock = new(1, 1);
    private readonly SemaphoreSlim _snapshotLock = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public ProcessUpdateService(
        ProcessManager processManager,
        IWebHostEnvironment environment,
        IConfiguration configuration,
        ProcessPathService pathService,
        SevenZipService sevenZipService,
        ILogger<ProcessUpdateService> logger)
    {
        _processManager = processManager;
        _environment = environment;
        _configuration = configuration;
        _pathService = pathService;
        _sevenZipService = sevenZipService;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ProcessSnapshotDto>> GetSnapshotsAsync(string processId)
    {
        var snapshots = await LoadSnapshotsAsync();
        var currentSnapshotId = await GetCurrentSnapshotIdAsync(processId);

        return snapshots
            .Where(item => string.Equals(item.ProcessId, processId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.CreatedAt)
            .Select(item => WithCurrentFlag(item, currentSnapshotId))
            .ToList();
    }

    public async Task<ProcessUpdateResultDto> UpdateAsync(string processId, IFormFile file, string? password, CancellationToken cancellationToken)
    {
        if (file.Length <= 0)
        {
            throw new ArgumentException("更新包不能为空。");
        }

        var extension = Path.GetExtension(file.FileName);
        if (!SupportedExtensions.Contains(extension))
        {
            throw new ArgumentException("仅支持 .zip 或 .rar 更新包。");
        }

        await _updateLock.WaitAsync(cancellationToken);
        try
        {
            var config = await _processManager.GetProcessConfigAsync(processId)
                ?? throw new KeyNotFoundException("未找到对应的进程配置。");

            var status = (await _processManager.GetStatusSnapshotsAsync())
                .FirstOrDefault(item => string.Equals(item.Id, processId, StringComparison.OrdinalIgnoreCase));

            var targetDirectory = _pathService.ResolveTargetDirectory(config.DllPath);
            var snapshotId = Guid.NewGuid().ToString("N");
            var createdAt = DateTimeOffset.Now;
            var snapshotDirectory = GetSnapshotDirectory(processId, createdAt, snapshotId);
            var uploadPath = string.Empty;
            var targetMayBeChanged = false;

            var snapshot = new ProcessSnapshotDto
            {
                SnapshotId = snapshotId,
                ProcessId = config.Id,
                ProcessName = config.Name,
                TargetDirectory = targetDirectory,
                SnapshotDirectory = snapshotDirectory,
                OriginalFileName = Path.GetFileName(file.FileName),
                FileSizeBytes = file.Length,
                CreatedAt = createdAt,
                Status = "Running",
                ProcessWasRunning = status?.IsRunning == true
            };

            try
            {
                if (!Directory.Exists(targetDirectory))
                {
                    throw new DirectoryNotFoundException($"目标目录不存在：{targetDirectory}");
                }

                uploadPath = await SaveUploadAsync(file, snapshotId, cancellationToken);

                if (snapshot.ProcessWasRunning)
                {
                    await _processManager.StopProcessAsync(processId);
                }

                _pathService.CopyDirectoryFast(targetDirectory, snapshotDirectory);
                snapshot.Status = "Succeeded";
                await AppendSnapshotAsync(snapshot);

                targetMayBeChanged = true;
                await ExtractArchiveAsync(uploadPath, targetDirectory, password, cancellationToken);

                if (snapshot.ProcessWasRunning)
                {
                    snapshot.RestartSucceeded = await _processManager.StartProcessAsync(processId);
                    if (!snapshot.RestartSucceeded)
                    {
                        snapshot.Status = "RestartFailed";
                        snapshot.ErrorMessage = "更新前快照已生成，更新包已解压，但进程重启失败。";
                        return new ProcessUpdateResultDto
                        {
                            Message = snapshot.ErrorMessage,
                            Snapshot = snapshot
                        };
                    }
                }

                await ClearCurrentSnapshotIdAsync(processId);

                return new ProcessUpdateResultDto
                {
                    Message = "更新完成，已备份更新前目录。",
                    Snapshot = snapshot
                };
            }
            catch (Exception ex)
            {
                snapshot.Status = "Failed";
                snapshot.ErrorMessage = ex.Message;
                if (snapshot.ProcessWasRunning && !targetMayBeChanged)
                {
                    snapshot.RestartSucceeded = await _processManager.StartProcessAsync(processId);
                    if (!snapshot.RestartSucceeded)
                    {
                        snapshot.ErrorMessage += " 原进程恢复启动失败。";
                    }
                }

                _logger.LogError(ex, "更新 {ProcessName} 失败。", config.Name);
                return new ProcessUpdateResultDto
                {
                    Message = snapshot.ErrorMessage ?? "更新失败。",
                    Snapshot = snapshot
                };
            }
            finally
            {
                DeleteQuietly(uploadPath);
            }
        }
        finally
        {
            _updateLock.Release();
        }
    }

    public async Task<ProcessUpdateResultDto> CreateManualSnapshotAsync(string processId, CancellationToken cancellationToken)
    {
        await _updateLock.WaitAsync(cancellationToken);
        try
        {
            var config = await _processManager.GetProcessConfigAsync(processId)
                ?? throw new KeyNotFoundException("未找到对应的进程配置。");

            var status = (await _processManager.GetStatusSnapshotsAsync())
                .FirstOrDefault(item => string.Equals(item.Id, processId, StringComparison.OrdinalIgnoreCase));

            var targetDirectory = _pathService.ResolveTargetDirectory(config.DllPath);
            if (!Directory.Exists(targetDirectory))
            {
                throw new DirectoryNotFoundException($"目标目录不存在：{targetDirectory}");
            }

            var snapshotId = Guid.NewGuid().ToString("N");
            var createdAt = DateTimeOffset.Now;
            var snapshotDirectory = GetSnapshotDirectory(processId, createdAt, snapshotId);
            var snapshot = new ProcessSnapshotDto
            {
                SnapshotId = snapshotId,
                ProcessId = config.Id,
                ProcessName = config.Name,
                TargetDirectory = targetDirectory,
                SnapshotDirectory = snapshotDirectory,
                OriginalFileName = "manual-backup",
                FileSizeBytes = 0,
                CreatedAt = createdAt,
                Status = "Succeeded",
                ProcessWasRunning = status?.IsRunning == true,
                RestartSucceeded = false,
                IsCurrent = false
            };

            _pathService.CopyDirectoryFast(targetDirectory, snapshotDirectory);
            await AppendSnapshotAsync(snapshot);

            return new ProcessUpdateResultDto
            {
                Message = "手动备份已生成，不影响当前运行版本。",
                Snapshot = snapshot
            };
        }
        finally
        {
            _updateLock.Release();
        }
    }

    public async Task<ProcessRestoreResultDto> RestoreAsync(string processId, string snapshotId, CancellationToken cancellationToken)
    {
        await _updateLock.WaitAsync(cancellationToken);
        try
        {
            var config = await _processManager.GetProcessConfigAsync(processId)
                ?? throw new KeyNotFoundException("未找到对应的进程配置。");

            var snapshots = await LoadSnapshotsAsync();
            var snapshot = snapshots.FirstOrDefault(item =>
                string.Equals(item.ProcessId, processId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.SnapshotId, snapshotId, StringComparison.OrdinalIgnoreCase))
                ?? throw new KeyNotFoundException("未找到对应的快照。");

            if (!string.Equals(snapshot.Status, "Succeeded", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("只能恢复成功生成的快照。");
            }

            if (!Directory.Exists(snapshot.SnapshotDirectory))
            {
                throw new DirectoryNotFoundException($"快照目录不存在：{snapshot.SnapshotDirectory}");
            }

            var targetDirectory = _pathService.ResolveTargetDirectory(config.DllPath);
            var status = (await _processManager.GetStatusSnapshotsAsync())
                .FirstOrDefault(item => string.Equals(item.Id, processId, StringComparison.OrdinalIgnoreCase));
            var wasRunning = status?.IsRunning == true;
            var targetMayBeChanged = false;

            try
            {
                if (wasRunning)
                {
                    await _processManager.StopProcessAsync(processId);
                }

                Directory.CreateDirectory(targetDirectory);
                ProcessPathService.ClearDirectory(targetDirectory);
                targetMayBeChanged = true;
                _pathService.CopyDirectoryFast(snapshot.SnapshotDirectory, targetDirectory);

                var restartSucceeded = await _processManager.StartProcessAsync(processId);
                if (!restartSucceeded)
                {
                    var failedSnapshot = WithCurrentFlag(snapshot, await GetCurrentSnapshotIdAsync(processId));
                    failedSnapshot.RestartSucceeded = false;
                    failedSnapshot.ErrorMessage = "快照已恢复，但进程重启失败。";
                    return new ProcessRestoreResultDto
                    {
                        Message = failedSnapshot.ErrorMessage,
                        Snapshot = failedSnapshot
                    };
                }

                await SetCurrentSnapshotIdAsync(processId, snapshotId);
                var currentSnapshot = WithCurrentFlag(snapshot, snapshotId);
                currentSnapshot.RestartSucceeded = true;

                return new ProcessRestoreResultDto
                {
                    Message = "已恢复到选中的快照，并标记为当前运行版本。",
                    Snapshot = currentSnapshot
                };
            }
            catch (Exception ex)
            {
                if (wasRunning && !targetMayBeChanged)
                {
                    await _processManager.StartProcessAsync(processId);
                }

                _logger.LogError(ex, "恢复 {ProcessName} 快照 {SnapshotId} 失败。", config.Name, snapshotId);
                var failedSnapshot = WithCurrentFlag(snapshot, await GetCurrentSnapshotIdAsync(processId));
                failedSnapshot.ErrorMessage = ex.Message;
                return new ProcessRestoreResultDto
                {
                    Message = ex.Message,
                    Snapshot = failedSnapshot
                };
            }
        }
        finally
        {
            _updateLock.Release();
        }
    }

    public async Task DeleteSnapshotAsync(string processId, string snapshotId, CancellationToken cancellationToken)
    {
        await _updateLock.WaitAsync(cancellationToken);
        try
        {
            var currentSnapshotId = await GetCurrentSnapshotIdAsync(processId);
            if (string.Equals(currentSnapshotId, snapshotId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("当前运行快照不能删除。");
            }

            var snapshots = await LoadSnapshotsAsync();
            var snapshot = snapshots.FirstOrDefault(item =>
                string.Equals(item.ProcessId, processId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.SnapshotId, snapshotId, StringComparison.OrdinalIgnoreCase))
                ?? throw new KeyNotFoundException("未找到对应的快照。");

            _pathService.DeleteSnapshotDirectory(snapshot.SnapshotDirectory);
            snapshots.RemoveAll(item => string.Equals(item.SnapshotId, snapshotId, StringComparison.OrdinalIgnoreCase));
            await SaveSnapshotsAsync(snapshots);
        }
        finally
        {
            _updateLock.Release();
        }
    }

    private string GetSnapshotDirectory(string processId, DateTimeOffset timestamp, string snapshotId)
    {
        var backupRoot = _configuration["UpdatePackage:BackupRoot"];
        if (string.IsNullOrWhiteSpace(backupRoot))
        {
            backupRoot = Path.Combine(_environment.ContentRootPath, "update-backups");
        }
        else if (!Path.IsPathRooted(backupRoot))
        {
            backupRoot = Path.Combine(_environment.ContentRootPath, backupRoot);
        }

        var folderName = $"{timestamp:yyyyMMdd-HHmmss}-{snapshotId[..8]}";
        return Path.Combine(backupRoot, SanitizePathSegment(processId), folderName);
    }

    private string GetSnapshotsFilePath()
    {
        var snapshotsFile = _configuration["UpdatePackage:SnapshotsFile"];
        if (string.IsNullOrWhiteSpace(snapshotsFile))
        {
            snapshotsFile = Path.Combine(_environment.ContentRootPath, "update-snapshots.json");
        }
        else if (!Path.IsPathRooted(snapshotsFile))
        {
            snapshotsFile = Path.Combine(_environment.ContentRootPath, snapshotsFile);
        }

        return snapshotsFile;
    }

    private string GetCurrentFilePath()
    {
        var currentFile = _configuration["UpdatePackage:CurrentFile"];
        if (string.IsNullOrWhiteSpace(currentFile))
        {
            currentFile = Path.Combine(_environment.ContentRootPath, "update-current.json");
        }
        else if (!Path.IsPathRooted(currentFile))
        {
            currentFile = Path.Combine(_environment.ContentRootPath, currentFile);
        }

        return currentFile;
    }

    private async Task<string> SaveUploadAsync(IFormFile file, string snapshotId, CancellationToken cancellationToken)
    {
        var uploadRoot = Path.Combine(_environment.ContentRootPath, "update-uploads");
        Directory.CreateDirectory(uploadRoot);

        var uploadPath = Path.Combine(uploadRoot, $"{snapshotId}{Path.GetExtension(file.FileName)}");
        await using var stream = File.Create(uploadPath);
        await file.CopyToAsync(stream, cancellationToken);
        return uploadPath;
    }

    private async Task ExtractArchiveAsync(string archivePath, string targetDirectory, string? password, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(targetDirectory);

        var sevenZipPath = await _sevenZipService.GetExecutablePathAsync(cancellationToken);

        var processStartInfo = new ProcessStartInfo
        {
            FileName = sevenZipPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };

        processStartInfo.ArgumentList.Add("x");
        processStartInfo.ArgumentList.Add(archivePath);
        processStartInfo.ArgumentList.Add("-y");
        processStartInfo.ArgumentList.Add($"-o{targetDirectory}");
        if (!string.IsNullOrWhiteSpace(password))
        {
            processStartInfo.ArgumentList.Add($"-p{password}");
        }

        try
        {
            using var process = Process.Start(processStartInfo)
                ?? throw new InvalidOperationException("无法启动 7-Zip。");

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(cancellationToken);

            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0)
            {
                var message = string.IsNullOrWhiteSpace(error) ? output : error;
                throw new InvalidOperationException($"7-Zip 解压失败：{message.Trim()}");
            }
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException($"未找到 7-Zip，请配置 UpdatePackage:SevenZipPath。{ex.Message}", ex);
        }
    }

    private async Task<List<ProcessSnapshotDto>> LoadSnapshotsAsync()
    {
        await _snapshotLock.WaitAsync();
        try
        {
            var snapshotsFile = GetSnapshotsFilePath();
            if (!File.Exists(snapshotsFile))
            {
                return new List<ProcessSnapshotDto>();
            }

            var json = await File.ReadAllTextAsync(snapshotsFile);
            return JsonSerializer.Deserialize<List<ProcessSnapshotDto>>(json, _jsonOptions)
                ?? new List<ProcessSnapshotDto>();
        }
        finally
        {
            _snapshotLock.Release();
        }
    }

    private async Task AppendSnapshotAsync(ProcessSnapshotDto item)
    {
        var snapshots = await LoadSnapshotsAsync();
        snapshots.Add(item);
        await SaveSnapshotsAsync(snapshots);
    }

    private async Task SaveSnapshotsAsync(List<ProcessSnapshotDto> snapshots)
    {
        await _snapshotLock.WaitAsync();
        try
        {
            var snapshotsFile = GetSnapshotsFilePath();
            var snapshotsDirectory = Path.GetDirectoryName(snapshotsFile);
            if (!string.IsNullOrWhiteSpace(snapshotsDirectory))
            {
                Directory.CreateDirectory(snapshotsDirectory);
            }

            var nextJson = JsonSerializer.Serialize(snapshots, _jsonOptions);
            await File.WriteAllTextAsync(snapshotsFile, nextJson);
        }
        finally
        {
            _snapshotLock.Release();
        }
    }

    private async Task<string?> GetCurrentSnapshotIdAsync(string processId)
    {
        var current = await LoadCurrentSnapshotsAsync();
        return current.TryGetValue(processId, out var snapshotId) ? snapshotId : null;
    }

    private async Task SetCurrentSnapshotIdAsync(string processId, string snapshotId)
    {
        var current = await LoadCurrentSnapshotsAsync();
        current[processId] = snapshotId;
        await SaveCurrentSnapshotsAsync(current);
    }

    private async Task ClearCurrentSnapshotIdAsync(string processId)
    {
        var current = await LoadCurrentSnapshotsAsync();
        if (current.Remove(processId))
        {
            await SaveCurrentSnapshotsAsync(current);
        }
    }

    private async Task<Dictionary<string, string>> LoadCurrentSnapshotsAsync()
    {
        await _snapshotLock.WaitAsync();
        try
        {
            var currentFile = GetCurrentFilePath();
            if (!File.Exists(currentFile))
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            var json = await File.ReadAllTextAsync(currentFile);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json, _jsonOptions)
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            _snapshotLock.Release();
        }
    }

    private async Task SaveCurrentSnapshotsAsync(Dictionary<string, string> current)
    {
        await _snapshotLock.WaitAsync();
        try
        {
            var currentFile = GetCurrentFilePath();
            var currentDirectory = Path.GetDirectoryName(currentFile);
            if (!string.IsNullOrWhiteSpace(currentDirectory))
            {
                Directory.CreateDirectory(currentDirectory);
            }

            var json = JsonSerializer.Serialize(current, _jsonOptions);
            await File.WriteAllTextAsync(currentFile, json);
        }
        finally
        {
            _snapshotLock.Release();
        }
    }

    private static ProcessSnapshotDto WithCurrentFlag(ProcessSnapshotDto item, string? currentSnapshotId)
    {
        return new ProcessSnapshotDto
        {
            SnapshotId = item.SnapshotId,
            ProcessId = item.ProcessId,
            ProcessName = item.ProcessName,
            TargetDirectory = item.TargetDirectory,
            SnapshotDirectory = item.SnapshotDirectory,
            OriginalFileName = item.OriginalFileName,
            FileSizeBytes = item.FileSizeBytes,
            CreatedAt = item.CreatedAt,
            Status = item.Status,
            ErrorMessage = item.ErrorMessage,
            ProcessWasRunning = item.ProcessWasRunning,
            RestartSucceeded = item.RestartSucceeded,
            IsCurrent = string.Equals(item.SnapshotId, currentSnapshotId, StringComparison.OrdinalIgnoreCase)
        };
    }

    private static string SanitizePathSegment(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(ch => invalidChars.Contains(ch) ? '_' : ch));
    }

    private static void DeleteQuietly(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort cleanup for transient upload packages.
        }
    }
}
