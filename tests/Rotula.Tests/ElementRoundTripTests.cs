using System.Reflection;
using Rotula.App.Editing;
using Rotula.Core.Elements;
using Rotula.Core.Imaging;
using Rotula.Core.Model;
using Rotula.Files;

namespace Rotula.Tests;

/// <summary>
/// Safety net for the hand-written element copy code. Adding an element type or a property touches several switches (the cloner, the DPI converter,
/// the file serializer and more), and a property that one of them forgets is silently dropped: the label saves, copies or converts fine and the
/// setting is simply gone. These tests find every concrete <see cref="LabelElement"/> by reflection, give EVERY public settable property a
/// distinctive non-default value (nested objects included) and check that the value survives each copy path. A new element type or property is
/// therefore covered without anyone remembering to add a test.
/// </summary>
public class ElementRoundTripTests
{
    /// <summary>The copy paths under test.</summary>
    private enum CopyPath
    {
        /// <summary><see cref="ElementCloner.Clone"/> (undo snapshots, duplicate, paste).</summary>
        Clone,
        /// <summary><see cref="RotulaFileSerializer.Serialize(LabelDocument, Rotula.Core.Merge.DataMergeSettings?, Rotula.Core.Sheets.SheetSettings?)"/> then <c>Deserialize</c> (the .rotula file).</summary>
        File,
        /// <summary><see cref="RotulaFileSerializer.SerializeElements"/> then <c>DeserializeElements</c> (copy and paste between two running copies).</summary>
        Clipboard,
        /// <summary><see cref="LabelDpiConverter.Convert"/> to the SAME resolution, which must be the identity.</summary>
        DpiIdentity,
    }

    /// <summary>
    /// Properties that a path legitimately drops or normalises. Each entry is a conscious decision with its reason; anything not listed here that
    /// does not survive is a failure. The key is the copy path plus the property as <c>DeclaringType.Property</c>. A test below checks that every
    /// entry still happens, so an exception that stops being needed (or a bug that gets fixed) cannot linger in this list.
    /// </summary>
    private static readonly Dictionary<(CopyPath Path, string Property), string> Allowed = new()
    {
        // Documented as "unused for a Windows font", so the file stores either the printer font id or the family, never both.
        [(CopyPath.File, "FontChoice.PrinterFontId")] = "Only meaningful for a printer font; a Windows font (the only kind stored on an element) does not write it.",
        [(CopyPath.Clipboard, "FontChoice.PrinterFontId")] = "Same mapping as the file: a Windows font does not write the printer font id.",
    };

    /// <summary>Properties left at their default on purpose: the value that would be "distinctive" is not a valid state, so the default is the right test value.</summary>
    private static readonly HashSet<string> KeepDefault = new()
    {
        // Only a Windows font is stored on an element; a Printer-source FontChoice is read back as "no Windows font" by design.
        "FontChoice.Source",
    };

    // ------------------------------------------------------------------------------------------ discovery

    private static readonly Lazy<Dictionary<string, Type>> ElementTypes = new(() =>
    {
        // The test project references every Rotula assembly, so a derived element declared in any of them is found.
        var assemblies = new List<Assembly> { typeof(LabelElement).Assembly };
        foreach (var name in Assembly.GetExecutingAssembly().GetReferencedAssemblies().Where(n => n.Name!.StartsWith("Rotula.", StringComparison.Ordinal)))
            assemblies.Add(Assembly.Load(name));
        return assemblies.Distinct()
            .SelectMany(a => a.GetTypes())
            .Where(t => typeof(LabelElement).IsAssignableFrom(t) && !t.IsAbstract && t.IsPublic)
            .ToDictionary(t => t.Name);
    });

    /// <summary>Theory data: one row per concrete element type.</summary>
    public static IEnumerable<object[]> ElementTypeNames() => ElementTypes.Value.Keys.OrderBy(n => n, StringComparer.Ordinal).Select(n => new object[] { n });

    [Fact]
    public void Discovery_FindsTheElementTypes()
    {
        // Guards the guard: if reflection ever found nothing, every theory below would pass vacuously.
        Assert.True(ElementTypes.Value.Count >= 12, "Found only: " + string.Join(", ", ElementTypes.Value.Keys));
        foreach (var expected in new[] { nameof(TextElement), nameof(DataFieldElement), nameof(BarcodeElement), nameof(ImageElement), nameof(RawZplElement) })
            Assert.Contains(expected, ElementTypes.Value.Keys);
    }

    // ------------------------------------------------------------------------------------------ the tests

    [Theory]
    [MemberData(nameof(ElementTypeNames))]
    public void Populate_GivesEveryPropertyANonDefaultValue(string typeName)
    {
        // Without this the other tests could pass because the value being round-tripped was never really set.
        var populated = Populate(ElementTypes.Value[typeName]);
        var untouched = new List<string>();
        FindUntouched(populated, null, typeName, untouched);
        Assert.True(untouched.Count == 0, "Properties still at their default after populating (teach Populate about them): " + string.Join("; ", untouched));
    }

