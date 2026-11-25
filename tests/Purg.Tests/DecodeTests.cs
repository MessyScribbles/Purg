using System.Buffers.Binary;
using System.IO;
using Purg.Decode;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace Purg.Tests;

public sealed class DecodeTests
{
    [Fact]
    public async Task BitmapDecodeOrientsDimensionsAndReleasesItsFileHandle()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".bmp");
        await File.WriteAllBytesAsync(path, OnePixelBitmap);
        var decoder = new ImageDecoder();
        try
        {
            var viewId = decoder.BeginView();
            var decoded = await decoder.DecodeAsync(path, ImageDecodeSize.Fit, viewId);

            Assert.NotNull(decoded.Bitmap);
            Assert.True(decoded.Bitmap!.IsFrozen);
            Assert.Equal(1, decoded.Metadata!.Width);
            Assert.Equal(1, decoded.Metadata.Height);

            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.Equal(58, exclusive.Length);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task MemoryBackedDecodeLoadsAndFreezesWithoutWritingAnImageFile()
    {
        var decoder = new ImageDecoder();

        var result = await decoder.DecodeAsync(
            OnePixelBitmap,
            "portable-photo.bmp",
            ImageDecodeSize.Fit,
            decoder.BeginView());

        Assert.NotNull(result.Bitmap);
        Assert.True(result.Bitmap!.IsFrozen);
        Assert.Equal(1, result.Metadata!.Width);
        Assert.Equal(1, result.Metadata.Height);
    }

    [Fact]
    public async Task WicDecoderLoadsAllBuiltInFormatsAndReleasesEachStream()
    {
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var decoder = new ImageDecoder();
        var source = CreateTwoPixelImage();
        var formats = new (string Extension, BitmapEncoder Encoder)[]
        {
            (".jpg", new JpegBitmapEncoder()),
            (".jpe", new JpegBitmapEncoder()),
            (".jfif", new JpegBitmapEncoder()),
            (".png", new PngBitmapEncoder()),
            (".bmp", new BmpBitmapEncoder()),
            (".gif", new GifBitmapEncoder()),
            (".tif", new TiffBitmapEncoder())
        };
        var imagePaths = new List<string>();

        try
        {
            foreach (var (extension, encoder) in formats)
            {
                var path = Path.Combine(folder, Guid.NewGuid().ToString("N") + extension);
                imagePaths.Add(path);
                encoder.Frames.Add(BitmapFrame.Create(source));
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    encoder.Save(stream);
                }
            }

            foreach (var path in imagePaths)
            {
                var decoded = await decoder.DecodeAsync(path, ImageDecodeSize.Fit, decoder.BeginView());

                Assert.NotNull(decoded.Bitmap);
                Assert.True(decoded.Bitmap!.IsFrozen);
                if (Path.GetExtension(path).Equals(".jpe", StringComparison.OrdinalIgnoreCase))
                {
                    Assert.Equal(2, decoded.Metadata!.Width);
                    Assert.Equal(1, decoded.Metadata.Height);
                }

                using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }

            var webpPath = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".webp");
            await File.WriteAllBytesAsync(webpPath, Array.Empty<byte>());
            var webp = await decoder.DecodeAsync(webpPath, ImageDecodeSize.Fit, decoder.BeginView());
            Assert.Null(webp.Bitmap);
            Assert.Contains(webp.UnavailableReason, new[] { "CodecMissing", "Unavailable" });

            var avifPath = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".avif");
            await File.WriteAllBytesAsync(avifPath, Array.Empty<byte>());
            var avif = await decoder.DecodeAsync(avifPath, ImageDecodeSize.Fit, decoder.BeginView());
            Assert.Null(avif.Bitmap);
            Assert.Contains(avif.UnavailableReason, new[] { "CodecMissing", "Unavailable" });
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task ExifOrientationRotatesTheDecodedPixelsAndDimensions()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".jpg");
        var metadata = new BitmapMetadata("jpg");
        metadata.SetQuery("/app1/ifd/{ushort=274}", (ushort)6);
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(CreateTwoPixelImage(), null, metadata, null));
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            encoder.Save(stream);
        }

        var decoder = new ImageDecoder();
        try
        {
            var result = await decoder.DecodeAsync(path, ImageDecodeSize.Fit, decoder.BeginView());

            Assert.NotNull(result.Bitmap);
            Assert.Equal(1, result.Metadata!.Width);
            Assert.Equal(2, result.Metadata.Height);
            Assert.Equal(1, result.Bitmap!.PixelWidth);
            Assert.Equal(2, result.Bitmap.PixelHeight);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static BitmapSource CreateTwoPixelImage()
    {
        var source = BitmapSource.Create(
            2,
            1,
            96,
            96,
            PixelFormats.Bgr32,
            null,
            new byte[] { 0, 0, 255, 0, 0, 255, 0, 0 },
            8);
        source.Freeze();
        return source;
    }

    private static byte[] OnePixelBitmap
    {
        get
        {
            var bytes = new byte[58];
            bytes[0] = (byte)'B';
            bytes[1] = (byte)'M';
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2), bytes.Length);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(10), 54);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(14), 40);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), 1);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), 1);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(26), 1);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(28), 24);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(34), 4);
            return bytes;
        }
    }
}
