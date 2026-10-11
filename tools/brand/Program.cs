using SkiaSharp;

namespace Rotula.Brand;

/// <summary>
/// Redraws the Rotula logo and icons from code and writes every brand file into the repository.
/// Double-click tools\brand\Regenerate-Brand.cmd. There is nothing to configure. Running it twice produces byte-identical files.
/// </summary>
internal static class Program
{
    private static string root = "";
    private static int written, unchanged;

    private static int Main(string[] args)
    {
        try
        {
            root = FindRoot();
            Console.WriteLine("Redrawing the Rotula logo and icons...");
            Console.WriteLine();

            // 1. Icons: one PNG per size per icon, packed by hand into ICO files.
            byte[] app = BuildIco(Layout.ForTile);
            byte[] doc = BuildIco(Layout.ForPage);
            Save("src/Rotula.App/Assets/rotula.ico", app, "application icon");
            Save("src/Rotula.App/Assets/rotula-document.ico", doc, ".rotula document icon");
            // The MCP server shares the app mark on purpose: it is the same product, and a different badge would be unreadable at 16 px.
            Save("src/Rotula.Mcp/Assets/rotula-mcp.ico", app, "MCP server icon (same mark as the app)");

            // 2. Vector logos and the WPF drawing resource.
            Save("docs/images/logo.svg", Text(SvgWriter.Mark()), "logo (vector)");
            Save("docs/images/logo-wordmark.svg", Text(SvgWriter.Lockup(darkSurface: false)), "logo with wordmark, for light backgrounds");
            Save("docs/images/logo-wordmark-dark.svg", Text(SvgWriter.Lockup(darkSurface: true)), "logo with wordmark, for dark backgrounds");
            Save("src/Rotula.App/Resources/Brand.xaml", Text(XamlWriter.Dictionary()), "WPF vector resource");

            // 3. Pictures for the web and the installer.
            using (var mark512 = RenderMaster(512)) Save("docs/images/logo-512.png", Containers.Png(mark512), "logo 512 px");
            using (var social = Scenes.Social()) Save("docs/images/social-preview.png", Containers.Png(social), "GitHub social preview 1280 x 640");

            foreach (var (w, h) in new[] { (164, 314), (192, 386), (246, 459), (328, 604), (410, 797) })
                using (var bmp = Scenes.WizardLarge(w, h)) Save($"packaging/installer/assets/wizard-{w}x{h}.bmp", Containers.Bmp(bmp), $"installer side image {w} x {h}");
            foreach (var (w, h) in new[] { (55, 55), (64, 68), (83, 80), (110, 106), (138, 140) })
                using (var bmp = Scenes.WizardSmall(w, h)) Save($"packaging/installer/assets/wizard-small-{w}x{h}.bmp", Containers.Bmp(bmp), $"installer header image {w} x {h}");

            // Developer aid, not needed for normal use: a folder of enlarged previews to inspect the small sizes.
            if (args.Length == 2 && args[0] == "--preview") Preview(args[1]);

            Console.WriteLine();
            Console.WriteLine($"Done. {written} file(s) updated, {unchanged} already up to date.");
            Console.WriteLine("Commit the changed files. Windows may keep showing the old icon for a while; see docs/releasing.md for refreshing the icon cache.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine("The logo and icons could not be regenerated.");
            Console.WriteLine($"What happened: {ex.Message}");
            Console.WriteLine("What to do: make sure you started it from the Rotula repository (tools\\brand\\Regenerate-Brand.cmd) and that the files are not open in another program.");
            return 1;
        }
    }

    private static byte[] BuildIco(Func<int, Layout> layout)
    {
        var images = new List<(int, byte[])>();
        foreach (int size in Containers.IconSizes)
            using (var bmp = MarkPainter.Render(layout(size))) images.Add((size, Containers.Png(bmp)));
        return Containers.Ico(images);
    }

    /// <summary>Renders the master tile at any pixel size (used for the 512 px PNG).</summary>
    private static SKBitmap RenderMaster(int px)
    {
        var bmp = new SKBitmap(px, px, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Transparent);
        MarkPainter.Paint(canvas, Layout.ForTile(Layout.Master), 0, 0, px / 256f);
        return bmp;
    }

    private static byte[] Text(string s) => new System.Text.UTF8Encoding(false).GetBytes(s);

    private static void Save(string relative, byte[] bytes, string what)
    {
        string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
        {
            unchanged++;
            Console.WriteLine($"  unchanged  {relative}");
            return;
        }
        File.WriteAllBytes(path, bytes);
        written++;
        Console.WriteLine($"  written    {relative}  ({what})");
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Rotula.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Rotula.sln was not found above this program.");
    }

    /// <summary>Writes a contact sheet of the small sizes magnified 8x with nearest-neighbour on white, dark and checkerboard backgrounds.</summary>
    private static void Preview(string folder)
    {
        Directory.CreateDirectory(folder);
        // The installer images are BMP files (viewable only in Inno Setup), so previews carry them as PNG.
        using (var side = Scenes.WizardLarge(328, 604)) File.WriteAllBytes(Path.Combine(folder, "wizard-large.png"), Containers.Png(side));
        using (var small = Scenes.WizardSmall(110, 106)) File.WriteAllBytes(Path.Combine(folder, "wizard-small.png"), Containers.Png(small));
        foreach (var (name, make) in new (string, Func<int, Layout>)[] { ("tile", Layout.ForTile), ("page", Layout.ForPage) })
        {
            int[] sizes = [16, 20, 24, 32, 48, 64];
            string[] bgs = ["white", "dark", "checker"];
            const int zoom = 8, pad = 12;
            int width = sizes.Sum(s => s * zoom + pad) + pad, height = bgs.Length * (64 * zoom + pad) + pad;
            using var sheet = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using var canvas = new SKCanvas(sheet);
            canvas.Clear(SKColors.Gray);
            for (int row = 0; row < bgs.Length; row++)
            {
                int x = pad, y = pad + row * (64 * zoom + pad);
                foreach (int size in sizes)
                {
                    using var icon = MarkPainter.Render(make(size));
                    var area = new SKRect(x, y, x + size * zoom, y + size * zoom);
                    using var bgPaint = new SKPaint { Color = bgs[row] == "dark" ? new SKColor(0x20, 0x20, 0x20) : SKColors.White };
                    canvas.DrawRect(area, bgPaint);
                    if (bgs[row] == "checker")
                        for (int cy = 0; cy < size * zoom; cy += 16)
                            for (int cx = 0; cx < size * zoom; cx += 16)
                                if (((cx + cy) / 16) % 2 == 0)
                                {
                                    using var p = new SKPaint { Color = new SKColor(0xD8, 0xD8, 0xD8) };
                                    canvas.DrawRect(x + cx, y + cy, 16, 16, p);
                                }
                    using var image = SKImage.FromBitmap(icon);
                    canvas.DrawImage(image, area, new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
                    x += size * zoom + pad;
                }
            }
            File.WriteAllBytes(Path.Combine(folder, $"sheet-{name}.png"), Containers.Png(sheet));
        }
        // Real-size strip too: what the shell actually shows, at 1x on both backgrounds.
        using var strip = new SKBitmap(520, 140, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var sc = new SKCanvas(strip);
        for (int band = 0; band < 2; band++)
        {
            using var bandPaint = new SKPaint { Color = band == 0 ? SKColors.White : new SKColor(0x20, 0x20, 0x20) };
            sc.DrawRect(0, band * 70, 520, 70, bandPaint);
            int x = 10;
            foreach (int size in new[] { 16, 20, 24, 32, 48 })
            {
                foreach (var make in new Func<int, Layout>[] { Layout.ForTile, Layout.ForPage })
                {
                    using var icon = MarkPainter.Render(make(size));
                    sc.DrawBitmap(icon, x, band * 70 + 10 + (48 - size) / 2, new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None), null);
                    x += size + 8;
                }
                x += 8;
            }
        }
        File.WriteAllBytes(Path.Combine(folder, "strip-1x.png"), Containers.Png(strip));
        Console.WriteLine($"  previews written to {folder}");
    }
}