    [Theory]
    [MemberData(nameof(ElementTypeNames))]
    public void Clone_KeepsEveryProperty(string typeName) => AssertSurvives(typeName, CopyPath.Clone);

    [Theory]
    [MemberData(nameof(ElementTypeNames))]
    public void RotulaFile_KeepsEveryProperty(string typeName) => AssertSurvives(typeName, CopyPath.File);

    [Theory]
    [MemberData(nameof(ElementTypeNames))]
    public void Clipboard_KeepsEveryProperty(string typeName) => AssertSurvives(typeName, CopyPath.Clipboard);

    [Theory]
    [MemberData(nameof(ElementTypeNames))]
    public void DpiConversionToTheSameResolution_KeepsEveryProperty(string typeName) => AssertSurvives(typeName, CopyPath.DpiIdentity);

    [Fact]
    public void EveryAllowedLoss_StillHappens()
    {
        // An allow-list entry that no longer matches anything is a stale decision: remove it (or, if it was a bug, the fix is done).
        var seen = new HashSet<(CopyPath, string)>();
        foreach (var type in ElementTypes.Value.Values)
            foreach (CopyPath path in Enum.GetValues<CopyPath>())
                foreach (var diff in Differences(type, path))
                    seen.Add((path, diff.Property));
        var stale = Allowed.Keys.Where(k => !seen.Contains(k)).Select(k => $"{k.Path}: {k.Property}").ToList();
        Assert.True(stale.Count == 0, "Allow-list entries that no longer happen: " + string.Join("; ", stale));
    }

    private static void AssertSurvives(string typeName, CopyPath path)
    {
        var unexpected = Differences(ElementTypes.Value[typeName], path).Where(d => !Allowed.ContainsKey((path, d.Property))).ToList();
        Assert.True(unexpected.Count == 0,
            $"{typeName} loses data on the {path} path (a copy switch forgot a property, or the allow-list needs a reasoned entry):{Environment.NewLine}"
            + string.Join(Environment.NewLine, unexpected.Select(d => "  " + d)));
    }

    // ------------------------------------------------------------------------------------------ the copy paths

    private static List<Difference> Differences(Type elementType, CopyPath path)
    {
        var original = (LabelElement)Populate(elementType);
        var copy = Copy(original, path);
        var diffs = new List<Difference>();
        Compare(original, copy, elementType.Name, diffs);
        return diffs;
    }

    private static LabelElement Copy(LabelElement original, CopyPath path)
    {
        switch (path)
        {
            case CopyPath.Clone:
                return ElementCloner.Clone(original);
            case CopyPath.File:
            {
                var doc = new LabelDocument();
                doc.Elements.Add(original);
                return Assert.Single(RotulaFileSerializer.Deserialize(RotulaFileSerializer.Serialize(doc)).Elements);
            }
            case CopyPath.Clipboard:
                return Assert.Single(RotulaFileSerializer.DeserializeElements(RotulaFileSerializer.SerializeElements(new[] { original })));
            case CopyPath.DpiIdentity:
            {
                var doc = new LabelDocument();
                doc.Elements.Add(original);
                return Assert.Single(LabelDpiConverter.Convert(doc, doc.Dpi).Document.Elements);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(path));
        }
    }

    // ------------------------------------------------------------------------------------------ populating

    /// <summary>Values the generic rules cannot invent: types without a plain constructor, or text that has a format the reader enforces.</summary>
    private static readonly Dictionary<string, Func<object>> Overrides = new()
    {
        ["ImageElement.Source"] = () => new GrayscaleBitmap(3, 2, new byte[] { 10, 20, 30, 40, 50, 60 }),
        // The file reader keeps only printer font file names (device letter, up to 8 characters, font extension).
        ["TextElement.StoredFontPath"] = () => "E:CUSTOM.TTF",
    };

    /// <summary>Creates an instance of <paramref name="type"/> with every public settable property set to a distinctive non-default value.</summary>
    private static object Populate(Type type)
    {
        var instance = Activator.CreateInstance(type)!;
        var fresh = Activator.CreateInstance(type)!;
        foreach (var p in SettableProperties(type))
        {
            string key = p.DeclaringType!.Name + "." + p.Name;
            object? value;
            if (KeepDefault.Contains(key)) value = p.GetValue(fresh);
            else if (Overrides.TryGetValue(key, out var make)) value = make();
            else value = Distinct(p.PropertyType, p.GetValue(fresh), p.Name);
            p.SetValue(instance, value);
        }
        return instance;
    }

