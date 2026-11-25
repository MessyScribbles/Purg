using System.Runtime.InteropServices;
using Purg.Platform;
using Purg.Session;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Search;

namespace Purg.Scan;

public static partial class FolderScanner
{
    public static async Task<ScanResult> ScanPortableAsync(
        Guid rootId,
        string rootName,
        CancellationToken cancellationToken = default,
        IProgress<ScanProgress>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootName);
        await ScanGate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(
                () => ScanPortable(rootId, cancellationToken, progress),
                cancellationToken);
        }
        finally
        {
            ScanGate.Release();
        }
    }

    private static ScanResult ScanPortable(
        Guid rootId,
        CancellationToken cancellationToken,
        IProgress<ScanProgress>? progress)
    {
        const uint BatchSize = 64;
        var photos = new List<PhotoEntry>();
        var totalFiles = 0;
        var lastReportedFileCount = 0;
        var lastReportedImageCount = 0;
        var skippedUnavailable = 0;
        var folders = new Stack<(StorageFolder Folder, string RelativePath)>();
        var root = PortableDeviceStorage.GetFolderAsync(rootId, string.Empty).GetAwaiter().GetResult();
        folders.Push((root, string.Empty));

        while (folders.TryPop(out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var childFolders = current.Folder.GetFoldersAsync()
                    .AsTask(cancellationToken)
                    .GetAwaiter()
                    .GetResult();
                foreach (var child in childFolders)
                {
                    if (!ShouldSkipFolder(current.RelativePath, child.Name))
                    {
                        folders.Push((
                            child,
                            PortableDeviceStorage.CombineRelativePath(current.RelativePath, child.Name)));
                    }
                }

                var queryOptions = new QueryOptions
                {
                    FolderDepth = FolderDepth.Shallow
                };
                queryOptions.SetPropertyPrefetch(
                    PropertyPrefetchOptions.BasicProperties,
                    Array.Empty<string>());
                var fileQuery = current.Folder.AreQueryOptionsSupported(queryOptions)
                    ? current.Folder.CreateFileQueryWithOptions(queryOptions)
                    : current.Folder.CreateFileQuery();
                var startIndex = 0u;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var files = fileQuery.GetFilesAsync(startIndex, BatchSize)
                        .AsTask(cancellationToken)
                        .GetAwaiter()
                        .GetResult();
                    if (files.Count == 0)
                    {
                        break;
                    }

                    startIndex += (uint)files.Count;
                    foreach (var file in files)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        totalFiles++;
                        if (totalFiles - lastReportedFileCount >= 64)
                        {
                            progress?.Report(new ScanProgress(totalFiles, photos.Count));
                            lastReportedFileCount = totalFiles;
                        }

                        if (!IsSupportedImage(file.Name))
                        {
                            continue;
                        }

                        try
                        {
                            var properties = file.GetBasicPropertiesAsync()
                                .AsTask(cancellationToken)
                                .GetAwaiter()
                                .GetResult();
                            var relativePath = PortableDeviceStorage.CombineRelativePath(current.RelativePath, file.Name);
                            photos.Add(new PhotoEntry(
                                $"portable:{rootId:D}/{relativePath}",
                                DisplayNameSanitizer.Sanitize(file.Name),
                                checked((long)properties.Size),
                                properties.DateModified.UtcDateTime,
                                rootId,
                                relativePath));
                        }
                        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or
                                                           ArgumentException or NotSupportedException or COMException or
                                                           InvalidOperationException)
                        {
                            skippedUnavailable++;
                        }

                        if (photos.Count - lastReportedImageCount >= 16)
                        {
                            progress?.Report(new ScanProgress(totalFiles, photos.Count));
                            lastReportedImageCount = photos.Count;
                        }
                    }

                    if ((uint)files.Count < BatchSize)
                    {
                        break;
                    }
                }
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or
                                               ArgumentException or NotSupportedException or COMException or
                                               InvalidOperationException)
            {
                skippedUnavailable++;
            }
        }

        progress?.Report(new ScanProgress(totalFiles, photos.Count));
        return new ScanResult(photos, 0, 0, totalFiles, skippedUnavailable);
    }

    private static bool ShouldSkipFolder(string parentPath, string name)
    {
        if (name.StartsWith("_TO_DELETE_", StringComparison.OrdinalIgnoreCase) ||
            name.Equals(".thumbnails", StringComparison.OrdinalIgnoreCase) ||
            name.Equals(".trash", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var isAndroidFolder = parentPath.Equals("Android", StringComparison.OrdinalIgnoreCase) ||
                              parentPath.EndsWith("/Android", StringComparison.OrdinalIgnoreCase);
        return isAndroidFolder &&
               (name.Equals("data", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("obb", StringComparison.OrdinalIgnoreCase));
    }
}
