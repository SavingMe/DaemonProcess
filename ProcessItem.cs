using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Serialization;

namespace ProcessDaemon.Models;

public class ProcessItem
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string DllPath { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public int StartupDelayMs { get; set; } = 5000;

    [JsonIgnore] public bool IsManuallyStopped { get; set; }
    [JsonIgnore] public bool IsCircuitBroken { get; set; }

    [JsonIgnore]
    public bool IsRunning
    {
        get
        {
            if (CurrentProcess == null)
            {
                return false;
            }

            try
            {
                return !CurrentProcess.HasExited;
            }
            catch
            {
                return false;
            }
        }
    }

    [JsonIgnore] public int? Pid => IsRunning ? CurrentProcess?.Id : null;
    [JsonIgnore] public Process? CurrentProcess { get; set; }

    [JsonIgnore] public double CpuUsage { get; set; }
    [JsonIgnore] public double MemoryMb { get; set; }
    [JsonIgnore] public TimeSpan LastTotalProcessorTime { get; set; }
    [JsonIgnore] public DateTime LastMonitorTime { get; set; }

    [JsonIgnore] public List<DateTime> CrashHistory { get; set; } = new();
    [JsonIgnore] public ConcurrentQueue<string> RecentLogs { get; set; } = new();

    public void ApplyConfiguration(ProcessConfigDto config)
    {
        Id = config.Id;
        Name = config.Name;
        DllPath = config.DllPath;
        Arguments = config.Arguments;
        StartupDelayMs = config.StartupDelayMs;
    }

    public ProcessConfigDto ToConfigDto()
    {
        return new ProcessConfigDto
        {
            Id = Id,
            Name = Name,
            DllPath = DllPath,
            Arguments = Arguments,
            StartupDelayMs = StartupDelayMs
        };
    }
}
