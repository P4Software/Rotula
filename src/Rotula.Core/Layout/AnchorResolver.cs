using Rotula.Core.Elements;
using Rotula.Core.Model;
using Rotula.Core.Text;
using Rotula.Core.Validation;

namespace Rotula.Core.Layout;

/// <summary>
/// The absolute positions of every element of a label for one record, worked out from the anchors (see <see cref="LabelElement.Anchor"/>).
/// Elements without an anchor keep their own X/Y; anchored ones are placed so that their anchor point lies at the reference's anchor point
/// plus their X/Y offset. Resolution is lazy and memoized, so asking for one element only measures the chain it depends on.
/// </summary>
public sealed class AnchorLayout
{
    private readonly LabelDocument _doc;
    private readonly Func<LabelElement, (int Width, int Height)> _sizeOf;
    private readonly Dictionary<string, LabelElement> _byId;
    private readonly Dictionary<string, (int X, int Y)> _origins = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Width, int Height)> _sizes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _cycles;
    private readonly HashSet<string> _missing = new(StringComparer.Ordinal);
    private readonly List<ValidationIssue> _issues = new();

    internal AnchorLayout(LabelDocument doc, Func<LabelElement, (int Width, int Height)> sizeOf)
    {
        _doc = doc;
        _sizeOf = sizeOf;
        _byId = new Dictionary<string, LabelElement>(StringComparer.Ordinal);
        // The last element wins when two share an id; ids are unique in practice, so this only keeps the lookup from throwing.
        foreach (var e in doc.Elements) _byId[e.Id] = e;
        _cycles = AnchorResolver.FindCycles(doc);
        // Every problem is found up front, so the issues are complete before any position is asked for (positions resolve lazily).
        foreach (var e in doc.Elements)
        {
            if (e.Anchor is null) continue;
            if (_cycles.Contains(e.Id))
            {
                string refId = e.ReferenceElementId ?? string.Empty;
                string other = _byId.TryGetValue(refId, out var r) ? AnchorResolver.Describe(r) : refId;
                _issues.Add(new ValidationIssue(ValidationSeverity.Error, AnchorResolver.CycleIssue, e.Id, other));
            }
            else if (e.RelativeTo == AnchorReference.Element && !(e.ReferenceElementId is { } id && _byId.TryGetValue(id, out var target) && !ReferenceEquals(target, e)))
            {
                // The object was deleted or the element came from another label: the label stands in, so the object still has a place.
                _missing.Add(e.Id);
                _issues.Add(new ValidationIssue(ValidationSeverity.Warning, AnchorResolver.MissingReferenceIssue, e.Id, e.ReferenceElementId ?? string.Empty));
            }
        }
    }

    /// <summary>The label the layout was built for.</summary>
    public LabelDocument Document => _doc;

    /// <summary>Problems found: elements that reference each other in a circle (an error) and references to elements that no longer exist (a warning).</summary>
    public IReadOnlyList<ValidationIssue> Issues => _issues;

    /// <summary>True when the element is part of a reference circle and therefore has no position of its own.</summary>
    public bool IsOnCycle(LabelElement e) => _cycles.Contains(e.Id);

    /// <summary>The unrotated size of the element, from the measurer the layout was built with (memoized).</summary>
    public (int Width, int Height) SizeOf(LabelElement e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (!_sizes.TryGetValue(e.Id, out var size))
        {
            size = _sizeOf(e);
            _sizes[e.Id] = size;
        }
        return size;
    }

    /// <summary>The absolute origin of the element: the point the ZPL <c>^FO</c>/<c>^FT</c> gets, relative to the label home.</summary>
    public (int X, int Y) OriginOf(LabelElement e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (_origins.TryGetValue(e.Id, out var known)) return known;
        var origin = Compute(e);
        _origins[e.Id] = origin;
        return origin;
    }

    /// <summary>The box the element occupies, in absolute dots relative to the label home (its estimated size placed at its resolved origin).</summary>
    public DotRect BoundsOf(LabelElement e)
    {
        var (w, h) = SizeOf(e);
        var (x, y) = OriginOf(e);
        return ElementGeometry.BoxAtOrigin(e, _doc, w, h).Offset(x, y);
    }

    /// <summary>The anchor point of the reference of <paramref name="e"/> for a given anchor and reference choice (the label or an element).</summary>
    public (double X, double Y) ReferencePoint(LabelElement e, AnchorPoint anchor, AnchorReference reference, string? referenceElementId)
    {
        ArgumentNullException.ThrowIfNull(e);
        // A target on a reference circle still answers (with its raw numbers), so an element hanging off a circle keeps a place near it.
        if (reference == AnchorReference.Element && referenceElementId is not null && _byId.TryGetValue(referenceElementId, out var target) && !ReferenceEquals(target, e))
            return BoundsOf(target).PointAt(anchor);
        return ElementGeometry.LabelBox(_doc).PointAt(anchor);
    }

    private (int X, int Y) Compute(LabelElement e)
    {
        // Absolute, or on a circle (reported in Issues): the raw numbers are the only position there is.
        if (e.Anchor is not { } anchor || _cycles.Contains(e.Id)) return (e.X, e.Y);

        // A reference that does not exist (reported in Issues) is replaced by the label.
        var reference = _missing.Contains(e.Id) ? AnchorReference.Label : e.RelativeTo;
        var (rx, ry) = ReferencePoint(e, anchor, reference, e.ReferenceElementId);
        var (w, h) = SizeOf(e);
        var (ax, ay) = ElementGeometry.BoxAtOrigin(e, _doc, w, h).PointAt(anchor);
        // The element's own anchor point must land on the reference point plus the offset; the origin is that point minus where the anchor
        // lies within the element's own box.
        return (Round(rx + e.X - ax), Round(ry + e.Y - ay));
    }

    private static int Round(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);
}

