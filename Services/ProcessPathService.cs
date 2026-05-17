using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ProcessDaemon.Services;

public sealed class ProcessPathService
{
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;

    public ProcessPathService(IConfiguration configuration, IWebHostEnvironment environment)
    {
        _configuration = configuration;
        _environment = environment;
    }

    public string ResolveEntryPath(string entryPath)
    {
        var fullPath = Path.GetFullPath(Path.IsPathRooted(entryPath)
            ? entryPath.Trim()
            : Path.Combine(GetProcessBaseDir(), entryPath.Trim()));

        EnsureInsideBaseDir(fullPath);
        return fullPath;
    }

    public string ResolveDllPath(string dllPath)
    {
        return ResolveEntryPath(dllPath);
    }

    public string ResolveTargetDirectory(string dllPath)
    {
        var targetDirectory = Path.GetDirectoryName(ResolveEntryPath(dllPath)) ?? GetProcessBaseDir();
        EnsureTargetDirectoryIsNotBaseDir(targetDirectory);
        return targetDirectory;
    }

    public void EnsureTargetDirectoryReady(string dllPath)
    {
        var targetDirectory = ResolveTargetDirectory(dllPath);

        try
        {
            Directory.CreateDirectory(targetDirectory);
            TrySetLinuxDirectoryMode(targetDirectory);
            VerifyDirectoryAccess(targetDirectory);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"目录准备失败：{targetDirectory}。{ex.Message}", ex);
        }
    }

    public bool IsSameTargetDirectory(string leftDllPath, string rightDllPath)
    {
        return string.Equals(
            NormalizeDirectoryPath(ResolveTargetDirectory(leftDllPath)),
            NormalizeDirectoryPath(ResolveTargetDirectory(rightDllPath)),
            StringComparison.OrdinalIgnoreCase);
    }

    public void DeleteTargetDirectory(string dllPath)
    {
        var targetDirectory = ResolveTargetDirectory(dllPath);
        if (Directory.Exists(targetDirectory))
        {
            Directory.Delete(targetDirectory, recursive: true);
        }
    }

    public void DeleteSnapshotDirectory(string snapshotDirectory)
    {
        var fullPath = Path.GetFullPath(snapshotDirectory);
        if (Directory.Exists(fullPath))
        {
            Directory.Delete(fullPath, recursive: true);
        }
    }

    public void CopyDirectoryFast(string sourceDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
            TryCopyWithLinuxCommand(sourceDirectory, targetDirectory))
        {
            return;
        }

        CopyDirectoryManaged(sourceDirectory, targetDirectory);
    }

    public void CopyDirectoryManaged(string sourceDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);

        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(Path.Combine(targetDirectory, relativePath));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, file);
            var targetFile = Path.Combine(targetDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            File.Copy(file, targetFile, overwrite: true);
        }
    }

    public static void ClearDirectory(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            File.Delete(file);
        }

        foreach (var childDirectory in Directory.EnumerateDirectories(directory))
        {
            Directory.Delete(childDirectory, recursive: true);
        }
    }

    private string GetProcessBaseDir()
    {
        var baseDir = _configuration["ProcessBaseDir"];
        if (string.IsNullOrWhiteSpace(baseDir))
        {
            baseDir = Path.Combine(_environment.ContentRootPath, "process-apps");
        }
        else if (!Path.IsPathRooted(baseDir))
        {
            baseDir = Path.Combine(_environment.ContentRootPath, baseDir);
        }

        return Path.GetFullPath(baseDir);
    }

    private void EnsureInsideBaseDir(string fullPath)
    {
        var normalizedBase = NormalizeDirectoryPath(GetProcessBaseDir());
        var normalizedPath = Path.GetFullPath(fullPath);

        if (!normalizedPath.StartsWith(normalizedBase, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"程序路径必须位于 ProcessBaseDir 内：{normalizedBase}");
        }
    }

    private void EnsureTargetDirectoryIsNotBaseDir(string targetDirectory)
    {
        if (string.Equals(
            NormalizeDirectoryPath(targetDirectory),
            NormalizeDirectoryPath(GetProcessBaseDir()),
            StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("程序路径必须位于 ProcessBaseDir 的子目录内，例如 test01/WebApplication1.dll 或 test01/WebApplication1。");
        }
    }

    private static string NormalizeDirectoryPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.EndsWith(Path.DirectorySeparatorChar))
        {
            fullPath += Path.DirectorySeparatorChar;
        }

        return fullPath;
    }

    private static void VerifyDirectoryAccess(string directory)
    {
        _ = Directory.EnumerateFileSystemEntries(directory).Take(1).ToList();

        var probePath = Path.Combine(directory, $".processdaemon-probe-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(probePath, "ok");
        _ = File.ReadAllText(probePath);
        File.Delete(probePath);
    }

    private static void TrySetLinuxDirectoryMode(string directory)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return;
        }

        var processStartInfo = new ProcessStartInfo
        {
            FileName = "chmod",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        processStartInfo.ArgumentList.Add("755");
        processStartInfo.ArgumentList.Add(directory);

        using var process = Process.Start(processStartInfo)
            ?? throw new InvalidOperationException("无法启动 chmod。");
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            var error = process.StandardError.ReadToEnd();
            throw new InvalidOperationException($"chmod 755 失败：{error.Trim()}");
        }
    }

    private static bool TryCopyWithLinuxCommand(string sourceDirectory, string targetDirectory)
    {
        try
        {
            var processStartInfo = new ProcessStartInfo
            {
                FileName = "cp",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            processStartInfo.ArgumentList.Add("-a");
            processStartInfo.ArgumentList.Add(Path.Combine(sourceDirectory, "."));
            processStartInfo.ArgumentList.Add(targetDirectory);

            using var process = Process.Start(processStartInfo);
            if (process == null)
            {
                return false;
            }

            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
