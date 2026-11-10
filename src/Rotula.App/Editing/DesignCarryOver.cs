using Rotula.Core.Barcodes;
using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Core.Validation;
using Rotula.Core.Zpl;

namespace Rotula.App.Editing;

/// <summary>What <see cref="DesignCarryOver.Apply"/> managed to bring across from the previous design.</summary>
/// <param name="Matched">Elements of the re-imported label that were recognised as elements of the previous design (same kind and place).</param>
/// <param name="RestoredBindings">Texts and barcodes whose <c>{{field}}</c> content was put back because the text held the field's value.</param>
/// <param name="ReinsertedNotPrintable">Objects marked "do not print" that were put back, since ZPL never carries them.</param>
public sealed record CarryOverReport(int Matched, int RestoredBindings, int ReinsertedNotPrintable);

/// <summary>
/// When the label is rebuilt from ZPL the user edited by hand, everything the printer language cannot express would be lost: element
/// names, data bindings (the text shows the record's values, not <c>{{fields}}</c>), print conditions, locks, text fit and the objects
/// marked "do not print". This pairs each element of the previous design with the re-imported element of the same kind at the same
/// place and copies those designer-only settings across, so editing a position or a font in the text does not silently unbind a field.
/// Elements the user removed or moved in the text simply find no partner and come through as parsed.
/// </summary>
public static class DesignCarryOver
{
    /// <summary>
    /// Carries the designer-only settings of <paramref name="previous"/> onto the elements of <paramref name="parsed"/> (in place) and
    /// puts back the "do not print" objects. Nothing the ZPL text states is changed, except that a text equal to a field's shown
    /// value becomes the field again.
    /// </summary>
    /// <param name="previous">The design before the edit.</param>
    /// <param name="parsed">The label read from the edited text; its elements are modified.</param>
    /// <param name="data">The record shown on the canvas when the text was generated (its values are what the text contains), or null for the sample values.</param>
    public static CarryOverReport Apply(LabelDocument previous, LabelDocument parsed, IReadOnlyDictionary<string, string>? data)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(parsed);
        int matched = 0, restored = 0, reinserted = 0;
        var taken = new HashSet<LabelElement>(ReferenceEqualityComparer.Instance);
        var scratch = new List<ValidationIssue>();

        foreach (var old in previous.Elements)
        {
            if (old.NotPrintable) continue; // handled below: it was never in the text
            string? shown = ShownContent(previous, old, data, scratch);
            // Best partner first: same place and the very content the text held; then the same content anywhere (the user moved it in the
            // text); then the same kind at the same place (the user changed its content). Each parsed element is claimed at most once.
            var candidates = parsed.Elements.Where(p => !taken.Contains(p) && SameKind(old, p)).ToList();
            var partner = (shown is null ? null : candidates.FirstOrDefault(p => SamePlace(old, p) && Content(p) == shown))
                ?? (shown is null ? null : candidates.FirstOrDefault(p => Content(p) == shown))
                ?? candidates.FirstOrDefault(p => SamePlace(old, p));
            if (partner is null) continue;
            taken.Add(partner);
            matched++;

            partner.Id = old.Id;
            partner.Name = old.Name;
            old.CopyObjectSettingsTo(partner);
            if (old is TextElement oldText && partner is TextElement newText)
            {
                if (shown is not null && newText.Text == shown && oldText.GetTemplate() != shown && HasField(oldText.GetTemplate()))
                {
                    newText.Text = oldText.GetTemplate();
                    restored++;
                }
                // Text fit is Rotula metadata the ZPL only carries as computed values.
                if (newText.Block is not null && oldText.Block?.Fit is { } fit) newText.Block.Fit = fit.Clone();
            }
            else if (old is BarcodeElement oldBarcode && partner is BarcodeElement newBarcode && shown is not null && newBarcode.Data == shown
                && oldBarcode.Data != shown && HasField(oldBarcode.Data))
            {
                newBarcode.Data = oldBarcode.Data;
                newBarcode.QrContentSpec = oldBarcode.QrContentSpec;
                restored++;
            }
        }

        // Objects that never reach the printer cannot be in the text; losing them because the text was edited would be a surprise.
        for (int i = 0; i < previous.Elements.Count; i++)
        {
            var old = previous.Elements[i];
            if (!old.NotPrintable) continue;
            parsed.Elements.Insert(Math.Min(i, parsed.Elements.Count), ElementCloner.Clone(old));
            reinserted++;
        }
        return new CarryOverReport(matched, restored, reinserted);
    }

    private static bool HasField(string template) => VariableResolver.FindVariables(template).Count > 0;

    // Data fields and plain texts are both texts in ZPL, so the exact class is not compared for them.
    private static bool SameKind(LabelElement a, LabelElement b) => a.GetType() == b.GetType() || (a is TextElement && b is TextElement);

    private static bool SamePlace(LabelElement a, LabelElement b) => a.X == b.X && a.Y == b.Y && a.OriginKind == b.OriginKind;

    /// <summary>The content of a text or barcode as it is after parsing; null for elements without data.</summary>
    private static string? Content(LabelElement e) => e switch
    {
        TextElement t => t.Text,
        BarcodeElement b => b.Data,
        _ => null,
    };

    /// <summary>
    /// What the generated text contained for the element: its template with the fields replaced by the shown record's values (or the
    /// sample values), normalised the way the generator writes it. Null for elements without data.
    /// </summary>
    private static string? ShownContent(LabelDocument doc, LabelElement e, IReadOnlyDictionary<string, string>? data, List<ValidationIssue> scratch)
    {
        switch (e)
        {
            case TextElement t:
                return VariableResolver.Resolve(t.GetTemplate(), data, doc.Variables, null, scratch);
            case BarcodeElement b:
            {
                string resolved = Rotula.Core.Barcodes.QrContent.QrContentBuilder.ResolveForElement(b, data, doc.Variables, scratch)
                    ?? VariableResolver.Resolve(b.Data, data, doc.Variables, null, scratch);
                // EAN-13 and UPC-A lose a typed check digit on the way out, so the parsed data is the normalised form.
                return BarcodeValidator.NormalizeForPrinter(b, resolved);
            }
            default:
                return null;
        }
    }
}
