namespace Rotula.Core.Sheets;

/// <summary>One label placed on a sheet.</summary>
/// <typeparam name="T">What is placed (a rendered label, a record number in tests).</typeparam>
/// <param name="Slot">The position on the sheet.</param>
/// <param name="Item">The label.</param>
/// <param name="LabelNumber">1-based number of the label in the whole run (copies included).</param>
public readonly record struct SheetPlacement<T>(SheetSlot Slot, T Item, long LabelNumber);

/// <summary>One sheet of a run: its 1-based number and the labels on it, in fill order.</summary>
/// <typeparam name="T">What is placed.</typeparam>
/// <param name="PageNumber">1-based sheet number.</param>
/// <param name="Placements">The labels on this sheet; positions that are skipped or left over stay empty.</param>
public sealed record SheetPage<T>(int PageNumber, IReadOnlyList<SheetPlacement<T>> Placements);

/// <summary>
/// Puts a sequence of labels onto sheets: in fill order across the positions of a sheet, then onto the next sheet. The first sheet can start at
/// a later position so a partly used sheet is used up. The labels are pulled lazily, one sheet at a time, so a long run never sits in memory;
/// the caller decides what a label is (one per record and copy, with its own serial number) and this class only decides where it goes.
/// </summary>
public static class SheetImposition
{
    /// <summary>How many sheets <paramref name="labelCount"/> labels need when the first sheet starts at <paramref name="startPosition"/>.</summary>
    /// <exception cref="ArgumentException">The layout has no positions, or the start position is not on the sheet.</exception>
    public static long PageCount(SheetLayout layout, long labelCount, int startPosition = 1)
    {
        Check(layout, startPosition);
        if (labelCount <= 0) return 0;
        long slots = layout.SlotCount;
        // The skipped positions behave like labels that were already used.
        return (labelCount + startPosition - 1 + slots - 1) / slots;
    }

    /// <summary>
    /// Lays the labels out on sheets. The first sheet leaves positions 1 to <paramref name="startPosition"/> - 1 empty; every later sheet starts
    /// at position 1. A sheet is yielded as soon as it is full (or the labels run out), so an exporter can write it and move on.
    /// </summary>
    /// <param name="layout">The sheet geometry. It should be valid (<see cref="SheetLayout.Validate"/>); only the slot count is checked here.</param>
    /// <param name="labels">The labels in print order.</param>
    /// <param name="startPosition">1-based first position used on the first sheet.</param>
    /// <param name="order">The fill order of the positions.</param>
    /// <exception cref="ArgumentException">The layout has no positions, or the start position is not on the sheet.</exception>
    public static IEnumerable<SheetPage<T>> Paginate<T>(SheetLayout layout, IEnumerable<T> labels, int startPosition = 1, SheetOrder order = SheetOrder.AcrossThenDown)
    {
        ArgumentNullException.ThrowIfNull(labels);
        Check(layout, startPosition);
        return PaginateCore(layout, labels, startPosition, order);
    }

    private static IEnumerable<SheetPage<T>> PaginateCore<T>(SheetLayout layout, IEnumerable<T> labels, int startPosition, SheetOrder order)
    {
        // The slots never change, so they are worked out once.
        var slots = layout.Slots(order).ToArray();
        int page = 1;
        int next = startPosition - 1;
        long number = 0;
        var current = new List<SheetPlacement<T>>(slots.Length);
        foreach (var label in labels)
        {
            current.Add(new SheetPlacement<T>(slots[next], label, ++number));
            if (++next == slots.Length)
            {
                yield return new SheetPage<T>(page++, current);
                current = new List<SheetPlacement<T>>(slots.Length);
                next = 0;
            }
        }
        if (current.Count > 0) yield return new SheetPage<T>(page, current);
    }

    private static void Check(SheetLayout layout, int startPosition)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (layout.SlotCount <= 0) throw new ArgumentException("The sheet has no label positions.", nameof(layout));
        if (startPosition < 1 || startPosition > layout.SlotCount)
            throw new ArgumentOutOfRangeException(nameof(startPosition), "The start position must be on the sheet.");
    }
}
