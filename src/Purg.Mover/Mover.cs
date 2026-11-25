using System.Diagnostics;
using System.Security;
using Microsoft.VisualBasic.FileIO;
using Purg.Platform;

namespace Purg.Mover;

public enum DeleteMode
{
    RecycleBin,
    Permanent
}

public sealed record DeleteRequest(
    string SourcePath,
    FileIdentity? Identity,
    Guid? PortableRootId = null,
    string? PortableRelativePath = null);

public enum FileOperationStatus
{
    Deleted,
    Skipped,
    Failed
}

public sealed record FileOperationResult(FileOperationStatus Status, string? Reason);

public sealed record DeleteProgress(int Processed, int Total, int Deleted, int Skipped, int Failed);

public sealed record DeleteSummary(
    DeleteMode Mode,
    int Total,
    int Processed,
    int Deleted,
    int Skipped,
    int Failed,
    bool WasCancelled,
    IReadOnlyList<string> DeletedPaths,
    IReadOnlyDictionary<string, int> Reasons);

public static partial class Mover
{
    public const string ChangedAfterMarking = "ChangedAfterMarking";
    public const string CloudOnlyFile = "CloudOnlyFile";
    public const string LinkedFile = "LinkedFile";
    public const string FileUnavailable = "FileUnavailable";
    public const string OutsideChosenFolder = "OutsideChosenFolder";
    public const string DeleteUnverified = "DeleteUnverified";

    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    public static bool FolderLooksCloudSynced(string folderPath)
    {
        var normalized = Path.GetFullPath(folderPath);
        var pathSegments = normalized.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return pathSegments.Any(segment =>
            segment.Contains("OneDrive", StringComparison.OrdinalIgnoreCase) ||
            segment.Contains("Dropbox", StringComparison.OrdinalIgnoreCase) ||
            segment.Contains("Google Drive", StringComparison.OrdinalIgnoreCase) ||
            segment.Contains("GoogleDrive", StringComparison.OrdinalIgnoreCase));
    }

    public static DeleteSummary DeleteMarkedFiles(
        string folderPath,
        DeleteMode mode,
        IReadOnlyList<DeleteRequest> requests,
        IProgress<DeleteProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        if (!FolderValidator.TryValidate(folderPath, out var validatedFolder, out var error))
        {
            throw new InvalidOperationException(error);
        }

        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(validatedFolder));
        var normalizedRootWithSeparator = normalizedRoot + Path.DirectorySeparatorChar;
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

            var operation = DeleteOne(request, validatedFolder, mode);
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
            mode,
            requests.Count,
            processed,
            deleted,
            skipped,
            failed,
            cancellationToken.IsCancellationRequested,
            deletedPaths,
            reasons);

        FileOperationResult DeleteOne(DeleteRequest request, string expectedFolder, DeleteMode deleteMode)
        {
            try
            {
                var fullSource = Path.GetFullPath(request.SourcePath);
                if (!fullSource.StartsWith(normalizedRootWithSeparator, StringComparison.OrdinalIgnoreCase))
                {
                    return new FileOperationResult(FileOperationStatus.Failed, OutsideChosenFolder);
                }

                if (!FolderValidator.TryValidate(expectedFolder, out var currentFolder, out _) ||
                    !PathComparer.Equals(currentFolder, expectedFolder))
                {
                    return new FileOperationResult(FileOperationStatus.Failed, OutsideChosenFolder);
                }

                if (request.Identity is not { } expectedIdentity || !File.Exists(fullSource))
                {
                    return new FileOperationResult(FileOperationStatus.Skipped, ChangedAfterMarking);
                }

                var attributes = File.GetAttributes(fullSource);
                var unsafeReason = FileSafety.GetUnsafeReason(fullSource, attributes);
                if (unsafeReason == UnsafeFileReason.CloudOnly)
                {
                    return new FileOperationResult(FileOperationStatus.Skipped, CloudOnlyFile);
                }

                if (unsafeReason == UnsafeFileReason.Linked)
                {
                    return new FileOperationResult(FileOperationStatus.Skipped, LinkedFile);
                }

                if (ContainsLinkedParentDirectory(fullSource, expectedFolder))
                {
                    return new FileOperationResult(FileOperationStatus.Skipped, LinkedFile);
                }

                FileIdentity currentIdentity;
                try
                {
                    currentIdentity = SampledFileHash.ComputeAsync(fullSource).GetAwaiter().GetResult();
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                                   SecurityException or ArgumentException or NotSupportedException or
                                                   System.Security.Cryptography.CryptographicException)
                {
                    return new FileOperationResult(FileOperationStatus.Skipped, ChangedAfterMarking);
                }

                if (!IdentityMatches(expectedIdentity, currentIdentity))
                {
                    return new FileOperationResult(FileOperationStatus.Skipped, ChangedAfterMarking);
                }

                if (deleteMode == DeleteMode.RecycleBin)
                {
                    FileSystem.DeleteFile(fullSource, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                }
                else
                {
                    FileSystem.DeleteFile(fullSource, UIOption.OnlyErrorDialogs, RecycleOption.DeletePermanently);
                }

                if (File.Exists(fullSource))
                {
                    return new FileOperationResult(FileOperationStatus.Failed, DeleteUnverified);
                }

                deletedPaths.Add(fullSource);
                return new FileOperationResult(FileOperationStatus.Deleted, null);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                return new FileOperationResult(FileOperationStatus.Skipped, ChangedAfterMarking);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                               SecurityException or ArgumentException or NotSupportedException or
                                               InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return new FileOperationResult(FileOperationStatus.Failed, FileUnavailable);
            }
        }

        static bool ContainsLinkedParentDirectory(string filePath, string rootPath)
        {
            var directoryPath = Path.GetDirectoryName(filePath);
            while (directoryPath is not null &&
                   !PathComparer.Equals(Path.TrimEndingDirectorySeparator(directoryPath), rootPath))
            {
                var attributes = File.GetAttributes(directoryPath);
                if ((attributes & FileAttributes.ReparsePoint) != 0 &&
                    new DirectoryInfo(directoryPath).LinkTarget is not null)
                {
                    return true;
                }

                directoryPath = Path.GetDirectoryName(directoryPath);
            }

            return false;
        }
    }

    public static void OpenRecycleBin()
    {
        var startInfo = new ProcessStartInfo("explorer.exe")
        {
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("shell:RecycleBinFolder");
        using var process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException("Explorer could not be started.");
        }
    }

    private static bool IdentityMatches(FileIdentity expected, FileIdentity actual) =>
        expected.SizeBytes == actual.SizeBytes &&
        expected.LastWriteTimeUtc == actual.LastWriteTimeUtc &&
        string.Equals(expected.SampledSha256, actual.SampledSha256, StringComparison.OrdinalIgnoreCase);

    private static void AddReason(IDictionary<string, int> reasons, string reason)
    {
        reasons.TryGetValue(reason, out var count);
        reasons[reason] = count + 1;
    }
}
