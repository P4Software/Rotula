namespace Rotula.Core.Sheets;

/// <summary>
/// The sheet layout chosen for a label, as it is remembered in the .rotula file: the built-in template (or none for a custom layout), the
/// geometry, the fill order and the guides. The "start at position" number is not part of it on purpose: it depends on the sheet that is in the
/// printer today, not on the label.
/// </summary>
public sealed record SheetSettings
{
    /// <summary>Id of the built-in template the layout came from (<see cref="SheetTemplates"/>), or null for a layout the user typed.</summary>
    public string? TemplateId { get; init; }

    /// <summary>The geometry.</summary>
    public SheetLayout Layout { get; init; } = SheetTemplates.All[0].Layout;

    /// <summary>The fill order of the positions.</summary>
    public SheetOrder Order { get; init; } = SheetOrder.AcrossThenDown;

    /// <summary>Marks drawn on the sheet.</summary>
    public SheetGuides Guides { get; init; } = SheetGuides.None;
}
