using ProcessDaemon.Hubs;
using ProcessDaemon.Models;
using ProcessDaemon.Services;
using ProcessDaemon.Workers;
using System.Text;
using System.Text.Json;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSignalR();
builder.Services.AddSingleton<ProcessConfigStore>();
builder.Services.AddSingleton<ProcessPathService>();
builder.Services.AddSingleton<ProcessLogService>();
builder.Services.AddSingleton<ProcessManager>();
builder.Services.AddSingleton<SevenZipService>();
builder.Services.AddSingleton<ProcessUpdateService>();
builder.Services.AddSingleton<FileManagerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ProcessLogService>());
builder.Services.AddHostedService<DaemonWorker>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapHub<MonitorHub>("/monitor");

var toolsApi = app.MapGroup("/api/tools");

toolsApi.MapGet("/7zip", async (SevenZipService sevenZipService, CancellationToken cancellationToken) =>
{
    var status = await sevenZipService.GetStatusAsync(cancellationToken);
    return Results.Ok(status);
});

toolsApi.MapPost("/7zip/install", async (SevenZipService sevenZipService, CancellationToken cancellationToken) =>
{
    try
    {
        var status = await sevenZipService.InstallAsync(cancellationToken);
        return status.Installed
            ? Results.Ok(status)
            : Results.BadRequest(status);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new SevenZipStatusDto
        {
            Installed = false,
            Message = ex.Message
        });
    }
    catch (IOException ex)
    {
        return Results.BadRequest(new SevenZipStatusDto
        {
            Installed = false,
            Message = ex.Message
        });
    }
    catch (UnauthorizedAccessException ex)
    {
        return Results.BadRequest(new SevenZipStatusDto
        {
            Installed = false,
            Message = ex.Message
        });
    }
    catch (System.ComponentModel.Win32Exception ex)
    {
        return Results.BadRequest(new SevenZipStatusDto
        {
            Installed = false,
            Message = ex.Message
        });
    }
});

app.MapGet("/api/files", (FileManagerService fileManager, string? path) =>
{
    try
    {
        return Results.Ok(fileManager.ListDirectory(path));
    }
    catch (Exception ex) when (IsFileManagerException(ex))
    {
        return ToFileManagerError(ex);
    }
});

app.MapDelete("/api/files", (FileManagerService fileManager, string path) =>
{
    try
    {
        fileManager.Delete(path);
        return Results.Ok(new { message = "已删除。" });
    }
    catch (Exception ex) when (IsFileManagerException(ex))
    {
        return ToFileManagerError(ex);
    }
});

var filesApi = app.MapGroup("/api/files");

filesApi.MapGet("/download", (FileManagerService fileManager, string path) =>
{
    try
    {
        var stream = fileManager.OpenRead(path);
        return Results.File(
            stream,
            "application/octet-stream",
            Path.GetFileName(path),
            enableRangeProcessing: true);
    }
    catch (Exception ex) when (IsFileManagerException(ex))
    {
        return ToFileManagerError(ex);
    }
});

filesApi.MapGet("/content", async (FileManagerService fileManager, string path, CancellationToken cancellationToken) =>
{
    try
    {
        var content = await fileManager.ReadTextAsync(path, cancellationToken);
        return Results.Ok(content);
    }
    catch (Exception ex) when (IsFileManagerException(ex))
    {
        return ToFileManagerError(ex);
    }
});

filesApi.MapPut("/content", async (
    FileManagerService fileManager,
    SaveFileTextRequest request,
    CancellationToken cancellationToken) =>
{
    try
    {
        await fileManager.SaveTextAsync(request, cancellationToken);
        return Results.Ok(new { message = "文件已保存。" });
    }
    catch (Exception ex) when (IsFileManagerException(ex))
    {
        return ToFileManagerError(ex);
    }
});

filesApi.MapPost("/directory", (FileManagerService fileManager, CreateDirectoryRequest request) =>
{
    try
    {
        var path = fileManager.CreateDirectory(request);
        return Results.Ok(new { message = "目录已创建。", path });
    }
    catch (Exception ex) when (IsFileManagerException(ex))
    {
        return ToFileManagerError(ex);
    }
});

