using System.Windows.Media.Imaging;

namespace Purg.Decode;

public sealed class ImageFrameCache
{
    private readonly Dictionary<string, BitmapSource> _mainFrames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BitmapSource> _thumbnails = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _mainOrder = new();
    private readonly Queue<string> _thumbnailOrder = new();

    public bool TryGet(string path, ImageDecodeSize size, out BitmapSource? bitmap)
    {
        var cache = size == ImageDecodeSize.Thumbnail ? _thumbnails : _mainFrames;
        return cache.TryGetValue(path, out bitmap);
    }

    public void Add(string path, ImageDecodeSize size, BitmapSource bitmap)
    {
        var cache = size == ImageDecodeSize.Thumbnail ? _thumbnails : _mainFrames;
        var order = size == ImageDecodeSize.Thumbnail ? _thumbnailOrder : _mainOrder;
        var capacity = size == ImageDecodeSize.Thumbnail ? 9 : 3;
        if (!cache.ContainsKey(path))
        {
            order.Enqueue(path);
        }

        cache[path] = bitmap;
        while (cache.Count > capacity && order.TryDequeue(out var oldest))
        {
            cache.Remove(oldest);
        }
    }

    public void Remove(string path, ImageDecodeSize size)
    {
        var cache = size == ImageDecodeSize.Thumbnail ? _thumbnails : _mainFrames;
        var order = size == ImageDecodeSize.Thumbnail ? _thumbnailOrder : _mainOrder;
        cache.Remove(path);
        var retained = order.Where(cache.ContainsKey).ToArray();
        order.Clear();
        foreach (var item in retained)
        {
            order.Enqueue(item);
        }
    }

    public void Clear()
    {
        _mainFrames.Clear();
        _thumbnails.Clear();
        _mainOrder.Clear();
        _thumbnailOrder.Clear();
    }

    public void KeepThumbnailWindow(IEnumerable<string> paths)
    {
        var keep = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        foreach (var key in _thumbnails.Keys.Where(key => !keep.Contains(key)).ToArray())
        {
            _thumbnails.Remove(key);
        }

        var retained = _thumbnailOrder.Where(_thumbnails.ContainsKey).ToArray();
        _thumbnailOrder.Clear();
        foreach (var path in retained)
        {
            _thumbnailOrder.Enqueue(path);
        }
    }
}
