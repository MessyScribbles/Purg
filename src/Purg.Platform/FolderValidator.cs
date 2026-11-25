namespace Purg.Platform;

public static class FolderValidator
{
    public static bool TryValidate(string? folderPath, out string fullPath, out string error)
    {
        fullPath = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(folderPath))
        {
            error = "FolderRequired";
            return false;
        }

        try
        {
            var candidate = Path.GetFullPath(folderPath.Trim());
            if (candidate.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase) ||
                candidate.StartsWith(@"\\wsl$", StringComparison.OrdinalIgnoreCase) ||
                candidate.StartsWith(@"\\wsl.localhost", StringComparison.OrdinalIgnoreCase))
            {
                error = "LocalFolderRequired";
                return false;
            }

            var root = Path.GetPathRoot(candidate);
            if (string.IsNullOrEmpty(root) || string.Equals(
                    Path.TrimEndingDirectorySeparator(candidate),
                    Path.TrimEndingDirectorySeparator(root),
                    StringComparison.OrdinalIgnoreCase))
            {
                error = "DriveRootNotAllowed";
                return false;
            }

            var drive = new DriveInfo(root);
            if (!drive.IsReady || (drive.DriveType != DriveType.Fixed && drive.DriveType != DriveType.Removable))
            {
                error = "LocalFolderRequired";
                return false;
            }

            var directory = new DirectoryInfo(candidate);
            if (!directory.Exists)
            {
                error = "FolderUnavailable";
                return false;
            }

            if (ContainsReparsePoint(candidate, root, File.GetAttributes))
            {
                error = "RegularFolderRequired";
                return false;
            }

            if (IsProtectedLocation(candidate))
            {
                error = "ProtectedFolder";
                return false;
            }

            fullPath = Path.TrimEndingDirectorySeparator(candidate);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or
                                           UnauthorizedAccessException or NotSupportedException)
        {
            error = "FolderUnavailable";
            return false;
        }
    }

    private static bool IsProtectedLocation(string candidate)
    {
        var protectedRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        };

        foreach (var protectedRoot in protectedRoots.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            if (IsSameOrChild(candidate, protectedRoot))
            {
                return true;
            }
        }

        var localLow = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "AppData",
            "LocalLow");
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return IsSameOrChild(candidate, localLow) ||
               (!string.IsNullOrWhiteSpace(profile) &&
                string.Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate)),
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(profile)),
                    StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSameOrChild(string candidate, string parent)
    {
        var normalizedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        return string.Equals(normalizedCandidate, normalizedParent, StringComparison.OrdinalIgnoreCase) ||
               normalizedCandidate.StartsWith(normalizedParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool ContainsReparsePoint(
        string candidate,
        string volumeRoot,
        Func<string, FileAttributes> getAttributes)
    {
        var current = Path.TrimEndingDirectorySeparator(candidate);
        var normalizedRoot = Path.TrimEndingDirectorySeparator(volumeRoot);
        while (!string.Equals(current, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            if ((getAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) ||
                string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = Path.TrimEndingDirectorySeparator(parent);
        }

        return false;
    }
}
