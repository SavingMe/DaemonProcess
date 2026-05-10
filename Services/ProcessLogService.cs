using Microsoft.Data.Sqlite;
using ProcessDaemon.Models;
using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace ProcessDaemon.Services;

public sealed class ProcessLogService : BackgroundService
{
    private readonly IWebHostEnvironment _environment;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ProcessLogService> _logger;
    private readonly Channel<ProcessLogEntryDto> _channel = Channel.CreateBounded<ProcessLogEntryDto>(new BoundedChannelOptions(10000)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false
    });
    private readonly SemaphoreSlim _databaseLock = new(1, 1);
    private readonly string _databasePath;

    public ProcessLogService(IWebHostEnvironment environment, IConfiguration configuration, ILogger<ProcessLogService> logger)
    {
        _environment = environment;
        _configuration = configuration;
        _logger = logger;
        _databasePath = Path.Combine(_environment.ContentRootPath, "data", "process-logs.db");
    }

    public void Enqueue(ProcessLogEntryDto entry)
    {
        if (!_channel.Writer.TryWrite(entry))
        {
            _logger.LogWarning("进程日志写入队列已满，丢弃 {ProcessId} 的一条日志。", entry.ProcessId);
        }
    }

    public async Task<ProcessLogQueryResultDto> QueryAsync(
        string processId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? level,
        string? keyword,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        await EnsureDatabaseAsync(cancellationToken);

        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 500);
        var offset = (page - 1) * pageSize;

        await _databaseLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);

            var (whereClause, parameters) = BuildLogFilter(processId, from, to, level, keyword);
            var total = await CountAsync(connection, whereClause, parameters, cancellationToken);
            var items = await QueryItemsAsync(connection, whereClause, parameters, pageSize, offset, cancellationToken);

            return new ProcessLogQueryResultDto
            {
                Page = page,
                PageSize = pageSize,
                Total = total,
                Items = items
            };
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    public async Task ExportAsync(
        string processId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? level,
        string? keyword,
        Stream output,
        CancellationToken cancellationToken)
    {
        await FlushPendingAsync(cancellationToken);
        await EnsureDatabaseAsync(cancellationToken);

        await _databaseLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);

            var (whereClause, parameters) = BuildLogFilter(processId, from, to, level, keyword);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT timestamp, stream, level, message
                FROM process_logs
                WHERE {whereClause}
                ORDER BY timestamp ASC, id ASC;
                """;
            AddParameters(command, parameters);

            await using var writer = new StreamWriter(output, new UTF8Encoding(false), 16 * 1024, leaveOpen: true);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var timestamp = DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                var stream = reader.GetString(1);
                var logLevel = reader.GetString(2);
                var message = reader.GetString(3);
                await writer.WriteLineAsync($"[{timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss}] [{logLevel}] [{stream}] {message}");
            }

            await writer.FlushAsync();
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    public async Task<int> DeleteProcessLogsAsync(string processId, CancellationToken cancellationToken)
    {
        await FlushPendingAsync(cancellationToken);
        await EnsureDatabaseAsync(cancellationToken);

        await _databaseLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM process_logs WHERE process_id = $processId;";
            command.Parameters.AddWithValue("$processId", processId);
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    private async Task FlushPendingAsync(CancellationToken cancellationToken)
    {
        var batch = new List<ProcessLogEntryDto>(1000);
        while (_channel.Reader.TryRead(out var item))
        {
            batch.Add(item);
            if (batch.Count >= 1000)
            {
                await WriteBatchAsync(batch, cancellationToken);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await WriteBatchAsync(batch, cancellationToken);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await EnsureDatabaseAsync(stoppingToken);
        await CleanupOldLogsAsync(stoppingToken);

        var batch = new List<ProcessLogEntryDto>(100);
        var lastCleanup = DateTimeOffset.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var item = await _channel.Reader.ReadAsync(stoppingToken);
                batch.Add(item);

                while (batch.Count < 100 && _channel.Reader.TryRead(out var next))
                {
                    batch.Add(next);
                }

                await WriteBatchAsync(batch, stoppingToken);
                batch.Clear();

                if (DateTimeOffset.UtcNow - lastCleanup > TimeSpan.FromDays(1))
                {
                    await CleanupOldLogsAsync(stoppingToken);
                    lastCleanup = DateTimeOffset.UtcNow;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "写入进程日志失败。");
                batch.Clear();
                await Task.Delay(1000, stoppingToken);
            }
        }

        while (_channel.Reader.TryRead(out var remaining))
        {
            batch.Add(remaining);
        }

        if (batch.Count > 0)
        {
            await WriteBatchAsync(batch, CancellationToken.None);
        }
    }

    private async Task EnsureDatabaseAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);

        await _databaseLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);

            await ExecuteNonQueryAsync(connection, """
                CREATE TABLE IF NOT EXISTS process_logs (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    process_id TEXT NOT NULL,
                    process_name TEXT NOT NULL,
                    timestamp TEXT NOT NULL,
                    stream TEXT NOT NULL,
                    level TEXT NOT NULL,
                    message TEXT NOT NULL,
                    raw_line TEXT NOT NULL
                );
                """, cancellationToken);

            await ExecuteNonQueryAsync(connection, "CREATE INDEX IF NOT EXISTS idx_process_logs_process_time ON process_logs(process_id, timestamp);", cancellationToken);
            await ExecuteNonQueryAsync(connection, "CREATE INDEX IF NOT EXISTS idx_process_logs_level_time ON process_logs(level, timestamp);", cancellationToken);
            await ExecuteNonQueryAsync(connection, "CREATE INDEX IF NOT EXISTS idx_process_logs_message ON process_logs(message);", cancellationToken);
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    private async Task WriteBatchAsync(List<ProcessLogEntryDto> batch, CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
        {
            return;
        }

        await _databaseLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            foreach (var item in batch)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = """
                    INSERT INTO process_logs (process_id, process_name, timestamp, stream, level, message, raw_line)
                    VALUES ($processId, $processName, $timestamp, $stream, $level, $message, $rawLine);
                    """;
                command.Parameters.AddWithValue("$processId", item.ProcessId);
                command.Parameters.AddWithValue("$processName", item.ProcessName);
                command.Parameters.AddWithValue("$timestamp", FormatTimestamp(item.Timestamp));
                command.Parameters.AddWithValue("$stream", item.Stream);
                command.Parameters.AddWithValue("$level", item.Level);
                command.Parameters.AddWithValue("$message", item.Message);
                command.Parameters.AddWithValue("$rawLine", item.RawLine);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    private async Task CleanupOldLogsAsync(CancellationToken cancellationToken)
    {
        var retentionDays = Math.Max(_configuration.GetValue("LogStore:RetentionDays", 30), 1);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays);

        await _databaseLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM process_logs WHERE timestamp < $cutoff;";
            command.Parameters.AddWithValue("$cutoff", FormatTimestamp(cutoff));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    private SqliteConnection CreateConnection()
    {
        return new SqliteConnection($"Data Source={_databasePath}");
    }

    private static async Task ExecuteNonQueryAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> CountAsync(SqliteConnection connection, string whereClause, Dictionary<string, object?> parameters, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM process_logs WHERE {whereClause};";
        AddParameters(command, parameters);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static (string WhereClause, Dictionary<string, object?> Parameters) BuildLogFilter(
        string processId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? level,
        string? keyword)
    {
        var where = new List<string> { "process_id = $processId" };
        var parameters = new Dictionary<string, object?>
        {
            ["$processId"] = processId
        };

        if (from.HasValue)
        {
            where.Add("timestamp >= $from");
            parameters["$from"] = FormatTimestamp(from.Value);
        }

        if (to.HasValue)
        {
            where.Add("timestamp <= $to");
            parameters["$to"] = FormatTimestamp(to.Value);
        }

        if (!string.IsNullOrWhiteSpace(level))
        {
            where.Add("level = $level");
            parameters["$level"] = level.Trim();
        }

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            where.Add("(message LIKE $keyword OR raw_line LIKE $keyword)");
            parameters["$keyword"] = $"%{keyword.Trim()}%";
        }

        return (string.Join(" AND ", where), parameters);
    }

    private static async Task<IReadOnlyList<ProcessLogEntryDto>> QueryItemsAsync(
        SqliteConnection connection,
        string whereClause,
        Dictionary<string, object?> parameters,
        int pageSize,
        int offset,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, process_id, process_name, timestamp, stream, level, message, raw_line
            FROM process_logs
            WHERE {whereClause}
            ORDER BY timestamp DESC, id DESC
            LIMIT $pageSize OFFSET $offset;
            """;
        AddParameters(command, parameters);
        command.Parameters.AddWithValue("$pageSize", pageSize);
        command.Parameters.AddWithValue("$offset", offset);

        var items = new List<ProcessLogEntryDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new ProcessLogEntryDto
            {
                Id = reader.GetInt64(0),
                ProcessId = reader.GetString(1),
                ProcessName = reader.GetString(2),
                Timestamp = DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                Stream = reader.GetString(4),
                Level = reader.GetString(5),
                Message = reader.GetString(6),
                RawLine = reader.GetString(7)
            });
        }

        items.Reverse();
        return items;
    }

    private static void AddParameters(SqliteCommand command, Dictionary<string, object?> parameters)
    {
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Key, parameter.Value ?? DBNull.Value);
        }
    }

    private static string FormatTimestamp(DateTimeOffset timestamp)
    {
        return timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }
}
