using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Rotula.Mcp.Tools;

/// <summary>Ready-made starting points for common label tasks.</summary>
[McpServerPromptType]
public sealed class LabelPrompts
{
    /// <summary>Guides the design of a shipping label.</summary>
    [McpServerPrompt(Name = "design_shipping_label")]
    [Description("Design a shipping label step by step with the Rotula tools.")]
    public string DesignShippingLabel(
        [Description("What the label is for, for example \"parcels for our web shop\".")] string purpose = "parcels",
        [Description("Label size, for example \"100 x 150 mm\".")] string size = "100 x 150 mm")
        => $"""
        Design a shipping label for {purpose} on a {size} label using the Rotula tools.
        1. Call get_server_info to see which folders are available, then create_label with the size (203 DPI unless told otherwise).
        2. Lay out the blocks with add_box and add_line: sender area, recipient area, a service or priority box, and a barcode area.
        3. Add the recipient and sender with add_text (use blockWidth for multi-line addresses) and the tracking number as a Code128 barcode with add_barcode or a QR code with add_qr.
        4. If the label will be filled from a spreadsheet, use add_data_field for the changing parts so merge_preview can check them.
        5. Call validate_label and fix every error and warning, then render_preview and check the layout. Remember the preview is approximate.
        6. Save with save_label. Do not print unless the user asks; print_label always shows a dry run first and needs the user's confirmation.
        """;

    /// <summary>Guides the conversion of an old ZPL label.</summary>
    [McpServerPrompt(Name = "convert_old_zpl_label")]
    [Description("Turn an existing ZPL label into an editable Rotula label and check it.")]
    public string ConvertOldZplLabel(
        [Description("The ZPL text, or the name of a .zpl file in the allowed folders.")] string zplOrFile,
        [Description("Printer resolution the ZPL was written for (203, 300 or 600).")] string dpi = "203")
        => $"""
        Convert this ZPL label into an editable Rotula label: {zplOrFile}
        1. Call import_zpl (use the text argument for pasted ZPL, or path for a file) with dpi {dpi}.
        2. Read the warnings. Anything listed was kept as raw ZPL so it still prints but cannot be edited; tell the user what that means for each one.
        3. Call get_label and describe the elements, then validate_label and render_preview.
        4. If the target printer has a different resolution, offer convert_dpi.
        5. Offer to save with save_label. Do not print unless the user asks.
        """;

    /// <summary>Guides the repair of validation findings.</summary>
    [McpServerPrompt(Name = "fix_validation_warnings")]
    [Description("Go through the validation findings of the open label and fix them.")]
    public string FixValidationWarnings()
        => """
        Fix the problems of the open label.
        1. Call validate_label. If it reports nothing, say so and stop.
        2. For each finding, use its elementId with get_label to see the element, and explain the problem in plain words.
        3. Fix it with update_element or move_element (for example correct a barcode check digit, shorten data, or move an element back inside the label). Do not delete elements unless the user agrees.
        4. Call validate_label again until errors are gone, then render_preview. If a change was a mistake, undo_last reverts it.
        """;
}
