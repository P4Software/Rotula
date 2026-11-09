using System.Buffers.Binary;
using Rotula.Core.Elements;
using Rotula.Core.Imaging;
using Rotula.Core.Model;
using Rotula.Rendering;
using Rotula.Rendering.Input;
using static Rotula.Tests.RenderingTestSupport;

namespace Rotula.Tests;

/// <summary>An image that is too large to render is drawn as a visible placeholder; the preview never throws or exhausts memory.</summary>
public class RenderingImageLimitsTests
{
    private static GrayscaleBitmap Gradient(int w = 8, int h = 8)
    {
        var px = new byte[w * h];
        for (int i = 0; i < px.Length; i++) px[i] = (byte)(i * 255 / px.Length);
        return new GrayscaleBitmap(w, h, px);
    }

    private static LabelDocument DocWith(ImageElement img)
    {
        var d = new LabelDocument { Width = Measure.Millimeters(50), Height = Measure.Millimeters(30) };
        d.Elements.Add(img);
        return d;
    }

    [Fact]
    public void A_target_whose_pixel_count_overflows_an_int_becomes_a_placeholder()
    {
        // 65536 x 65536 = 2^32 wraps to 0 in int arithmetic: the unguarded scaler allocated an empty buffer and threw while indexing it.
        var doc = DocWith(new ImageElement { X = 10, Y = 10, Source = Gradient(), WidthDots = 65536, HeightDots = 65536 });

        var label = LabelDocumentAdapter.ToRenderLabel(doc);

        var p = Assert.IsType<RenderPlaceholder>(Assert.Single(label.Elements));
        Assert.Contains("too large", p.Caption, StringComparison.OrdinalIgnoreCase);
        using var r = LabelRenderer.RenderBitmap(label); // draws without throwing
        Assert.NotNull(DarkBounds(r.Bitmap));
    }

    [Fact]
    public void A_40000_by_40000_target_becomes_a_placeholder_limited_to_the_label()
    {
        var doc = DocWith(new ImageElement { Source = Gradient(), WidthDots = 40000, HeightDots = 40000 });

        var label = LabelDocumentAdapter.ToRenderLabel(doc);

        var p = Assert.IsType<RenderPlaceholder>(Assert.Single(label.Elements));
        Assert.True(p.WidthDots <= doc.WidthDots && p.HeightDots <= doc.HeightDots);
        using var r = LabelRenderer.RenderBitmap(label);
        Assert.Empty(r.Issues);
    }

    [Fact]
    public void One_side_given_with_a_huge_value_is_refused_too()
    {
        // The other side follows the aspect ratio of the 8 x 8 source, so this is 30000 x 30000 again.
        var doc = DocWith(new ImageElement { Source = Gradient(), WidthDots = 30000 });

        Assert.IsType<RenderPlaceholder>(Assert.Single(LabelDocumentAdapter.ToRenderLabel(doc).Elements));
    }

    [Fact]
    public void A_normal_image_is_still_drawn_as_a_picture()
    {
        var doc = DocWith(new ImageElement { X = 5, Y = 5, Source = Gradient(), WidthDots = 64, HeightDots = 64 });

        var img = Assert.IsType<RenderImage>(Assert.Single(LabelDocumentAdapter.ToRenderLabel(doc).Elements));
        Assert.Equal((64, 64), ((int)img.WidthDots, (int)img.HeightDots));
    }

    [Fact]
    public void A_render_image_whose_header_claims_billions_of_pixels_is_refused_without_decoding_it()
    {
        // A valid PNG header that says 60000 x 60000; the file itself is a few dozen bytes.
        var label = Label(100, 60, new RenderImage { EncodedBytes = PngClaiming(60000, 60000), WidthDots = 50, HeightDots = 30 });

        using var r = LabelRenderer.RenderBitmap(label);

        var issue = Assert.Single(r.Issues);
        Assert.Contains("too large", issue.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A PNG signature, an IHDR chunk with the given size and a stub IDAT: enough for a codec to read the size, nothing to decode.</summary>
    private static byte[] PngClaiming(int width, int height)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 0; // greyscale
        WriteChunk(ms, "IHDR", ihdr);
        WriteChunk(ms, "IDAT", [0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01]);
        WriteChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        var typeAndData = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        s.Write(typeAndData);
        uint crc = 0xFFFFFFFF;
        foreach (byte b in typeAndData)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        var crcBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, ~crc);
        s.Write(crcBytes);
    }
}

/// <summary>
/// The dithered picture of an image element is computed once per distinct picture, so dragging an object does not redo it for every
/// mouse move. These tests change process-wide cache settings, so they live in one class (xUnit runs a class's tests one after another).
/// </summary>
public class RenderingImageCacheTests : IDisposable
{
    private readonly long _savedMax = ImageRenderCache.MaxBytes;

