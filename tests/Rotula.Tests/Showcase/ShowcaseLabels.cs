using Rotula.Core.Elements;
using Rotula.Core.Gs1;
using Rotula.Core.Model;
using Rotula.Core.Text;

namespace Rotula.Tests.Showcase;

/// <summary>
/// The eight labels of the README gallery. Every brand, address, product and number in them is fictional. Only fonts that ship with every
/// Windows install are used (Bahnschrift, Segoe UI, Georgia, Palatino Linotype, Constantia, Consolas, Impact, Arial), and the symbols are real:
/// the GS1 numbers use the check digits Rotula computes, and the test that renders them reads every barcode back with a decoder.
/// </summary>
internal static partial class ShowcaseLabels
{
    /// <summary>All labels in gallery order.</summary>
    public static IReadOnlyList<ShowcaseLabel> All() => new[]
    {
        PalletLabel(),
        ParcelLabel(),
        FoodLabel(),
        SpecimenLabel(),
        AssetLabel(),
        ShelfLabel(),
        WineLabel(),
        ConditionsLabel(),
    };

    private const string Industrial = "Bahnschrift";
    private const string Ui = "Segoe UI";

    private static ShowcaseVariant Single() => new(string.Empty, null);
}
