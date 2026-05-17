namespace ProcessDaemon.Models;

public sealed class ProcessConfigDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string DllPath { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public int StartupDelayMs { get; set; } = 5000;
}

public sealed class ProcessStatusDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int? Pid { get; set; }
    public bool IsRunning { get; set; }
    public bool IsManuallyStopped { get; set; }
    public bool IsCircuitBroken { get; set; }
    public double CpuUsage { get; set; }
    public double MemoryMb { get; set; }
}

public sealed class ProcessSaveResult
{
    public ProcessConfigDto Process { get; set; } = new();
    public bool RestartRequired { get; set; }
}

public sealed class ProcessUpdateHistoryDto
{
    public string UpdateId { get; set; } = string.Empty;
    public string ProcessId { get; set; } = string.Empty;
    public string ProcessName { get; set; } = string.Empty;
    public string TargetDirectory { get; set; } = string.Empty;
    public string OriginalFileName { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public string BackupDirectory { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public bool ProcessWasRunning { get; set; }
    public bool RestartSucceeded { get; set; }
}

public sealed class ProcessUpdateCleanupResult
{
    public int SnapshotRecordsDeleted { get; set; }
    public int CurrentSnapshotRecordsDeleted { get; set; }
    public int HistoryRecordsDeleted { get; set; }
    public int BackupDirectoriesDeleted { get; set; }
    public int UploadFilesDeleted { get; set; }
}

public sealed class ProcessSnapshotRequestDto
{
    public string Remark { get; set; } = string.Empty;
}

public sealed class ProcessUpdateResultDto
{
    public string Message { get; set; } = string.Empty;
    public ProcessSnapshotDto Snapshot { get; set; } = new();
}

public sealed class ProcessSnapshotDto
{
    public string SnapshotId { get; set; } = string.Empty;
    public string ProcessId { get; set; } = string.Empty;
    public string ProcessName { get; set; } = string.Empty;
    public string TargetDirectory { get; set; } = string.Empty;
    public string SnapshotDirectory { get; set; } = string.Empty;
    public string OriginalFileName { get; set; } = string.Empty;
    public string Remark { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public bool ProcessWasRunning { get; set; }
    public bool RestartSucceeded { get; set; }
    public bool IsCurrent { get; set; }
}

public sealed class ProcessRestoreResultDto
{
    public string Message { get; set; } = string.Empty;
    public ProcessSnapshotDto Snapshot { get; set; } = new();
}

public sealed class SevenZipStatusDto
{
    public bool Installed { get; set; }
    public string Source { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public bool PackageFound { get; set; }
    public string Message { get; set; } = string.Empty;
}

public sealed class ProcessLogEntryDto
{
    public long Id { get; set; }
    public string ProcessId { get; set; } = string.Empty;
    public string ProcessName { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; }
    public string Stream { get; set; } = string.Empty;
    public string Level { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string RawLine { get; set; } = string.Empty;
}

public sealed class ProcessLogQueryResultDto
{
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int Total { get; set; }
    public IReadOnlyList<ProcessLogEntryDto> Items { get; set; } = Array.Empty<ProcessLogEntryDto>();
}