/// <summary>
/// Turns anchors into absolute coordinates. It is the single place that decides where an anchored object goes: the ZPL generator (so every
/// record of a data merge), the preview and the designer's selection frames all call it, so what the canvas shows is what is printed.
/// </summary>
public static class AnchorResolver
{
    /// <summary>Issue key (error): the element and the element it references, directly or through others, are positioned relative to each other.</summary>
    public const string CycleIssue = "Element.Anchor.Cycle";

    /// <summary>Issue key (warning): the referenced element does not exist; the label is used instead.</summary>
    public const string MissingReferenceIssue = "Element.Anchor.MissingReference";

    /// <summary>True when any element of the label has an anchor, so positions need resolving at all.</summary>
    public static bool HasAnchors(LabelDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        return doc.Elements.Any(e => e.Anchor is not null);
    }

    /// <summary>
    /// Builds the layout of a label for one record. Sizes come from <see cref="ElementSizeEstimator"/> unless <paramref name="sizeOf"/> is given
    /// (a caller that has already measured its elements passes that, so it never measures twice).
    /// </summary>
    /// <param name="doc">The label.</param>
    /// <param name="data">The record's values (already resolved), or null for the sample values. Text widths depend on it.</param>
    /// <param name="rasterizer">Measures Windows fonts exactly; may be null.</param>
    /// <param name="sizeOf">Replaces the built-in size estimate.</param>
    public static AnchorLayout Resolve(LabelDocument doc, IReadOnlyDictionary<string, string>? data = null, ITextRasterizer? rasterizer = null,
        Func<LabelElement, (int Width, int Height)>? sizeOf = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        return new AnchorLayout(doc, sizeOf ?? (e => ElementSizeEstimator.Estimate(doc, e, data, rasterizer)));
    }

