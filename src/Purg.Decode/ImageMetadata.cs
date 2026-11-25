namespace Purg.Decode;

public sealed record ImageMetadata(
    int Width,
    int Height,
    DateTime? DateTaken,
    string? CameraModel,
    int Orientation);

public sealed record DecodedImage(
    System.Windows.Media.Imaging.BitmapSource? Bitmap,
    ImageMetadata? Metadata,
    string? UnavailableReason);

public enum ImageDecodeSize
{
    Fit,
    Thumbnail
}