    public void Dispose()
    {
        ImageRenderCache.Computed = null;
        ImageRenderCache.MaxBytes = _savedMax;
        ImageRenderCache.Clear();
    }

    private static GrayscaleBitmap Noise(int seed, int size = 32)
    {
        var rnd = new Random(seed);
        var px = new byte[size * size];
        rnd.NextBytes(px);
        return new GrayscaleBitmap(size, size, px);
    }

    private static LabelDocument DocWith(ImageElement img)
    {
        var d = new LabelDocument { Width = Measure.Millimeters(50), Height = Measure.Millimeters(30) };
        d.Elements.Add(img);
        return d;
    }

    private static RenderImage Render(GrayscaleBitmap source, int size = 64) =>
        Assert.IsType<RenderImage>(Assert.Single(LabelDocumentAdapter.ToRenderLabel(
            DocWith(new ImageElement { Source = source, WidthDots = size, HeightDots = size })).Elements));

    /// <summary>Counts how often the picture of exactly this source is computed (other tests may render other images meanwhile).</summary>
    private static Func<int> CountComputations(GrayscaleBitmap mine)
    {
        int n = 0;
        ImageRenderCache.Computed = src => { if (ReferenceEquals(src, mine)) Interlocked.Increment(ref n); };
        return () => Volatile.Read(ref n);
    }

    [Fact]
    public void Rendering_an_unchanged_image_again_does_not_redo_the_work()
    {
        var source = Noise(1);
        var img = new ImageElement { X = 10, Y = 10, Source = source, WidthDots = 120, HeightDots = 120 };
        var doc = DocWith(img);
        var count = CountComputations(source);

        var first = Assert.IsType<RenderImage>(Assert.Single(LabelDocumentAdapter.ToRenderLabel(doc).Elements));
        // What a drag does: the position changes on every frame, the picture does not.
        for (int i = 0; i < 20; i++)
        {
            img.X += 3;
            var again = Assert.IsType<RenderImage>(Assert.Single(LabelDocumentAdapter.ToRenderLabel(doc).Elements));
            Assert.Equal(first.EncodedBytes, again.EncodedBytes);
        }

        Assert.Equal(1, count());
    }

    [Fact]
    public void A_setting_that_changes_the_dots_is_a_different_picture()
    {
        var source = Noise(2);
        var img = new ImageElement { Source = source, WidthDots = 100, HeightDots = 100 };
        var doc = DocWith(img);
        var count = CountComputations(source);

        LabelDocumentAdapter.ToRenderLabel(doc);
        img.Threshold = 90;
        LabelDocumentAdapter.ToRenderLabel(doc);
        img.Dither = ImageDitherMode.Ordered;
        LabelDocumentAdapter.ToRenderLabel(doc);
        img.Invert = true;
        LabelDocumentAdapter.ToRenderLabel(doc);
        img.Rotation = FieldRotation.Rotated90;
        LabelDocumentAdapter.ToRenderLabel(doc);
        img.WidthDots = 80;
        LabelDocumentAdapter.ToRenderLabel(doc);
        Assert.Equal(6, count());

        // Coming back to a state seen before is free.
        img.WidthDots = 100;
        img.Rotation = null;
        img.Invert = false;
        img.Dither = ImageDitherMode.FloydSteinberg;
        img.Threshold = 128;
        LabelDocumentAdapter.ToRenderLabel(doc);
        Assert.Equal(6, count());
    }

    [Fact]
    public void Another_source_with_the_same_settings_is_not_confused_with_the_first()
    {
        Assert.NotEqual(Render(Noise(3)).EncodedBytes, Render(Noise(4)).EncodedBytes);
    }

    [Fact]
    public void The_cache_stays_inside_its_memory_limit_and_drops_the_oldest_first()
    {
        ImageRenderCache.Clear();
        ImageRenderCache.MaxBytes = 12_000; // room for only a few noisy 64 x 64 pictures
        var sources = Enumerable.Range(10, 12).Select(i => Noise(i)).ToArray();
        var seen = new int[sources.Length];
        ImageRenderCache.Computed = src =>
        {
            int i = Array.FindIndex(sources, s => ReferenceEquals(s, src));
            if (i >= 0) Interlocked.Increment(ref seen[i]);
        };

        foreach (var s in sources) Render(s);

        Assert.True(ImageRenderCache.CurrentBytes <= ImageRenderCache.MaxBytes);
        // The first source was pushed out, so showing it again computes it again; the last one is still there.
        Render(sources[0]);
        Render(sources[^1]);
        Assert.Equal(2, seen[0]);
        Assert.Equal(1, seen[^1]);
        Assert.True(ImageRenderCache.CurrentBytes <= ImageRenderCache.MaxBytes);
    }

    [Fact]
    public async Task Concurrent_renders_of_the_same_image_agree()
    {
        var source = Noise(99, 48);
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => Render(source, 96).EncodedBytes)));

        Assert.All(results, r => Assert.Equal(results[0], r));
    }
}