    /// <summary>The ids of every element that sits on a reference circle (A relative to B relative to A, or an element relative to itself).</summary>
    public static HashSet<string> FindCycles(LabelDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var byId = new Dictionary<string, LabelElement>(StringComparer.Ordinal);
        foreach (var e in doc.Elements) byId[e.Id] = e;
        var onCycle = new HashSet<string>(StringComparer.Ordinal);
        foreach (var start in doc.Elements)
        {
            // Walk the chain from each element; meeting the start again means it is on a circle.
            var seen = new HashSet<string>(StringComparer.Ordinal) { start.Id };
            var current = start;
            while (Next(current, byId) is { } next)
            {
                if (next.Id == start.Id) { onCycle.Add(start.Id); break; }
                if (!seen.Add(next.Id)) break; // joined a circle further down, but the start itself is not on it
                current = next;
            }
        }
        return onCycle;
    }

    /// <summary>
    /// True when making <paramref name="elementId"/> relative to <paramref name="referenceElementId"/> would close a circle: the reference is the
    /// element itself, or is (directly or through other elements) positioned relative to it.
    /// </summary>
    public static bool WouldCreateCycle(LabelDocument doc, string elementId, string referenceElementId)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (string.Equals(elementId, referenceElementId, StringComparison.Ordinal)) return true;
        var byId = new Dictionary<string, LabelElement>(StringComparer.Ordinal);
        foreach (var e in doc.Elements) byId[e.Id] = e;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = byId.GetValueOrDefault(referenceElementId);
        while (current is not null && seen.Add(current.Id))
        {
            if (current.Id == elementId) return true;
            current = Next(current, byId);
        }
        return false;
    }

    /// <summary>
    /// True when the element follows one of <paramref name="ids"/>: it is positioned relative to it, directly or through other elements.
    /// Moving that set moves the element along, so a move must not add the distance to it a second time.
    /// </summary>
    public static bool FollowsAny(LabelDocument doc, LabelElement e, IReadOnlySet<string> ids)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(ids);
        var byId = new Dictionary<string, LabelElement>(StringComparer.Ordinal);
        foreach (var el in doc.Elements) byId[el.Id] = el;
        var seen = new HashSet<string>(StringComparer.Ordinal) { e.Id };
        var current = Next(e, byId);
        while (current is not null && seen.Add(current.Id))
        {
            if (ids.Contains(current.Id)) return true;
            current = Next(current, byId);
        }
        return false;
    }

    /// <summary>
    /// The X/Y an element needs so that it stays exactly where it is when its anchor or reference changes to the given values (null anchor
    /// means absolute). The designer uses it so that choosing an anchor changes the numbers, never the picture.
    /// </summary>
    /// <param name="layout">The current layout of the label (where the element is now).</param>
    /// <param name="e">The element.</param>
    /// <param name="anchor">The new anchor, or null for an absolute position.</param>
    /// <param name="reference">The new reference.</param>
    /// <param name="referenceElementId">The new reference element, when <paramref name="reference"/> is an element.</param>
    public static (int X, int Y) OffsetKeepingPosition(AnchorLayout layout, LabelElement e, AnchorPoint? anchor, AnchorReference reference, string? referenceElementId)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(e);
        var (ox, oy) = layout.OriginOf(e);
        if (anchor is not { } a) return (ox, oy);
        var (rx, ry) = layout.ReferencePoint(e, a, reference, referenceElementId);
        var (w, h) = layout.SizeOf(e);
        var (ax, ay) = ElementGeometry.BoxAtOrigin(e, layout.Document, w, h).PointAt(a);
        return ((int)Math.Round(ox + ax - rx, MidpointRounding.AwayFromZero), (int)Math.Round(oy + ay - ry, MidpointRounding.AwayFromZero));
    }

    /// <summary>The element's name, or its id when it has none, for messages.</summary>
    public static string Describe(LabelElement e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return string.IsNullOrWhiteSpace(e.Name) ? e.Id : e.Name;
    }

    private static LabelElement? Next(LabelElement e, Dictionary<string, LabelElement> byId) =>
        e.Anchor is not null && e.RelativeTo == AnchorReference.Element && e.ReferenceElementId is { } id ? byId.GetValueOrDefault(id) : null;
}
