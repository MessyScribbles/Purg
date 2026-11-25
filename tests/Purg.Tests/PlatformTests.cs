using System.IO;
using System.Resources;
using Purg.App;
using Purg.Platform;
using Purg.Scan;
using Xunit;

namespace Purg.Tests;

public sealed class PlatformTests
{
    [Fact]
    public void UiCopyIsEmbeddedInTheResourceFile()
    {
        var strings = new ResourceManager("Purg.App.Resources.Strings", typeof(MainWindow).Assembly);

        Assert.Equal("purg", strings.GetString("Brand"));
        Assert.Equal("Warning: Permanently deleted photos cannot be recovered or undone.", strings.GetString("PermanentDeleteWarning"));
        Assert.Equal("Changed after marking", strings.GetString("ChangedAfterMarking"));
    }

    [Fact]
    public void DisplayNameSanitizerRemovesControlsAndBidiOverrides()
    {
        var result = DisplayNameSanitizer.Sanitize("holiday\u202Egpj.exe\u0001.jpg");

        Assert.Equal("holidaygpj.exe.jpg", result);
    }

    [Fact]
    public void CloudOnlyAttributesAreRejectedWithoutOpeningTheFile()
    {
        var reason = FileSafety.GetUnsafeReason("not-opened.jpg", FileAttributes.Offline);

        Assert.Equal(UnsafeFileReason.CloudOnly, reason);
    }

    [Fact]
    public void ReparsePointWithoutLinkTargetIsNotTreatedAsAnExternalLink()
    {
        var reason = FileSafety.GetUnsafeReason("not-opened.jpg", FileAttributes.ReparsePoint);

        Assert.Equal(UnsafeFileReason.None, reason);
    }

    [Fact]
    public void FolderValidatorChecksEveryAncestorForReparsePoints()
    {
        var root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))!;
        var link = Path.Combine(root, "alias");
        var selectedFolder = Path.Combine(link, "selected");
        var checkedPaths = new List<string>();

        var hasReparsePoint = FolderValidator.ContainsReparsePoint(
            selectedFolder,
            root,
            path =>
            {
                checkedPaths.Add(path);
                return string.Equals(path, link, StringComparison.OrdinalIgnoreCase)
                    ? FileAttributes.ReparsePoint
                    : FileAttributes.Directory;
            });

        Assert.True(hasReparsePoint);
        Assert.Contains(link, checkedPaths, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ScannerFindsEverySupportedExtensionWithoutOpeningFiles()
    {
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var names = new[]
        {
            "one.JPG", "two.jpeg", "three.jpe", "four.jfif", "five.PNG", "six.webp", "seven.avif",
            "eight.bmp", "nine.GIF", "ten.tif", "eleven.TIFF", "twelve.heic", "thirteen.heif",
            "fourteen.ico", "fifteen.wdp", "sixteen.hdp", "seventeen.jxr"
        };
        var progress = new ScanProgressCollector();
        try
        {
            foreach (var name in names)
            {
                await File.WriteAllBytesAsync(Path.Combine(folder, name), Array.Empty<byte>());
            }

            await File.WriteAllBytesAsync(Path.Combine(folder, "unsafe.svg"), Array.Empty<byte>());
            await File.WriteAllBytesAsync(Path.Combine(folder, "document.pdf"), Array.Empty<byte>());

            var result = await FolderScanner.ScanAsync(folder, progress: progress);

            Assert.Equal(names.Length, result.Photos.Count);
            Assert.DoesNotContain(result.Photos, photo => Path.GetExtension(photo.Path) is ".svg" or ".pdf");
            Assert.Contains(progress.Updates, update => update.FilesScanned == names.Length + 2);
            Assert.Contains(progress.Updates, update => update.ImagesFound == names.Length);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task ScannerIncludesNestedFilesAndSkipsLegacyDestinationFolders()
    {
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var nestedFolder = Path.Combine(folder, "nested", "deeper");
        var legacyFolder = Path.Combine(folder, "_TO_DELETE_20200101");
        Directory.CreateDirectory(nestedFolder);
        Directory.CreateDirectory(legacyFolder);
        var nestedFirst = Path.Combine(nestedFolder, "same.jpg");
        var nestedSecond = Path.Combine(folder, "nested", "same.jpg");
        var legacyPhoto = Path.Combine(legacyFolder, "ignored.jpg");
        try
        {
            await File.WriteAllBytesAsync(nestedFirst, Array.Empty<byte>());
            await File.WriteAllBytesAsync(nestedSecond, Array.Empty<byte>());
            await File.WriteAllBytesAsync(legacyPhoto, Array.Empty<byte>());

            var result = await FolderScanner.ScanAsync(folder);

            Assert.Equal(2, result.Photos.Count);
            Assert.Contains(result.Photos, photo => photo.Path == Path.GetFullPath(nestedFirst));
            Assert.Contains(result.Photos, photo => photo.Path == Path.GetFullPath(nestedSecond));
            Assert.DoesNotContain(result.Photos, photo => photo.Path == Path.GetFullPath(legacyPhoto));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData("../outside.jpg")]
    [InlineData("nested/../../outside.jpg")]
    [InlineData(@"nested\..\outside.jpg")]
    [InlineData("C:/outside.jpg")]
    public async Task PortableDevicePathsRejectTraversal(string relativePath)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            PortableDeviceStorage.ResolveFileAsync(Guid.NewGuid(), relativePath));
    }

    private sealed class ScanProgressCollector : IProgress<ScanProgress>
    {
        private readonly List<ScanProgress> _updates = new();

        public IReadOnlyList<ScanProgress> Updates => _updates;

        public void Report(ScanProgress value) => _updates.Add(value);
    }

    [Fact]
    public async Task SampledHashRecordsIdentityForSmallFiles()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".bin");
        var content = new byte[] { 1, 2, 3, 4, 5 };
        await File.WriteAllBytesAsync(path, content);
        try
        {
            var identity = await SampledFileHash.ComputeAsync(path);

            Assert.Equal(content.Length, identity.SizeBytes);
            Assert.Equal(64, identity.SampledSha256.Length);
            Assert.Equal(File.GetLastWriteTimeUtc(path), identity.LastWriteTimeUtc);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
