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
    private readonly ILogger<ProcessUpdateService> _logger;
    private readonly SemaphoreSlim _updateLock = new(1, 1);
    private readonly SemaphoreSlim _historyLock = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public ProcessUpdateService(
        ProcessManager processManager,
        IWebHostEnvironment environment,
        IConfiguration configuration,
        ILogger<ProcessUpdateService> logger)
    {
        _processManager = processManager;
        _environment = environment;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ProcessUpdateHistoryDto>> GetHistoryAsync(string processId)
    {
        var history = await LoadHistoryAsync();
        return history
            .Where(item => string.Equals(item.ProcessId, processId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.StartedAt)
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

            var targetDirectory = ResolveTargetDirectory(config.DllPath);
            var startedAt = DateTimeOffset.Now;
            var backupDirectory = GetBackupDirectory(processId, startedAt);
            var history = new ProcessUpdateHistoryDto
            {
                UpdateId = Guid.NewGuid().ToString("N"),
                ProcessId = config.Id,
                ProcessName = config.Name,
                TargetDirectory = targetDirectory,
                OriginalFileName = Path.GetFileName(file.FileName),
                FileSizeBytes = file.Length,
                BackupDirectory = backupDirectory,
                StartedAt = startedAt,
                Status = "Running",
                ProcessWasRunning = status?.IsRunning == true
            };

            await AppendOrUpdateHistoryAsync(history);

            var uploadPath = string.Empty;
            var targetMayBeChanged = false;
            try
            {
                if (!Directory.Exists(targetDirectory))
                {
                    throw new DirectoryNotFoundException($"目标目录不存在：{targetDirectory}");
                }

                uploadPath = await SaveUploadAsync(file, history.UpdateId, cancellationToken);

                if (history.ProcessWasRunning)
                {
                    await _processManager.StopProcessAsync(processId);
                }

                CopyDirectory(targetDirectory, backupDirectory);
                targetMayBeChanged = true;
                await ExtractArchiveAsync(uploadPath, targetDirectory, password, cancellationToken);

                history.RestartSucceeded = await _processManager.StartProcessAsync(processId);
                history.Status = history.RestartSucceeded ? "Succeeded" : "RestartFailed";
                history.ErrorMessage = history.RestartSucceeded ? null : "更新包已解压，但进程重启失败。";
            }
            catch (Exception ex)
            {
                history.Status = history.Status == "Running" ? "Failed" : history.Status;
                history.ErrorMessage = ex.Message;
                if (history.ProcessWasRunning && !targetMayBeChanged)
                {
                    history.RestartSucceeded = await _processManager.StartProcessAsync(processId);
                    if (!history.RestartSucceeded)
                    {
                        history.ErrorMessage += " 原进程恢复启动失败。";
                    }
                }

                _logger.LogError(ex, "更新 {ProcessName} 失败。", config.Name);
            }
            finally
            {
                history.FinishedAt = DateTimeOffset.Now;
                await AppendOrUpdateHistoryAsync(history);
                DeleteQuietly(uploadPath);
            }

            if (history.Status is "Failed" or "RestartFailed")
            {
                return new ProcessUpdateResultDto
                {
                    Message = history.ErrorMessage ?? "更新失败。",
                    History = history
                };
            }

            return new ProcessUpdateResultDto
            {
                Message = "更新完成，进程已重启。",
                History = history
            };
        }
        finally
        {
            _updateLock.Release();
        }
    }

    private string ResolveTargetDirectory(string dllPath)
    {
        var normalizedPath = dllPath.Trim();
        if (!Path.IsPathRooted(normalizedPath))
        {
            normalizedPath = Path.Combine(_environment.ContentRootPath, normalizedPath);
        }

        return Path.GetDirectoryName(Path.GetFullPath(normalizedPath)) ?? _environment.ContentRootPath;
    }

    private string GetBackupDirectory(string processId, DateTimeOffset timestamp)
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

        return Path.Combine(backupRoot, SanitizePathSegment(processId), timestamp.ToString("yyyyMMdd-HHmmss"));
    }

    private string GetHistoryFilePath()
    {
        var historyFile = _configuration["UpdatePackage:HistoryFile"];
        if (string.IsNullOrWhiteSpace(historyFile))
        {
            historyFile = Path.Combine(_environment.ContentRootPath, "update-history.json");
        }
        else if (!Path.IsPathRooted(historyFile))
        {
            historyFile = Path.Combine(_environment.ContentRootPath, historyFile);
        }

        return historyFile;
    }

    private async Task<string> SaveUploadAsync(IFormFile file, string updateId, CancellationToken cancellationToken)
    {
        var uploadRoot = Path.Combine(_environment.ContentRootPath, "update-uploads");
        Directory.CreateDirectory(uploadRoot);

        var uploadPath = Path.Combine(uploadRoot, $"{updateId}{Path.GetExtension(file.FileName)}");
        await using var stream = File.Create(uploadPath);
        await file.CopyToAsync(stream, cancellationToken);
        return uploadPath;
    }

    private async Task ExtractArchiveAsync(string archivePath, string targetDirectory, string? password, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(targetDirectory);

        var sevenZipPath = _configuration["UpdatePackage:SevenZipPath"];
        if (string.IsNullOrWhiteSpace(sevenZipPath))
        {
            sevenZipPath = "7z";
        }

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

    private static void CopyDirectory(string sourceDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);

        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(Path.Combine(targetDirectory, relativePath));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, file);
            var targetFile = Path.Combine(targetDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            File.Copy(file, targetFile, overwrite: true);
        }
    }

    private async Task<List<ProcessUpdateHistoryDto>> LoadHistoryAsync()
    {
        await _historyLock.WaitAsync();
        try
        {
            var historyFile = GetHistoryFilePath();
            if (!File.Exists(historyFile))
            {
                return new List<ProcessUpdateHistoryDto>();
            }

            var json = await File.ReadAllTextAsync(historyFile);
            return JsonSerializer.Deserialize<List<ProcessUpdateHistoryDto>>(json, _jsonOptions)
                ?? new List<ProcessUpdateHistoryDto>();
        }
        finally
        {
            _historyLock.Release();
        }
    }

    private async Task AppendOrUpdateHistoryAsync(ProcessUpdateHistoryDto item)
    {
        await _historyLock.WaitAsync();
        try
        {
            var historyFile = GetHistoryFilePath();
            var historyDirectory = Path.GetDirectoryName(historyFile);
            if (!string.IsNullOrWhiteSpace(historyDirectory))
            {
                Directory.CreateDirectory(historyDirectory);
            }

            var history = new List<ProcessUpdateHistoryDto>();
            if (File.Exists(historyFile))
            {
                var json = await File.ReadAllTextAsync(historyFile);
                history = JsonSerializer.Deserialize<List<ProcessUpdateHistoryDto>>(json, _jsonOptions)
                    ?? new List<ProcessUpdateHistoryDto>();
            }

            var index = history.FindIndex(entry => entry.UpdateId == item.UpdateId);
            if (index >= 0)
            {
                history[index] = item;
            }
            else
            {
                history.Add(item);
            }

            var nextJson = JsonSerializer.Serialize(history, _jsonOptions);
            await File.WriteAllTextAsync(historyFile, nextJson);
        }
        finally
        {
            _historyLock.Release();
        }
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
