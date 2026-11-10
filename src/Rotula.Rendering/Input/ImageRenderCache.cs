using System.Runtime.CompilerServices;
using Rotula.Core.Elements;
using Rotula.Core.Imaging;
using Rotula.Core.Model;

namespace Rotula.Rendering.Input;

/// <summary>
/// Limits and memoisation for the preview of image elements.
/// <para>
/// <b>Limit.</b> Scaling and dithering allocate several buffers of <c>width * height</c> pixels, so a design that asks for a huge target
/// (a hostile file, or a typo) must be refused before anything is allocated. The sizes are checked with 64-bit arithmetic because the
/// 32-bit product wraps around: 65536 x 65536 is 0 as an <see cref="int"/>.
/// </para>
/// <para>
/// <b>Cache.</b> Dragging an object re-renders the whole label on every mouse move. Without a cache the scale, the error-diffusion dither,
/// the rotation and the PNG encoding of every image on the label were repeated for each frame although nothing about the picture changed.
/// The key is the identity of the source bitmap (it is immutable, so identity is as good as content and costs nothing) plus every setting
/// that changes the dots. Memory is bounded and least recently used entries go first.
/// </para>
/// </summary>
internal static class ImageRenderCache
{
    /// <summary>
    /// Largest target, in pixels, that is dithered for the preview. 40 million pixels is far more than any label printed at 600 dpi
    /// (a full 4 x 6 inch label is about 8.6 million) and keeps the transient buffers of the dither near 360 MB.
    /// It is the same limit as the pixel cap of <c>ImageProcessor</c> in Rotula.Core (kept as its own constant because Rendering must
    /// check before calling Core, so that the preview draws a placeholder instead of catching the exception Core throws above it).
    /// </summary>
    internal const long MaxPixels = 40_000_000;

    /// <summary>Upper bound of the memory held by the cache, in bytes. Not a constant so a test can shrink it.</summary>
    internal static long MaxBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>Bytes currently held by the cache.</summary>
    internal static long CurrentBytes { get { lock (Gate) return _bytes; } }

    /// <summary>Test hook: called each time a picture is really computed (a cache miss) with the source it was computed from.</summary>
    internal static Action<GrayscaleBitmap>? Computed { get; set; }

    /// <summary>The result of preparing an image element.</summary>
    /// <param name="EncodedBytes">The monochrome picture as a PNG; empty when <paramref name="TooLarge"/>.</param>
    /// <param name="Width">Width of the picture after rotation, in dots.</param>
    /// <param name="Height">Height of the picture after rotation, in dots.</param>
    /// <param name="TooLarge">True when the target is above <see cref="MaxPixels"/> and nothing was computed.</param>
    internal readonly record struct Prepared(byte[] EncodedBytes, int Width, int Height, bool TooLarge);

    private readonly record struct Key(long Source, int Width, int Height, ImageDitherMode Dither, int Threshold, bool Invert, FieldRotation Rotation);

    private sealed class Entry
    {
        public required Key Key { get; init; }
        public required byte[] Png { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }

        /// <summary>The array plus a rough allowance for the entry and the dictionary slot.</summary>
        public long Cost => Png.Length + 128;
    }

    private sealed class SourceId
    {
        public readonly long Value = Interlocked.Increment(ref _lastId);
    }

    private static long _lastId;
    private static readonly object Gate = new();
    // Weak association: a bitmap that is no longer used by any document is free to be collected, its entries age out of the LRU list.
    private static readonly ConditionalWeakTable<GrayscaleBitmap, SourceId> Ids = new();
    private static readonly Dictionary<Key, LinkedListNode<Entry>> Map = new();
    private static readonly LinkedList<Entry> Lru = new(); // first = most recently used
    private static long _bytes;

    /// <summary>The target size of an element in 64-bit arithmetic, with the rule of <see cref="ImageProcessor.ResolveSize"/> (0 keeps the aspect ratio).</summary>
    internal static (long Width, long Height) ResolveSize(ImageElement img)
    {
        var src = img.Source!;
        long w = img.WidthDots, h = img.HeightDots;
        if (w <= 0 && h <= 0) return (src.Width, src.Height);
        if (w <= 0) w = Math.Max(1, (long)Math.Round((double)src.Width * h / src.Height, MidpointRounding.AwayFromZero));
        if (h <= 0) h = Math.Max(1, (long)Math.Round((double)src.Height * w / src.Width, MidpointRounding.AwayFromZero));
        return (w, h);
    }

    /// <summary>
    /// True when the target size of <paramref name="img"/> is empty or above <see cref="MaxPixels"/>, so nothing may be dithered and the image is
    /// shown as a stand-in. Only arithmetic: it can be asked (by the driver pre-flight, through the adapter) without preparing the picture.
    /// The element must have a source.
    /// </summary>
    internal static bool IsTooLarge(ImageElement img)
    {
        var (w, h) = ResolveSize(img);
        // Both sides are below the cap before multiplying, so the product cannot overflow a long either.
        return w < 1 || h < 1 || w > MaxPixels || h > MaxPixels || w * h > MaxPixels;
    }

    /// <summary>
    /// Returns the printable dots of an image element as a PNG, from the cache when the same picture was prepared before.
    /// Never throws for a size problem: an oversize target (see <see cref="IsTooLarge"/>) comes back with <see cref="Prepared.TooLarge"/> set.
    /// The element must have a source.
    /// </summary>
    internal static Prepared Prepare(ImageElement img)
    {
        var src = img.Source ?? throw new ArgumentException("The image element has no source.", nameof(img));
        if (IsTooLarge(img)) return new Prepared([], 0, 0, true);
        var (w, h) = ResolveSize(img);

        var rotation = img.Rotation ?? FieldRotation.Normal;
        var key = new Key(Ids.GetValue(src, _ => new SourceId()).Value, (int)w, (int)h, img.Dither, img.Threshold, img.Invert, rotation);
        lock (Gate)
        {
            if (Map.TryGetValue(key, out var hit))
            {
                Lru.Remove(hit);
                Lru.AddFirst(hit);
                return new Prepared(hit.Value.Png, hit.Value.Width, hit.Value.Height, false);
            }
        }

        // The expensive part runs outside the lock so two pictures can be prepared at the same time. If two threads prepare the same
        // one, both results are identical and the second insert simply keeps the first.
        var mono = ImageProcessor.ToMonochrome(img);
        var entry = new Entry { Key = key, Png = LabelDocumentAdapter.EncodeMonochrome(mono), Width = mono.Width, Height = mono.Height };
        Computed?.Invoke(src);
        Store(entry);
        return new Prepared(entry.Png, entry.Width, entry.Height, false);
    }

    private static void Store(Entry entry)
    {
        lock (Gate)
        {
            if (entry.Cost > MaxBytes || Map.ContainsKey(entry.Key)) return;
            _bytes += entry.Cost;
            Map[entry.Key] = Lru.AddFirst(entry);
            while (_bytes > MaxBytes && Lru.Last is { } oldest)
            {
                Lru.RemoveLast();
                Map.Remove(oldest.Value.Key);
                _bytes -= oldest.Value.Cost;
            }
        }
    }

    /// <summary>Drops everything (tests, and a way to give the memory back).</summary>
    internal static void Clear()
    {
        lock (Gate)
        {
            Map.Clear();
            Lru.Clear();
            _bytes = 0;
        }
    }
}
