using Rotula.Core.Model;
using Rotula.Rendering.Barcodes;
using ZXing;
using ZXing.Common;

namespace Rotula.Tests;

/// <summary>Encodes with Rotula's preview encoder and reads the result back with ZXing's decoder, so a test can compare the payload and the symbology identifier.</summary>
internal static class Gs1DecodeSupport
{
    /// <summary>What the reader reported: the text and the AIM symbology identifier (for example <c>]C1</c>).</summary>
    internal sealed record Decoded(string Text, string? SymbologyIdentifier);

    /// <summary>Draws the matrix on a white background with a quiet zone and decodes it; fails the test when nothing could be read.</summary>
    internal static Decoded Decode(BarcodeMatrix matrix, BarcodeFormat format)
    {
        const int quiet = 10, scale = 4;
        int w = (matrix.Width + 2 * quiet) * scale;
        // A linear matrix has a single row; give it some height so the reader finds scan lines.
        int rows = matrix.IsLinear ? 40 : matrix.Height;
        int h = (rows + 2 * quiet) * scale;
        var luminance = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int mx = x / scale - quiet, my = y / scale - quiet;
                bool dark = mx >= 0 && my >= 0 && mx < matrix.Width && my < rows && matrix.IsDark(mx, matrix.IsLinear ? 0 : my);
                luminance[y * w + x] = dark ? (byte)0 : (byte)255;
            }
        var source = new RGBLuminanceSource(luminance, w, h, RGBLuminanceSource.BitmapFormat.Gray8);
        var hints = new Dictionary<DecodeHintType, object>
        {
            [DecodeHintType.POSSIBLE_FORMATS] = new[] { format },
            [DecodeHintType.TRY_HARDER] = true,
        };
        // ZXing.Net converts FNC1 only when this key is present: the first FNC1 becomes the "]C1" prefix, later ones the group separator.
        if (format == BarcodeFormat.CODE_128) hints[DecodeHintType.ASSUME_GS1] = true;
        var result = new MultiFormatReader().decode(new BinaryBitmap(new HybridBinarizer(source)), hints)
            ?? throw new Xunit.Sdk.XunitException("ZXing could not read the " + format + " symbol.");
        string? id = result.ResultMetadata is { } meta && meta.TryGetValue(ResultMetadataType.SYMBOLOGY_IDENTIFIER, out var v) ? v?.ToString() : null;
        return new Decoded(result.Text, id);
    }

    /// <summary>Encodes data with the preview encoder and decodes it again.</summary>
    internal static Decoded RoundTrip(BarcodeSymbology symbology, string data, BarcodeFormat format, BarcodeEncodeOptions? options = null) =>
        Decode(BarcodeEncoder.Encode(symbology, data, options), format);
}
