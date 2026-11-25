using System.Collections.Concurrent;
using System.Security.Cryptography;
using Windows.Devices.Enumeration;
using Windows.Devices.Portable;
using Windows.Storage;

namespace Purg.Platform;

public sealed record PortableDeviceInfo(string Id, string Name);

public sealed record PortableFolderInfo(string Name, string RelativePath);

public static class PortableDeviceStorage
{
    private const int SampleLength = 64 * 1024;
    private const long MaximumPreviewBytes = 96 * 1024 * 1024;
    private static readonly ConcurrentDictionary<Guid, StorageFolder> Roots = new();

    public static async Task<IReadOnlyList<PortableDeviceInfo>> GetDevicesAsync()
    {
        var devices = await DeviceInformation.FindAllAsync(StorageDevice.GetDeviceSelector());
        return devices
            .Select(device => new PortableDeviceInfo(
                device.Id,
                DisplayNameSanitizer.Sanitize(device.Name) is { Length: > 0 } name ? name : "Portable device"))
            .OrderBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static async Task<Guid> RegisterDeviceRootAsync(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        var root = StorageDevice.FromId(deviceId) ??
                   throw new IOException("The portable device is unavailable.");
        await root.GetBasicPropertiesAsync();
        return RegisterRoot(root);
    }

    public static Guid RegisterRoot(StorageFolder folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var id = Guid.NewGuid();
        if (!Roots.TryAdd(id, folder))
        {
            throw new InvalidOperationException("The portable folder could not be registered.");
        }

        return id;
    }

    public static async Task<IReadOnlyList<PortableFolderInfo>> GetFoldersAsync(
        Guid rootId,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        var folder = await ResolveFolderAsync(rootId, relativePath);
        cancellationToken.ThrowIfCancellationRequested();
        var folders = await folder.GetFoldersAsync();
        cancellationToken.ThrowIfCancellationRequested();
        return folders
            .Select(item => new PortableFolderInfo(
                DisplayNameSanitizer.Sanitize(item.Name),
                CombineRelativePath(relativePath, item.Name)))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static Task<StorageFolder> GetFolderAsync(Guid rootId, string relativePath) =>
        ResolveFolderAsync(rootId, relativePath);

    public static async Task<byte[]> ReadAllBytesAsync(
        Guid rootId,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        var file = await ResolveFileAsync(rootId, relativePath).ConfigureAwait(false);
        var randomAccessStream = await file.OpenReadAsync().AsTask().ConfigureAwait(false);
        using (randomAccessStream)
        {
            if (randomAccessStream.Size > (ulong)MaximumPreviewBytes)
            {
                throw new IOException("The image is too large to preview from a portable device.");
            }

            using var source = randomAccessStream.AsStreamForRead();
            using var target = new MemoryStream(checked((int)randomAccessStream.Size));
            var buffer = new byte[81920];
            long totalRead = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                totalRead += read;
                if (totalRead > MaximumPreviewBytes)
                {
                    throw new IOException("The portable image changed while it was being read.");
                }

                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            if (totalRead != checked((long)randomAccessStream.Size))
            {
                throw new IOException("The portable image changed while it was being read.");
            }

            return target.GetBuffer();
        }
    }

    public static async Task<FileIdentity> ComputeIdentityAsync(
        Guid rootId,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        var file = await ResolveFileAsync(rootId, relativePath).ConfigureAwait(false);
        var basicProperties = await file.GetBasicPropertiesAsync().AsTask().ConfigureAwait(false);
        var randomAccessStream = await file.OpenReadAsync().AsTask().ConfigureAwait(false);
        using (randomAccessStream)
        using (var stream = randomAccessStream.AsStreamForRead())
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var size = checked((long)randomAccessStream.Size);
            var lastWrite = basicProperties.DateModified.UtcDateTime;
            var buffer = new byte[SampleLength];
            if (size < SampleLength * 3L)
            {
                await AppendRangeAsync(stream, hash, buffer, 0, size, cancellationToken);
            }
            else
            {
                await AppendRangeAsync(stream, hash, buffer, 0, SampleLength, cancellationToken);
                await AppendRangeAsync(
                    stream,
                    hash,
                    buffer,
                    size / 2 - SampleLength / 2,
                    SampleLength,
                    cancellationToken);
                await AppendRangeAsync(
                    stream,
                    hash,
                    buffer,
                    size - SampleLength,
                    SampleLength,
                    cancellationToken);
            }

            Span<byte> sizeBytes = stackalloc byte[sizeof(long)];
            BitConverter.TryWriteBytes(sizeBytes, size);
            hash.AppendData(sizeBytes);
            var latestProperties = await file.GetBasicPropertiesAsync().AsTask().ConfigureAwait(false);
            if (checked((long)randomAccessStream.Size) != size ||
                latestProperties.DateModified.UtcDateTime != lastWrite)
            {
                throw new IOException("The portable file changed while its identity was being checked.");
            }

            return new FileIdentity(size, lastWrite, Convert.ToHexString(hash.GetHashAndReset()));
        }
    }

    public static async Task<StorageFile> ResolveFileAsync(Guid rootId, string relativePath)
    {
        var segments = GetPathSegments(relativePath);
        if (segments.Length == 0)
        {
            throw new ArgumentException("A portable file path is required.", nameof(relativePath));
        }

        var folder = await ResolveFolderAsync(rootId, string.Join('/', segments[..^1]));
        return await folder.GetFileAsync(segments[^1]).AsTask().ConfigureAwait(false);
    }

    public static void ReleaseRoot(Guid rootId) => Roots.TryRemove(rootId, out _);

    public static string CombineRelativePath(string parent, string child) =>
        string.IsNullOrEmpty(parent) ? child : $"{parent}/{child}";

    private static async Task<StorageFolder> ResolveFolderAsync(Guid rootId, string relativePath)
    {
        if (!Roots.TryGetValue(rootId, out var folder))
        {
            throw new IOException("The portable folder is no longer available.");
        }

        foreach (var segment in GetPathSegments(relativePath))
        {
            folder = await folder.GetFolderAsync(segment).AsTask().ConfigureAwait(false);
        }

        return folder;
    }

    private static string[] GetPathSegments(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".." || segment.Contains('\\') || segment.Contains(':')))
        {
            throw new ArgumentException("The portable path is invalid.", nameof(relativePath));
        }

        return segments;
    }

    private static async Task AppendRangeAsync(
        Stream stream,
        IncrementalHash hash,
        byte[] buffer,
        long offset,
        long length,
        CancellationToken cancellationToken)
    {
        stream.Position = offset;
        while (length > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requested = (int)Math.Min(buffer.Length, length);
            var read = await stream.ReadAsync(buffer.AsMemory(0, requested), cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("The portable file changed while its identity was being checked.");
            }

            hash.AppendData(buffer, 0, read);
            length -= read;
        }
    }
}
