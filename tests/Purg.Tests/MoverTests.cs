using System.IO;
using Purg.Mover;
using Purg.Platform;
using Xunit;
using FileMover = Purg.Mover.Mover;

namespace Purg.Tests;

public sealed class MoverTests
{
    [Fact]
    public async Task PermanentDeleteVerifiesTheFileIdentityAndRemoval()
    {
        using var folder = TestFolder.Create();
        var source = folder.AddFile("photo.jpg", new byte[] { 1, 2, 3, 4 });
        var identity = await SampledFileHash.ComputeAsync(source);

        var summary = FileMover.DeleteMarkedFiles(
            folder.Path,
            DeleteMode.Permanent,
            new[] { new DeleteRequest(source, identity) },
            progress: null,
            CancellationToken.None);

        Assert.Equal(1, summary.Processed);
        Assert.Equal(1, summary.Deleted);
        Assert.False(File.Exists(source));
        Assert.Contains(source, summary.DeletedPaths);
    }

    [Fact]
    public async Task PermanentDeleteAllowsAFileInANestedSubfolder()
    {
        using var folder = TestFolder.Create();
        var nestedFolder = System.IO.Path.Combine(folder.Path, "nested", "camera");
        Directory.CreateDirectory(nestedFolder);
        var source = System.IO.Path.Combine(nestedFolder, "photo.jpg");
        await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3, 4 });
        var identity = await SampledFileHash.ComputeAsync(source);

        var summary = FileMover.DeleteMarkedFiles(
            folder.Path,
            DeleteMode.Permanent,
            new[] { new DeleteRequest(source, identity) },
            progress: null,
            CancellationToken.None);

        Assert.Equal(1, summary.Processed);
        Assert.Equal(1, summary.Deleted);
        Assert.False(File.Exists(source));
        Assert.Contains(source, summary.DeletedPaths);
    }

    [Theory]
    [InlineData(DeleteMode.RecycleBin)]
    [InlineData(DeleteMode.Permanent)]
    public async Task ChangedFileIsSkippedWithoutDeletingIt(DeleteMode mode)
    {
        using var folder = TestFolder.Create();
        var source = folder.AddFile("changed.jpg", new byte[] { 1, 2, 3 });
        var identity = await SampledFileHash.ComputeAsync(source);
        await File.WriteAllBytesAsync(source, new byte[] { 4, 5, 6, 7 });

        var summary = FileMover.DeleteMarkedFiles(
            folder.Path,
            mode,
            new[] { new DeleteRequest(source, identity) },
            progress: null,
            CancellationToken.None);

        Assert.Equal(1, summary.Skipped);
        Assert.Equal(1, summary.Reasons[FileMover.ChangedAfterMarking]);
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task CloudPlaceholderIsSkippedWithoutDeletingIt()
    {
        using var folder = TestFolder.Create();
        var source = folder.AddFile("cloud.jpg", new byte[] { 1, 2, 3 });
        var identity = await SampledFileHash.ComputeAsync(source);
        var originalAttributes = File.GetAttributes(source);
        File.SetAttributes(source, originalAttributes | FileAttributes.Offline);
        try
        {
            var summary = FileMover.DeleteMarkedFiles(
                folder.Path,
                DeleteMode.Permanent,
                new[] { new DeleteRequest(source, identity) },
                progress: null,
                CancellationToken.None);

            Assert.Equal(1, summary.Skipped);
            Assert.Equal(1, summary.Reasons[FileMover.CloudOnlyFile]);
            Assert.True(File.Exists(source));
        }
        finally
        {
            File.SetAttributes(source, originalAttributes);
        }
    }

    [Fact]
    public void RecycleBinModeRequiresAnIdentityBeforeActing()
    {
        using var folder = TestFolder.Create();
        var source = folder.AddFile("unverified.jpg", new byte[] { 1, 2, 3 });

        var summary = FileMover.DeleteMarkedFiles(
            folder.Path,
            DeleteMode.RecycleBin,
            new[] { new DeleteRequest(source, null) },
            progress: null,
            CancellationToken.None);

        Assert.Equal(1, summary.Skipped);
        Assert.Equal(1, summary.Reasons[FileMover.ChangedAfterMarking]);
        Assert.True(File.Exists(source));
    }

    [Fact]
    public void PortableDeleteRequiresARecordedIdentityBeforeAccessingTheDevice()
    {
        var summary = FileMover.DeletePortableFiles(
            new[]
            {
                new DeleteRequest(
                    "portable:unverified/photo.jpg",
                    null,
                    Guid.NewGuid(),
                    "photo.jpg")
            },
            progress: null,
            CancellationToken.None);

        Assert.Equal(1, summary.Skipped);
        Assert.Equal(1, summary.Reasons[FileMover.ChangedAfterMarking]);
        Assert.Empty(summary.DeletedPaths);
    }

    [Fact]
    public async Task PreCancelledDeletionDoesNotProcessFiles()
    {
        using var folder = TestFolder.Create();
        var source = folder.AddFile("cancel.jpg", new byte[] { 1, 2, 3 });
        var identity = await SampledFileHash.ComputeAsync(source);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var summary = FileMover.DeleteMarkedFiles(
            folder.Path,
            DeleteMode.Permanent,
            new[] { new DeleteRequest(source, identity) },
            progress: null,
            cancellation.Token);

        Assert.True(summary.WasCancelled);
        Assert.Equal(0, summary.Processed);
        Assert.True(File.Exists(source));
    }

    private sealed class TestFolder : IDisposable
    {
        private TestFolder(string path) => Path = path;

        public string Path { get; }

        public static TestFolder Create()
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrWhiteSpace(documents))
            {
                throw new InvalidOperationException("The test environment has no documents folder.");
            }

            var path = System.IO.Path.Combine(documents, "PurgMoverTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TestFolder(path);
        }

        public string AddFile(string name, byte[] bytes)
        {
            var path = System.IO.Path.Combine(Path, name);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
