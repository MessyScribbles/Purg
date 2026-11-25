namespace Purg.Platform;

public enum UnsafeFileReason
{
    None,
    CloudOnly,
    Linked
}

public static class FileSafety
{
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
    private const FileAttributes CloudAttributes = FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess;

    public static UnsafeFileReason GetUnsafeReason(string path, FileAttributes attributes)
    {
        if ((attributes & CloudAttributes) != 0)
        {
            return UnsafeFileReason.CloudOnly;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            var fileInfo = new FileInfo(path);
            if (fileInfo.LinkTarget is not null)
            {
                return UnsafeFileReason.Linked;
            }
        }

        return UnsafeFileReason.None;
    }

    public static bool IsSafeToOpen(string path, FileAttributes attributes) =>
        GetUnsafeReason(path, attributes) == UnsafeFileReason.None;
}
