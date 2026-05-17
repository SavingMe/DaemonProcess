namespace ProcessDaemon.Models;

public sealed class FileEntryDto
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public long? SizeBytes { get; set; }
    public DateTimeOffset LastWriteTime { get; set; }
    public string Extension { get; set; } = string.Empty;
}

public sealed class DirectoryListingDto
{
    public string Path { get; set; } = string.Empty;
    public string? ParentPath { get; set; }
    public IReadOnlyList<FileEntryDto> Entries { get; set; } = Array.Empty<FileEntryDto>();
}

public sealed class FileTextDto
{
    public string Path { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
}

public sealed class CreateDirectoryRequest
{
    public string ParentPath { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

public sealed class RenameFileRequest
{
    public string Path { get; set; } = string.Empty;
    public string NewName { get; set; } = string.Empty;
}

public sealed class SaveFileTextRequest
{
    public string Path { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
}
