namespace WoWData.Core.Imaging;

/// <summary>
/// Decoded images by path, the most recently used kept up to a total size (pixel bytes), the least recently used
/// forgotten first. Thread-safe. A missing image (null) is remembered too, at no cost.
/// <para>After 0.14.0: the model resolver's texture cache used to keep every texture ever decoded: browsing many creatures,
/// items or map tiles grew the memory without end (about 1 MB per new model, found by `--leaktest`), and the 3D views
/// kept the GPU copy of every one of them too (a view frees a texture once its image is collected).</para>
/// </summary>
public sealed class ImageCache
{
    private readonly Dictionary<string, LinkedListNode<(string Key, RgbaImage? Image)>> _map = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<(string Key, RgbaImage? Image)> _lru = new();
    private long _bytes;

    /// <summary>The most pixel bytes kept (default 32 MB: 32 textures of 512 x 512, 128 of 256 x 256). Decoding a texture
    /// again takes a millisecond or two; the models shown keep their own textures (ModelAsset), so this only spares
    /// the decoding when going back to a recent model, skin or look.</summary>
    public long MaxBytes { get; }

    public ImageCache(long maxBytes = 32L << 20) { MaxBytes = Math.Max(0, maxBytes); }

    public int Count { get { lock (_map) return _map.Count; } }
    public long Bytes { get { lock (_map) return _bytes; } }

    private static long SizeOf(RgbaImage? img) => img?.Pixels.LongLength ?? 0;

    /// <summary>The cached image of <paramref name="key"/>, else <paramref name="load"/>'s (run outside the lock: two
    /// threads asking for the same new key may both decode it; the first one is kept).</summary>
    public RgbaImage? GetOrAdd(string key, Func<string, RgbaImage?> load)
    {
        lock (_map)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node.Value.Image;
            }
        }
        var img = load(key);
        lock (_map)
        {
            if (_map.TryGetValue(key, out var node)) return node.Value.Image;
            _map[key] = _lru.AddFirst((key, img));
            _bytes += SizeOf(img);
            while (_bytes > MaxBytes && _lru.Count > 1)
            {
                var last = _lru.Last!;
                _lru.RemoveLast();
                _map.Remove(last.Value.Key);
                _bytes -= SizeOf(last.Value.Image);
            }
            return img;
        }
    }

    public bool Contains(string key) { lock (_map) return _map.ContainsKey(key); }

    public void Remove(string key)
    {
        lock (_map)
        {
            if (!_map.Remove(key, out var node)) return;
            _lru.Remove(node);
            _bytes -= SizeOf(node.Value.Image);
        }
    }

    public void Clear()
    {
        lock (_map)
        {
            _map.Clear();
            _lru.Clear();
            _bytes = 0;
        }
    }
}