filesApi.MapPost("/upload", async (
    FileManagerService fileManager,
    HttpRequest request,
    CancellationToken cancellationToken) =>
{
    try
    {
        var form = await request.ReadFormAsync(cancellationToken);
        var directory = form["path"].FirstOrDefault();
        var overwrite = bool.TryParse(form["overwrite"].FirstOrDefault(), out var parsed) && parsed;
        var uploaded = await fileManager.SaveUploadsAsync(directory ?? string.Empty, form.Files, overwrite, cancellationToken);
        return Results.Ok(new
        {
            message = $"已上传 {uploaded.Count} 个文件。",
            uploaded
        });
    }
    catch (Exception ex) when (IsFileManagerException(ex))
    {
        return ToFileManagerError(ex);
    }
});

filesApi.MapPut("/rename", (FileManagerService fileManager, RenameFileRequest request) =>
{
    try
    {
        var path = fileManager.Rename(request);
        return Results.Ok(new { message = "已重命名。", path });
    }
    catch (Exception ex) when (IsFileManagerException(ex))
    {
        return ToFileManagerError(ex);
    }
});

var processApi = app.MapGroup("/api/processes");

processApi.MapGet("/", async (ProcessManager processManager) =>
{
    var processes = await processManager.GetProcessConfigsAsync();
    return Results.Ok(processes);
});