    private static IEnumerable<PropertyInfo> SettableProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.SetMethod is { IsPublic: true } && p.GetIndexParameters().Length == 0)
            .OrderBy(p => p.Name, StringComparer.Ordinal);

    /// <summary>
    /// A value of <paramref name="propertyType"/> that differs from <paramref name="defaultValue"/>. The size of each step depends on the property name,
    /// so two properties of the same type (width and height, say) do not get the same number and a swap between them would be noticed. Numbers stay
    /// close to their default so they remain inside every range the printer or the DPI converter enforces.
    /// </summary>
    private static object? Distinct(Type propertyType, object? defaultValue, string name)
    {
        Type t = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
        int step = 1 + name.Sum(c => c) % 3;
        if (t.IsEnum)
        {
            // The last defined member that is not the default; for a nullable enum whose default is null that is simply the last member.
            return Enum.GetValues(t).Cast<object>().Last(v => !v.Equals(defaultValue));
        }
        if (t == typeof(bool)) return !(defaultValue is true);
        if (t == typeof(int)) return defaultValue is int i ? i + step : 40 + step;
        if (t == typeof(long)) return defaultValue is long l ? l + step : 40L + step;
        if (t == typeof(double)) return (defaultValue is double d ? d : 10.0) + 0.25 * step;
        if (t == typeof(char))
        {
            const string candidates = "KLMNOPQ";
            return candidates.First(c => !(defaultValue is char dc && dc == c) && candidates.IndexOf(c) >= step % candidates.Length);
        }
        if (t == typeof(string)) return "S" + name;
        if (t.IsClass && t.GetConstructor(Type.EmptyTypes) is not null) return Populate(t);
        throw new NotSupportedException(
            $"Property '{name}' has type {t.FullName}: add a rule for it in {nameof(ElementRoundTripTests)}.{nameof(Distinct)} or an entry in {nameof(Overrides)}, " +
            "so the round-trip tests keep covering every property.");
    }

    /// <summary>Reports every settable property of <paramref name="populated"/> that still equals its default (so nothing was really tested for it).</summary>
    private static void FindUntouched(object populated, object? defaults, string path, List<string> untouched)
    {
        var type = populated.GetType();
        defaults ??= Activator.CreateInstance(type)!;
        foreach (var p in SettableProperties(type))
        {
            string key = p.DeclaringType!.Name + "." + p.Name;
            object? now = p.GetValue(populated), before = p.GetValue(defaults);
            if (KeepDefault.Contains(key)) continue;
            if (now is null) { untouched.Add($"{path}.{p.Name}"); continue; }
            if (IsNested(now.GetType())) FindUntouched(now, before, $"{path}.{p.Name}", untouched);
            else if (Equals(now, before)) untouched.Add($"{path}.{p.Name}");
        }
    }

    // ------------------------------------------------------------------------------------------ comparing

    /// <summary>One value that came back different.</summary>
    /// <param name="Path">Where, for example <c>TextElement.Block.Fit.Mode</c>.</param>
    /// <param name="Property">The declaring type and property, for example <c>TextBlockOptions.Alignment</c>; the allow-list key.</param>
    /// <param name="Detail">What was expected and what came back.</param>
    private sealed record Difference(string Path, string Property, string Detail)
    {
        public override string ToString() => $"{Path}: {Detail}";
    }

    /// <summary>True for the model's own option objects, which are compared property by property; everything else is compared as a value.</summary>
    private static bool IsNested(Type t) => t.IsClass && t != typeof(string) && t != typeof(GrayscaleBitmap) && t.Namespace?.StartsWith("Rotula.", StringComparison.Ordinal) == true;

    private static void Compare(object expected, object? actual, string path, List<Difference> diffs)
    {
        foreach (var p in SettableProperties(expected.GetType()))
        {
            string owner = p.DeclaringType!.Name + "." + p.Name;
            string here = path + "." + p.Name;
            object? want = p.GetValue(expected);
            object? got = actual is null ? null : p.GetValue(actual);
            if (want is null || got is null)
            {
                if (!ReferenceEquals(want, got)) diffs.Add(new Difference(here, owner, $"expected {Show(want)}, got {Show(got)}"));
            }
            else if (want is GrayscaleBitmap a)
            {
                if (!SamePixels(a, got as GrayscaleBitmap)) diffs.Add(new Difference(here, owner, "the image pixels differ"));
            }
            else if (IsNested(want.GetType()))
            {
                Compare(want, got, here, diffs);
            }
            else if (!want.Equals(got))
            {
                diffs.Add(new Difference(here, owner, $"expected {Show(want)}, got {Show(got)}"));
            }
        }
    }

    private static string Show(object? value) => value is null ? "null" : $"'{value}'";

    private static bool SamePixels(GrayscaleBitmap a, GrayscaleBitmap? b)
    {
        if (b is null || a.Width != b.Width || a.Height != b.Height) return false;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
                if (a.GetPixel(x, y) != b.GetPixel(x, y)) return false;
        return true;
    }
}
