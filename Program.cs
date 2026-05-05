using ProcessDaemon.Hubs;
using ProcessDaemon.Models;
using ProcessDaemon.Services;
using ProcessDaemon.Workers;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSignalR();
builder.Services.AddSingleton<ProcessConfigStore>();
builder.Services.AddSingleton<ProcessManager>();
builder.Services.AddSingleton<ProcessUpdateService>();
builder.Services.AddHostedService<DaemonWorker>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapHub<MonitorHub>("/monitor");

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
});

processApi.MapDelete("/{id}", async (string id, ProcessManager processManager) =>
{
    var deleted = await processManager.DeleteProcessAsync(id);
    return deleted
        ? Results.Ok(new { message = "进程配置已删除。" })
        : Results.NotFound(new { message = "未找到对应的进程配置。" });
});

processApi.MapGet("/{id}/updates", async (string id, ProcessUpdateService updateService) =>
{
    var history = await updateService.GetHistoryAsync(id);
    return Results.Ok(history);
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
        return result.History.Status == "Succeeded"
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
});

app.Run();
