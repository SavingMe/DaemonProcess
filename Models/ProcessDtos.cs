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
