using ProcessDaemon.Models;
using System.Text;

namespace ProcessDaemon.Services;

public sealed class FileManagerService
{
    private const long MaxTextFileBytes = 2 * 1024 * 1024;

    public DirectoryListingDto ListDirectory(string? path)
    {
        var directoryPath = ResolvePathOrDefault(path);
        if (!Directory.Exists(directoryPath))
        {
            throw new DirectoryNotFoundException($"目录不存在：{directoryPath}");
        }

        var entries = new List<FileEntryDto>();
        foreach (var directory in Directory.EnumerateDirectories(directoryPath))
        {
            var info = new DirectoryInfo(directory);
            entries.Add(new FileEntryDto
            {
                Name = info.Name,
                Path = info.FullName,
                IsDirectory = true,
                SizeBytes = null,
                LastWriteTime = info.LastWriteTime,
                Extension = string.Empty
            });
        }

        foreach (var file in Directory.EnumerateFiles(directoryPath))
        {
            var info = new FileInfo(file);
            entries.Add(new FileEntryDto
            {
                Name = info.Name,
                Path = info.FullName,
                IsDirectory = false,
                SizeBytes = info.Length,
                LastWriteTime = info.LastWriteTime,
                Extension = info.Extension
            });
        }

        return new DirectoryListingDto
        {
            Path = directoryPath,
            ParentPath = GetParentPath(directoryPath),
            Entries = entries
                .OrderByDescending(item => item.IsDirectory)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    public FileStream OpenRead(string path)
    {
        var filePath = ResolveRequiredPath(path);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"文件不存在：{filePath}", filePath);
        }

        return File.OpenRead(filePath);
    }

    public async Task<FileTextDto> ReadTextAsync(string path, CancellationToken cancellationToken)
    {
        var filePath = ResolveRequiredPath(path);
        var info = new FileInfo(filePath);
        if (!info.Exists)
        {
            throw new FileNotFoundException($"文件不存在：{filePath}", filePath);
        }

        if ((info.Attributes & FileAttributes.Directory) == FileAttributes.Directory)
        {
            throw new InvalidOperationException($"不能按文本读取目录：{filePath}");
        }

        if (info.Length > MaxTextFileBytes)
        {
            throw new InvalidOperationException($"文件超过 2MB，请下载后查看：{filePath}");
        }

        return new FileTextDto
        {
            Path = filePath,
            Content = await File.ReadAllTextAsync(filePath, Encoding.UTF8, cancellationToken),
            SizeBytes = info.Length
        };
    }

    public async Task SaveTextAsync(SaveFileTextRequest request, CancellationToken cancellationToken)
    {
        var filePath = ResolveRequiredPath(request.Path);
        if (Directory.Exists(filePath))
        {
            throw new InvalidOperationException($"不能按文本保存目录：{filePath}");
        }

        var parent = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(parent) && !Directory.Exists(parent))
        {
            throw new DirectoryNotFoundException($"父目录不存在：{parent}");
        }

        await File.WriteAllTextAsync(filePath, request.Content ?? string.Empty, new UTF8Encoding(false), cancellationToken);
    }

    public string CreateDirectory(CreateDirectoryRequest request)
    {
        var parentPath = ResolveRequiredPath(request.ParentPath);
        if (!Directory.Exists(parentPath))
        {
            throw new DirectoryNotFoundException($"父目录不存在：{parentPath}");
        }

        var childName = ValidateChildName(request.Name);
        var directoryPath = Path.GetFullPath(Path.Combine(parentPath, childName));
        Directory.CreateDirectory(directoryPath);
        return directoryPath;
    }

    public async Task<IReadOnlyList<FileEntryDto>> SaveUploadsAsync(string directory, IFormFileCollection files, bool overwrite, CancellationToken cancellationToken)
    {
        var directoryPath = ResolveRequiredPath(directory);
        if (!Directory.Exists(directoryPath))
        {
            throw new DirectoryNotFoundException($"目录不存在：{directoryPath}");
        }

        if (files.Count == 0)
        {
            throw new ArgumentException("请选择要上传的文件。");
        }

        var uploaded = new List<FileEntryDto>();
        foreach (var file in files)
        {
            if (file.Length <= 0)
            {
                continue;
            }

            var fileName = ValidateChildName(Path.GetFileName(file.FileName));
            var targetPath = Path.GetFullPath(Path.Combine(directoryPath, fileName));
            if (File.Exists(targetPath) && !overwrite)
            {
                throw new IOException($"文件已存在：{targetPath}");
            }

            await using (var output = File.Create(targetPath))
            {
                await file.CopyToAsync(output, cancellationToken);
            }

            var info = new FileInfo(targetPath);
            uploaded.Add(new FileEntryDto
            {
                Name = info.Name,
                Path = info.FullName,
                IsDirectory = false,
                SizeBytes = info.Length,
                LastWriteTime = info.LastWriteTime,
                Extension = info.Extension
            });
        }

        return uploaded;
    }

    public string Rename(RenameFileRequest request)
    {
        var sourcePath = ResolveRequiredPath(request.Path);
        var newName = ValidateChildName(request.NewName);
        var parent = Path.GetDirectoryName(sourcePath);
        if (string.IsNullOrWhiteSpace(parent))
        {
            throw new InvalidOperationException($"无法重命名根目录：{sourcePath}");
        }

        var targetPath = Path.GetFullPath(Path.Combine(parent, newName));
        if (File.Exists(targetPath) || Directory.Exists(targetPath))
        {
            throw new IOException($"目标已存在：{targetPath}");
        }

        if (Directory.Exists(sourcePath))
        {
            Directory.Move(sourcePath, targetPath);
            return targetPath;
        }

        if (File.Exists(sourcePath))
        {
            File.Move(sourcePath, targetPath);
            return targetPath;
        }

        throw new FileNotFoundException($"路径不存在：{sourcePath}", sourcePath);
    }

    public void Delete(string path)
    {
        var targetPath = ResolveRequiredPath(path);
        if (Directory.Exists(targetPath))
        {
            Directory.Delete(targetPath, recursive: true);
            return;
        }

        if (File.Exists(targetPath))
        {
            File.Delete(targetPath);
            return;
        }

        throw new FileNotFoundException($"路径不存在：{targetPath}", targetPath);
    }

    private static string ResolvePathOrDefault(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            return Path.GetFullPath(path.Trim());
        }

        if (OperatingSystem.IsWindows())
        {
            return Path.GetPathRoot(Environment.CurrentDirectory) ?? Environment.CurrentDirectory;
        }

        return "/";
    }

    private static string ResolveRequiredPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("路径不能为空。");
        }

        return Path.GetFullPath(path.Trim());
    }

    private static string? GetParentPath(string directoryPath)
    {
        var parent = Directory.GetParent(directoryPath);
        return parent?.FullName;
    }

    private static string ValidateChildName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("名称不能为空。");
        }

        var trimmed = name.Trim();
        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            trimmed.Contains(Path.DirectorySeparatorChar) ||
            trimmed.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException("名称不能包含路径分隔符或非法字符。");
        }

        return trimmed;
    }
}
