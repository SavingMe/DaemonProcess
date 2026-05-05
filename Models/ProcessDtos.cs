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

public sealed class ProcessUpdateResultDto
{
    public string Message { get; set; } = string.Empty;
    public ProcessUpdateHistoryDto History { get; set; } = new();
}
