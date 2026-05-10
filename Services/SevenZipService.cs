using ProcessDaemon.Models;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ProcessDaemon.Services;

public sealed class SevenZipService
{
    private const string PackageFileName = "7z2601-linux-x64.tar.xz";

    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<SevenZipService> _logger;
    private readonly SemaphoreSlim _installLock = new(1, 1);

    public SevenZipService(IConfiguration configuration, IWebHostEnvironment environment, ILogger<SevenZipService> logger)
    {
        _configuration = configuration;
        _environment = environment;
        _logger = logger;
    }

    public async Task<SevenZipStatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var packageFound = File.Exists(GetPackagePath());

        var configuredPath = _configuration["UpdatePackage:SevenZipPath"];
        if (!string.IsNullOrWhiteSpace(configuredPath) &&
            await TryGetVersionAsync(ResolveConfiguredPath(configuredPath), cancellationToken))
        {
            return Installed("configured", ResolveConfiguredPath(configuredPath), packageFound, "7-Zip 已可用。");
        }

        var localPath = GetLocalExecutablePath();
        if (localPath != null && await TryGetVersionAsync(localPath, cancellationToken))
        {
            return Installed("local", localPath, packageFound, "已使用程序目录内置 7-Zip。");
        }

        if (await TryGetVersionAsync("7z", cancellationToken))
        {
            return Installed("path", "7z", packageFound, "已检测到系统 7-Zip。");
        }

        return new SevenZipStatusDto
        {
            Installed = false,
            Source = string.Empty,
            Path = string.Empty,
            PackageFound = packageFound,
            Message = packageFound
                ? "未检测到 7-Zip，可使用程序目录中的离线安装包一键安装。"
                : $"未检测到 7-Zip，且程序目录缺少 {PackageFileName}。"
        };
    }

    public async Task<SevenZipStatusDto> InstallAsync(CancellationToken cancellationToken)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return new SevenZipStatusDto
            {
                Installed = false,
                PackageFound = File.Exists(GetPackagePath()),
                Message = "一键安装 7-Zip 仅支持 Linux 部署环境。"
            };
        }

        await _installLock.WaitAsync(cancellationToken);
        try
        {
            var current = await GetStatusAsync(cancellationToken);
            if (current.Installed)
            {
                return current;
            }

            var packagePath = GetPackagePath();
            if (!File.Exists(packagePath))
            {
                return new SevenZipStatusDto
                {
                    Installed = false,
                    PackageFound = false,
                    Message = $"未找到离线安装包：{PackageFileName}。"
                };
            }

            var installDirectory = GetInstallDirectory();
            Directory.CreateDirectory(installDirectory);
            await ExtractPackageAsync(packagePath, installDirectory, cancellationToken);
            await TrySetExecutableModeAsync(installDirectory, cancellationToken);

            var installed = await GetStatusAsync(cancellationToken);
            if (installed.Installed)
            {
                installed.Message = "7-Zip 已安装完成。";
                return installed;
            }

            installed.Message = "安装包已解压，但未找到可执行的 7z/7zz。";
            return installed;
        }
        finally
        {
            _installLock.Release();
        }
    }

    public async Task<string> GetExecutablePathAsync(CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(cancellationToken);
        if (!status.Installed || string.IsNullOrWhiteSpace(status.Path))
        {
            throw new InvalidOperationException(status.PackageFound
                ? "未检测到 7-Zip，请先在页面点击“一键安装 7z”。"
                : $"未检测到 7-Zip，且程序目录缺少 {PackageFileName}。");
        }

        return status.Path;
    }

    private SevenZipStatusDto Installed(string source, string path, bool packageFound, string message)
    {
        return new SevenZipStatusDto
        {
            Installed = true,
            Source = source,
            Path = path,
            PackageFound = packageFound,
            Message = message
        };
    }

    private string ResolveConfiguredPath(string configuredPath)
    {
        var trimmed = configuredPath.Trim();
        if (Path.IsPathRooted(trimmed) || trimmed.Contains(Path.DirectorySeparatorChar) || trimmed.Contains(Path.AltDirectorySeparatorChar))
        {
            return Path.GetFullPath(Path.IsPathRooted(trimmed)
                ? trimmed
                : Path.Combine(_environment.ContentRootPath, trimmed));
        }

        return trimmed;
    }

    private string? GetLocalExecutablePath()
    {
        var installDirectory = GetInstallDirectory();
        foreach (var fileName in new[] { "7zz", "7z" })
        {
            var directPath = Path.Combine(installDirectory, fileName);
            if (File.Exists(directPath))
            {
                return directPath;
            }
        }

        if (!Directory.Exists(installDirectory))
        {
            return null;
        }

        return Directory
            .EnumerateFiles(installDirectory, "*", SearchOption.AllDirectories)
            .FirstOrDefault(path =>
                string.Equals(Path.GetFileName(path), "7zz", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileName(path), "7z", StringComparison.OrdinalIgnoreCase));
    }

    private string GetInstallDirectory()
    {
        return Path.Combine(_environment.ContentRootPath, "tools", "7zip");
    }

    private string GetPackagePath()
    {
        return Path.Combine(_environment.ContentRootPath, PackageFileName);
    }

    private async Task<bool> TryGetVersionAsync(string executablePath, CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("i");

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return false;
            }

            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "检测 7-Zip {ExecutablePath} 失败。", executablePath);
            return false;
        }
    }

    private async Task ExtractPackageAsync(string packagePath, string installDirectory, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "tar",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-xJf");
        startInfo.ArgumentList.Add(packagePath);
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(installDirectory);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 tar，请确认 Linux 环境支持 tar -xJf。");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            var message = string.IsNullOrWhiteSpace(error) ? output : error;
            throw new InvalidOperationException($"解压 7-Zip 离线安装包失败：{message.Trim()}");
        }
    }

    private static async Task TrySetExecutableModeAsync(string installDirectory, CancellationToken cancellationToken)
    {
        foreach (var executable in Directory.EnumerateFiles(installDirectory, "*", SearchOption.AllDirectories)
            .Where(path =>
                string.Equals(Path.GetFileName(path), "7zz", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileName(path), "7z", StringComparison.OrdinalIgnoreCase)))
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "chmod",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("+x");
            startInfo.ArgumentList.Add(executable);

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("无法启动 chmod。");
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"设置 7-Zip 执行权限失败：{executable}");
            }
        }
    }
}