processApi.MapPost("/", async (ProcessManager processManager, ProcessConfigDto request) =>
{
    try
    {
        var process = await processManager.CreateProcessAsync(request);
        return Results.Created($"/api/processes/{process.Id}", new
        {
            message = "进程配置已保存。",
            process
        });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
    catch (IOException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
    catch (UnauthorizedAccessException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
});

processApi.MapPut("/{id}", async (string id, ProcessManager processManager, ProcessConfigDto request) =>
{
    if (!string.Equals(id, request.Id, StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { message = "请求路径和配置 ID 不一致。" });
    }

    try
    {
        var result = await processManager.UpdateProcessAsync(request);
        return Results.Ok(new
        {
            message = result.RestartRequired ? "配置已保存，重启后生效。" : "配置已保存。",
            restartRequired = result.RestartRequired,
            process = result.Process
        });
    }
    catch (KeyNotFoundException ex)
    {
        return Results.NotFound(new { message = ex.Message });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
    catch (IOException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
    catch (UnauthorizedAccessException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
});

processApi.MapDelete("/{id}", async (
    string id,
    ProcessManager processManager,
    ProcessUpdateService updateService,
    ProcessLogService logService,
    CancellationToken cancellationToken) =>
{
    try
    {
        var deleted = await processManager.DeleteProcessAsync(id);
        if (!deleted)
        {
            return Results.NotFound(new { message = "未找到对应的进程配置。" });
        }

        var updateCleanup = await updateService.DeleteProcessUpdateDataAsync(id, cancellationToken);
        var logsDeleted = await logService.DeleteProcessLogsAsync(id, cancellationToken);

        return Results.Ok(new
        {
            message = "进程配置、部署目录、历史备份、更新记录和历史日志已删除。",
            updateCleanup,
            logsDeleted
        });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
    catch (IOException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
    catch (UnauthorizedAccessException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
});

processApi.MapGet("/{id}/updates", async (string id, ProcessUpdateService updateService) =>
{
    var snapshots = await updateService.GetSnapshotsAsync(id);
    return Results.Ok(snapshots);
});

processApi.MapGet("/{id}/logs/export", (
    string id,
    ProcessLogService logService,
    DateTimeOffset? from,
    DateTimeOffset? to,
    string? level,
    string? keyword,
    CancellationToken cancellationToken) =>
{
    var safeId = string.Concat(id.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-'));
    var fileName = $"process-{safeId}-logs-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.txt";
    return Results.Stream(
        stream => logService.ExportAsync(id, from, to, level, keyword, stream, cancellationToken),
        "text/plain; charset=utf-8",
        fileName);
});

processApi.MapGet("/{id}/logs", async (
    string id,
    ProcessLogService logService,
    DateTimeOffset? from,
    DateTimeOffset? to,
    string? level,
    string? keyword,
    int? page,
    int? pageSize,
    CancellationToken cancellationToken) =>
{
    var result = await logService.QueryAsync(
        id,
        from,
        to,
        level,
        keyword,
        page ?? 1,
        pageSize ?? 200,
        cancellationToken);
    return Results.Ok(result);
});

processApi.MapDelete("/{id}/logs", async (
    string id,
    ProcessLogService logService,
    CancellationToken cancellationToken) =>
{
    var deleted = await logService.DeleteProcessLogsAsync(id, cancellationToken);
    return Results.Ok(new
    {
        message = deleted > 0 ? $"已删除 {deleted} 条日志。" : "没有可删除的历史日志。",
        deleted
    });
});

processApi.MapPost("/{id}/updates", async (
    string id,
    HttpRequest request,
    ProcessUpdateService updateService,
    CancellationToken cancellationToken) =>
{
    try
    {
        var form = await request.ReadFormAsync(cancellationToken);
        var file = form.Files["file"];
        if (file == null)
        {
            return Results.BadRequest(new { message = "请选择更新包。" });
        }

        var password = form["password"].FirstOrDefault();
        var remark = form["remark"].FirstOrDefault();
        var result = await updateService.UpdateAsync(id, file, password, remark, cancellationToken);
        return result.Snapshot.Status == "Succeeded"
            ? Results.Ok(result)
            : Results.BadRequest(result);
    }
    catch (KeyNotFoundException ex)
    {
        return Results.NotFound(new { message = ex.Message });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
    catch (IOException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
    catch (UnauthorizedAccessException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
});

processApi.MapPost("/{id}/snapshots", async (
    string id,
    HttpRequest request,
    ProcessUpdateService updateService,
    CancellationToken cancellationToken) =>
{
    try
    {
        ProcessSnapshotRequestDto? snapshotRequest = null;
        if (request.ContentLength.GetValueOrDefault() > 0)
        {
            snapshotRequest = await request.ReadFromJsonAsync<ProcessSnapshotRequestDto>(cancellationToken: cancellationToken);
        }

        var result = await updateService.CreateManualSnapshotAsync(id, snapshotRequest?.Remark, cancellationToken);
        return Results.Ok(result);
    }
    catch (JsonException ex)
    {
        return Results.BadRequest(new { message = $"备注请求格式不正确：{ex.Message}" });
    }
    catch (KeyNotFoundException ex)
    {
        return Results.NotFound(new { message = ex.Message });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
    catch (DirectoryNotFoundException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
    catch (IOException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
    catch (UnauthorizedAccessException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
});

processApi.MapPost("/{id}/updates/{snapshotId}/restore", async (
    string id,
    string snapshotId,
    ProcessUpdateService updateService,
    CancellationToken cancellationToken) =>
{
    try
    {
        var result = await updateService.RestoreAsync(id, snapshotId, cancellationToken);
        return result.Snapshot.IsCurrent
            ? Results.Ok(result)
            : Results.BadRequest(result);
    }
    catch (KeyNotFoundException ex)
    {
        return Results.NotFound(new { message = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
    catch (DirectoryNotFoundException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
});

processApi.MapDelete("/{id}/updates/{snapshotId}", async (
    string id,
    string snapshotId,
    ProcessUpdateService updateService,
    CancellationToken cancellationToken) =>
{
    try
    {
        await updateService.DeleteSnapshotAsync(id, snapshotId, cancellationToken);
        return Results.Ok(new { message = "历史快照和备份目录已删除。" });
    }
    catch (KeyNotFoundException ex)
    {
        return Results.NotFound(new { message = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
    catch (IOException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
    catch (UnauthorizedAccessException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
});

app.Run();

static bool IsFileManagerException(Exception ex)
{
    return ex is ArgumentException or
        InvalidOperationException or
        IOException or
        UnauthorizedAccessException or
        NotSupportedException or
        System.ComponentModel.Win32Exception;
}

static IResult ToFileManagerError(Exception ex)
{
    return ex switch
    {
        FileNotFoundException or DirectoryNotFoundException => Results.NotFound(new { message = ex.Message }),
        _ => Results.BadRequest(new { message = ex.Message })
    };
}
