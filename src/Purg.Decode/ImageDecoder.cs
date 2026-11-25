using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Purg.Decode;

public sealed class ImageDecoder
{
    private const long JpegPixelLimit = 40_000_000;
    private const long OtherPixelLimit = 12_000_000;
    private const long FitPixelLimit = 4_000_000;
    private readonly BlockingCollection<DecodeRequest> _requests = new();
    private readonly Thread _worker;
    private long _latestRequest;

    public ImageDecoder()
    {
        _worker = new Thread(ProcessRequests)
        {
            IsBackground = true,
            Name = "purg image decode worker",
            Priority = ThreadPriority.Normal
        };
        _worker.Start();
    }

    public long BeginView() => Interlocked.Increment(ref _latestRequest);

    public Task<DecodedImage> DecodeAsync(string path, ImageDecodeSize size, long viewId)
    {
        var completion = new TaskCompletionSource<DecodedImage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _requests.Add(new DecodeRequest(viewId, path, null, Path.GetExtension(path), size, completion));
        return completion.Task;
    }

    public Task<DecodedImage> DecodeAsync(byte[] content, string fileName, ImageDecodeSize size, long viewId)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        var completion = new TaskCompletionSource<DecodedImage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _requests.Add(new DecodeRequest(viewId, fileName, content, Path.GetExtension(fileName), size, completion));
        return completion.Task;
    }

    private void ProcessRequests()
    {
        foreach (var request in _requests.GetConsumingEnumerable())
        {
            if (request.ViewId != Interlocked.Read(ref _latestRequest))
            {
                request.Content = null;
                request.Completion.TrySetCanceled();
                continue;
            }

            try
            {
                request.Completion.TrySetResult(Decode(request));
            }
            catch (CodecUnavailableException)
            {
                request.Completion.TrySetResult(new DecodedImage(null, null, "CodecMissing"));
            }
            catch (Exception exception) when (ExtensionIsCodecDependent(request.Extension) &&
                                               IsCodecFailure(exception))
            {
                request.Completion.TrySetResult(new DecodedImage(null, null, "CodecMissing"));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                               NotSupportedException or InvalidOperationException or
                                               FileFormatException or ArgumentException or
                                               OverflowException or System.Runtime.InteropServices.COMException)
            {
                request.Completion.TrySetResult(new DecodedImage(null, null, "Unavailable"));
            }
            finally
            {
                request.Content = null;
            }
        }
    }

    private static DecodedImage Decode(DecodeRequest request)
    {
        Stream OpenStream() => request.Content is null
            ? new FileStream(
                request.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete)
            : new MemoryStream(request.Content, writable: false);

        ImageMetadata metadata;
        using (var headerStream = OpenStream())
        {
            BitmapDecoder decoder;
            try
            {
                decoder = BitmapDecoder.Create(
                    headerStream,
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnDemand);
            }
            catch (Exception exception) when (ExtensionIsCodecDependent(request.Extension) && IsCodecFailure(exception))
            {
                throw new CodecUnavailableException(exception);
            }

            var frame = decoder.Frames[0];
            var orientation = ReadOrientation(frame.Metadata);
            var width = frame.PixelWidth;
            var height = frame.PixelHeight;
            var rotated = orientation is 5 or 6 or 7 or 8;
            var orientedWidth = rotated ? height : width;
            var orientedHeight = rotated ? width : height;
            metadata = new ImageMetadata(
                orientedWidth,
                orientedHeight,
                ReadDateTaken(frame.Metadata),
                ReadCameraModel(frame.Metadata),
                orientation);
        }

        var pixelLimit = ExtensionIsJpeg(request.Extension) ? JpegPixelLimit : OtherPixelLimit;
        var sourcePixels = (long)metadata.Width * metadata.Height;
        if (sourcePixels > pixelLimit)
        {
            return new DecodedImage(null, metadata, "TooLarge");
        }

        var decodeLimit = request.Size switch
        {
            ImageDecodeSize.Thumbnail => 96L * 96L,
            _ => FitPixelLimit
        };
        var targetWidth = metadata.Width;
        if (request.Size == ImageDecodeSize.Thumbnail)
        {
            targetWidth = Math.Min(96, metadata.Width);
        }
        else if (sourcePixels > decodeLimit)
        {
            targetWidth = Math.Max(1, (int)Math.Floor(Math.Sqrt(decodeLimit * (double)metadata.Width / metadata.Height)));
        }

        BitmapImage bitmap;
        using (var stream = OpenStream())
        {
            bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            bitmap.StreamSource = stream;
            if (metadata.Orientation is 5 or 6 or 7 or 8)
            {
                bitmap.DecodePixelHeight = Math.Min(targetWidth, metadata.Width);
            }
            else
            {
                bitmap.DecodePixelWidth = targetWidth;
            }

            bitmap.EndInit();
        }

        BitmapSource result = ApplyOrientation(bitmap, metadata.Orientation);
        result.Freeze();
        return new DecodedImage(result, metadata, null);
    }

    private static BitmapSource ApplyOrientation(BitmapSource source, int orientation)
    {
        var width = source.PixelWidth;
        var height = source.PixelHeight;
        var matrix = orientation switch
        {
            2 => new Matrix(-1, 0, 0, 1, width, 0),
            3 => new Matrix(-1, 0, 0, -1, width, height),
            4 => new Matrix(1, 0, 0, -1, 0, height),
            5 => new Matrix(0, 1, 1, 0, 0, 0),
            6 => new Matrix(0, 1, -1, 0, height, 0),
            7 => new Matrix(0, -1, -1, 0, height, width),
            8 => new Matrix(0, -1, 1, 0, 0, width),
            _ => Matrix.Identity
        };

        if (orientation == 1 || orientation is < 2 or > 8)
        {
            return source;
        }

        var transformed = new TransformedBitmap(source, new MatrixTransform(matrix));
        if (transformed.CanFreeze)
        {
            transformed.Freeze();
        }

        return transformed;
    }

    private static int ReadOrientation(object? metadata)
    {
        if (metadata is not BitmapMetadata bitmapMetadata)
        {
            return 1;
        }

        try
        {
            var value = bitmapMetadata.GetQuery("/app1/ifd/{ushort=274}");
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or
                                           NotSupportedException or FormatException or OverflowException or
                                           System.Runtime.InteropServices.COMException)
        {
            return 1;
        }
    }

    private static DateTime? ReadDateTaken(object? metadata)
    {
        if (metadata is not BitmapMetadata bitmapMetadata)
        {
            return null;
        }

        foreach (var query in new[] { "/app1/ifd/exif/{ushort=36867}", "/app1/ifd/{ushort=306}" })
        {
            try
            {
                var value = bitmapMetadata.GetQuery(query)?.ToString();
                if (DateTime.TryParseExact(
                        value,
                        new[] { "yyyy:MM:dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss" },
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeLocal,
                        out var date))
                {
                    return date;
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or
                                               NotSupportedException or System.Runtime.InteropServices.COMException)
            {
            }
        }

        return null;
    }

    private static string? ReadCameraModel(object? metadata)
    {
        if (metadata is not BitmapMetadata bitmapMetadata)
        {
            return null;
        }

        try
        {
            return bitmapMetadata.GetQuery("/app1/ifd/{ushort=272}")?.ToString();
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or
                                           NotSupportedException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    private static bool ExtensionIsJpeg(string extension) =>
        extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".jpe", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".jfif", StringComparison.OrdinalIgnoreCase);

    private static bool ExtensionIsCodecDependent(string extension) =>
        extension.Equals(".webp", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".avif", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".heic", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".heif", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".wdp", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".hdp", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".jxr", StringComparison.OrdinalIgnoreCase);

    private static bool IsCodecFailure(Exception exception) =>
        exception is FileFormatException or NotSupportedException or
            System.Runtime.InteropServices.COMException;

    private sealed class CodecUnavailableException : Exception
    {
        public CodecUnavailableException(Exception innerException)
            : base("The image codec is unavailable.", innerException)
        {
        }
    }

    private sealed record DecodeRequest(
        long ViewId,
        string Path,
        byte[]? Content,
        string Extension,
        ImageDecodeSize Size,
        TaskCompletionSource<DecodedImage> Completion)
    {
        public byte[]? Content { get; set; } = Content;
    }
}
