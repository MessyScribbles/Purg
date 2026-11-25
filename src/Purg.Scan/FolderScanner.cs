using System.IO.Enumeration;
using Purg.Platform;
using Purg.Session;

namespace Purg.Scan;

public sealed record ScanResult(
    IReadOnlyList<PhotoEntry> Photos,
    int SkippedCloudFiles,
    int SkippedLinkedFiles,
    int TotalFiles,
    int SkippedUnavailableFiles = 0);

public sealed record ScanProgress(int FilesScanned, int ImagesFound = 0);

public static partial class FolderScanner
{
    private static readonly SemaphoreSlim ScanGate = new(1, 1);
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".jfif",
        ".png", ".webp", ".bmp", ".gif", ".tif", ".tiff", ".avif",
        ".heic", ".heif", ".ico", ".wdp", ".hdp", ".jxr"
    };

    public static async Task<ScanResult> ScanAsync(
        string folderPath,
        CancellationToken cancellationToken = default,
        IProgress<ScanProgress>? progress = null)
    {
        await ScanGate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() => Scan(folderPath, cancellationToken, progress), cancellationToken);
        }
        finally
        {
            ScanGate.Release();
        }
    }

    public static bool IsSupportedImage(string name) =>
        SupportedExtensions.Contains(Path.GetExtension(name));

    private static ScanResult Scan(
        string folderPath,
        CancellationToken cancellationToken,
        IProgress<ScanProgress>? progress)
    {
        var photos = new List<PhotoEntry>();
        var cloudSkipped = 0;
        var linkedSkipped = 0;
        var totalFiles = 0;
        var enumerable = new FileSystemEnumerable<FileCandidate>(
            folderPath,
            (ref FileSystemEntry entry) => new FileCandidate(
                entry.ToFullPath(),
                entry.FileName.ToString(),
                entry.Length,
                entry.LastWriteTimeUtc.UtcDateTime,
                entry.Attributes),
            new EnumerationOptions
            {
                RecurseSubdirectories = true,
                ReturnSpecialDirectories = false,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.System | FileAttributes.Hidden | FileAttributes.ReparsePoint
            });
        enumerable.ShouldIncludePredicate = (ref FileSystemEntry entry) =>
            (entry.Attributes & FileAttributes.Directory) == 0;
        enumerable.ShouldRecursePredicate = (ref FileSystemEntry entry) =>
            !entry.FileName.ToString().StartsWith("_TO_DELETE_", StringComparison.OrdinalIgnoreCase);

        foreach (var candidate in enumerable)
        {
            cancellationToken.ThrowIfCancellationRequested();
            totalFiles++;
            if (totalFiles % 128 == 0)
            {
                progress?.Report(new ScanProgress(totalFiles, photos.Count));
            }

            if (!SupportedExtensions.Contains(Path.GetExtension(candidate.Name)))
            {
                continue;
            }

            var reason = FileSafety.GetUnsafeReason(candidate.Path, candidate.Attributes);
            if (reason == UnsafeFileReason.CloudOnly)
            {
                cloudSkipped++;
                continue;
            }

            if (reason == UnsafeFileReason.Linked)
            {
                linkedSkipped++;
                continue;
            }

            photos.Add(new PhotoEntry(
                candidate.Path,
                DisplayNameSanitizer.Sanitize(candidate.Name),
                candidate.Length,
                candidate.LastWriteTimeUtc));
        }

        progress?.Report(new ScanProgress(totalFiles, photos.Count));
        return new ScanResult(photos, cloudSkipped, linkedSkipped, totalFiles);
    }

    private sealed record FileCandidate(
        string Path,
        string Name,
        long Length,
        DateTime LastWriteTimeUtc,
        FileAttributes Attributes);
}
