using System.Runtime.InteropServices;
using System.Security;
using Purg.Platform;
using Windows.Storage;

namespace Purg.Mover;

public static partial class Mover
{
    public static DeleteSummary DeletePortableFiles(
        IReadOnlyList<DeleteRequest> requests,
        IProgress<DeleteProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var deletedPaths = new List<string>();
        var reasons = new Dictionary<string, int>(StringComparer.Ordinal);
        var deleted = 0;
        var skipped = 0;
        var failed = 0;
        var processed = 0;

        foreach (var request in requests)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var operation = DeleteOne(request);
            processed++;
            switch (operation.Status)
            {
                case FileOperationStatus.Deleted:
                    deleted++;
                    break;
                case FileOperationStatus.Skipped:
                    skipped++;
                    AddReason(reasons, operation.Reason!);
                    break;
                case FileOperationStatus.Failed:
                    failed++;
                    AddReason(reasons, operation.Reason!);
                    break;
            }

            progress?.Report(new DeleteProgress(processed, requests.Count, deleted, skipped, failed));
        }

        return new DeleteSummary(
            DeleteMode.Permanent,
            requests.Count,
            processed,
            deleted,
            skipped,
            failed,
            cancellationToken.IsCancellationRequested,
            deletedPaths,
            reasons);

        FileOperationResult DeleteOne(DeleteRequest request)
        {
            try
            {
                if (request.PortableRootId is not { } rootId ||
                    string.IsNullOrWhiteSpace(request.PortableRelativePath) ||
                    request.Identity is not { } expectedIdentity)
                {
                    return new FileOperationResult(FileOperationStatus.Skipped, ChangedAfterMarking);
                }

                var currentIdentity = PortableDeviceStorage.ComputeIdentityAsync(
                    rootId,
                    request.PortableRelativePath).GetAwaiter().GetResult();
                if (!IdentityMatches(expectedIdentity, currentIdentity))
                {
                    return new FileOperationResult(FileOperationStatus.Skipped, ChangedAfterMarking);
                }

                var file = PortableDeviceStorage.ResolveFileAsync(
                    rootId,
                    request.PortableRelativePath).GetAwaiter().GetResult();
                file.DeleteAsync(StorageDeleteOption.PermanentDelete).AsTask().GetAwaiter().GetResult();
                if (PortableFileExists(rootId, request.PortableRelativePath))
                {
                    return new FileOperationResult(FileOperationStatus.Failed, DeleteUnverified);
                }

                deletedPaths.Add(request.SourcePath);
                return new FileOperationResult(FileOperationStatus.Deleted, null);
            }
            catch (Exception exception) when (IsPortableNotFound(exception))
            {
                return new FileOperationResult(FileOperationStatus.Skipped, ChangedAfterMarking);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                               SecurityException or ArgumentException or NotSupportedException or
                                               InvalidOperationException or COMException)
            {
                return new FileOperationResult(FileOperationStatus.Failed, FileUnavailable);
            }
        }

        static bool PortableFileExists(Guid rootId, string relativePath)
        {
            try
            {
                _ = PortableDeviceStorage.ResolveFileAsync(rootId, relativePath).GetAwaiter().GetResult();
                return true;
            }
            catch (Exception exception) when (IsPortableNotFound(exception))
            {
                return false;
            }
        }

        static bool IsPortableNotFound(Exception exception) =>
            exception is FileNotFoundException or DirectoryNotFoundException ||
            exception is COMException comException &&
            (comException.HResult == unchecked((int)0x80070002) ||
             comException.HResult == unchecked((int)0x80070003));
    }
}
