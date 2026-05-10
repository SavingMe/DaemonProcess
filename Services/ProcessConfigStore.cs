using ProcessDaemon.Models;
using System.Text.Json;

namespace ProcessDaemon.Services;

public class ProcessConfigStore
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<ProcessConfigStore> _logger;
    private readonly string _filePath;
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public ProcessConfigStore(IConfiguration configuration, IWebHostEnvironment environment, ILogger<ProcessConfigStore> logger)
    {
        _configuration = configuration;
        _logger = logger;
        _filePath = Path.Combine(environment.ContentRootPath, "processes.json");
    }

    public List<ProcessConfigDto> Load()
    {
        if (File.Exists(_filePath))
        {
            try
            {
                var json = File.ReadAllText(_filePath);
                var stored = JsonSerializer.Deserialize<List<ProcessConfigDto>>(json, _serializerOptions);
                return stored ?? new List<ProcessConfigDto>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "读取 {FilePath} 失败，将回退到 appsettings 配置。", _filePath);
            }
        }

        var fallback = _configuration.GetSection("ProcessConfig").Get<List<ProcessConfigDto>>() ?? new List<ProcessConfigDto>();

        try
        {
            Save(fallback);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "初始化运行时配置文件失败，服务将继续使用默认配置。");
        }

        return fallback;
    }

    public void Save(IReadOnlyCollection<ProcessConfigDto> configs)
    {
        var json = JsonSerializer.Serialize(configs, _serializerOptions);
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = Path.Combine(
            directory ?? Directory.GetCurrentDirectory(),
            $"{Path.GetFileName(_filePath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
}
