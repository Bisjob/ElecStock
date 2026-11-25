using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ElecStock.Data.Files;

public sealed class StorageOptions
{
    /// <summary>Folder holding photos, thumbnails and datasheets; relative paths are under the content root.</summary>
    public string Root { get; set; } = "AppData";
    /// <summary>Photos larger than this (in pixels, longest side) are shrunk on import.</summary>
    public int MaxPhotoSide { get; set; } = 2400;
    public int ThumbSide { get; set; } = 480;
}

/// <summary>
/// Files of the stock on disk: <c>photos/</c>, <c>thumbs/</c>, <c>datasheets/</c>, <c>icons/</c> (served under
/// <c>/files/</c>) and <c>trash/</c> (removed files, never served). Storage paths are relative,
/// with forward slashes, and file names are GUIDs so renaming a part never moves a file.
/// </summary>
public sealed class FileStore
{
    public const string PhotosDir = "photos";
    public const string ThumbsDir = "thumbs";
    public const string DatasheetsDir = "datasheets";
    public const string IconsDir = "icons";
    public const string TrashDir = "trash";
    public static readonly string[] ServedDirs = [PhotosDir, ThumbsDir, DatasheetsDir, IconsDir];

    public FileStore(IOptions<StorageOptions> options, IHostEnvironment env)
    {
        Options = options.Value;
        Root = Path.GetFullPath(Path.Combine(env.ContentRootPath, Options.Root));
        foreach (var dir in ServedDirs.Append(TrashDir))
            Directory.CreateDirectory(Path.Combine(Root, dir));
    }

    public StorageOptions Options { get; }
    public string Root { get; }

    /// <summary>URL of a stored file, relative to the server root.</summary>
    public static string Url(string storagePath) => "files/" + storagePath;

    public string FullPath(string storagePath)
    {
        var full = Path.GetFullPath(Path.Combine(Root, storagePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"'{storagePath}' is outside the storage folder.");
        return full;
    }

    public bool Exists(string? storagePath) => storagePath != null && File.Exists(FullPath(storagePath));

    /// <summary>A new storage path in <paramref name="dir"/> with the given extension (".jpg").</summary>
    public static string NewPath(string dir, string extension) => $"{dir}/{Guid.NewGuid():N}{extension.ToLowerInvariant()}";

    public async Task<string> SaveAsync(string dir, string extension, Stream content, CancellationToken ct)
    {
        var path = NewPath(dir, extension);
        await using var output = File.Create(FullPath(path));
        await content.CopyToAsync(output, ct);
        return path;
    }

    public async Task<string> SaveAsync(string dir, string extension, byte[] content, CancellationToken ct)
    {
        var path = NewPath(dir, extension);
        await File.WriteAllBytesAsync(FullPath(path), content, ct);
        return path;
    }

    /// <summary>Moves a stored file to <c>trash/</c> (recoverable by hand); missing files are ignored.</summary>
    public void Trash(string? storagePath)
    {
        if (string.IsNullOrEmpty(storagePath)) return;
        var full = FullPath(storagePath);
        if (!File.Exists(full)) return;
        var target = Path.Combine(Root, TrashDir, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Path.GetFileName(full)}");
        File.Move(full, target, overwrite: true);
    }

    /// <summary>Moves every served file to <c>trash/replaced-…/</c>, before an import that replaces the stock.</summary>
    public void TrashAll()
    {
        var target = Path.Combine(Root, TrashDir, $"replaced-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
        foreach (var dir in ServedDirs)
        {
            var source = Path.Combine(Root, dir);
            if (Directory.Exists(source) && Directory.EnumerateFileSystemEntries(source).Any())
            {
                Directory.CreateDirectory(target);
                Directory.Move(source, Path.Combine(target, dir));
            }
            Directory.CreateDirectory(source);
        }
    }
}
