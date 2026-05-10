using ProcessDaemon.Hubs;
using ProcessDaemon.Models;
using ProcessDaemon.Services;
using ProcessDaemon.Workers;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSignalR();
builder.Services.AddSingleton<ProcessConfigStore>();
builder.Services.AddSingleton<ProcessPathService>();
builder.Services.AddSingleton<ProcessLogService>();
builder.Services.AddSingleton<ProcessManager>();
builder.Services.AddSingleton<SevenZipService>();
builder.Services.AddSingleton<ProcessUpdateService>();
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

processApi.MapDelete("/{id}", async (string id, ProcessManager processManager) =>
{
    try
    {
        var deleted = await processManager.DeleteProcessAsync(id);
        return deleted
            ? Results.Ok(new { message = "进程配置和部署目录已删除。" })
            : Results.NotFound(new { message = "未找到对应的进程配置。" });
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
        var result = await updateService.UpdateAsync(id, file, password, cancellationToken);
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
    ProcessUpdateService updateService,
    CancellationToken cancellationToken) =>
{
    try
    {
        var result = await updateService.CreateManualSnapshotAsync(id, cancellationToken);
        return Results.Ok(result);
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
