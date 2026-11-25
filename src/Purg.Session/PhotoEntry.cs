namespace Purg.Session;

public enum PhotoState
{
    Undecided,
    Keep,
    Del,
    Gone
}

public sealed class PhotoEntry
{
    public PhotoEntry(
        string path,
        string displayName,
        long sizeBytes,
        DateTime lastWriteTimeUtc,
        Guid? portableRootId = null,
        string? portableRelativePath = null)
    {
        Path = path;
        DisplayName = displayName;
        SizeBytes = sizeBytes;
        LastWriteTimeUtc = lastWriteTimeUtc;
        PortableRootId = portableRootId;
        PortableRelativePath = portableRelativePath;
    }

    public string Path { get; }
    public string DisplayName { get; }
    public long SizeBytes { get; }
    public DateTime LastWriteTimeUtc { get; }
    public Guid? PortableRootId { get; }
    public string? PortableRelativePath { get; }
    public bool IsPortable => PortableRootId.HasValue;
    public PhotoState State { get; internal set; }
}
