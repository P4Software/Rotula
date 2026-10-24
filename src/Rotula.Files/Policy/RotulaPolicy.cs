namespace Rotula.Files.Policy;

/// <summary>
/// Registry location and value names of the Rotula policies. The ADMX template in <c>packaging/policies</c> writes exactly
/// these names (a test compares them), and <c>docs/deployment.md</c> documents them.
/// </summary>
public static class PolicyNames
{
    /// <summary>Key under HKLM (computer) and HKCU (user). The computer value wins.</summary>
    public const string KeyPath = @"SOFTWARE\Policies\P4Software\Rotula";

    /// <summary>Text: "auto", "en", "es", "pt-BR" or "fr-CA". Sets the language and locks the menu.</summary>
    public const string Language = "Language";

    /// <summary>Number: 1 turns the update check on, 0 turns it off; either way the user cannot change it.</summary>
    public const string UpdateCheck = "UpdateCheck";

    /// <summary>Text: Windows printer name preselected until the user picks a printer. Not locked.</summary>
    public const string DefaultPrinter = "DefaultPrinter";

    /// <summary>Number: 0 means add-ons in the user's own folder are not loaded (the installed "packs" folder still is).</summary>
    public const string UserAddOns = "UserAddOns";

    /// <summary>Number: 0 means the MCP server never prints, whatever its settings file says.</summary>
    public const string McpPrinting = "McpPrinting";

    /// <summary>Number: 0 means the MCP server never reads databases, whatever its settings file says.</summary>
    public const string McpDatabase = "McpDatabase";

    /// <summary>Number: 0 means the print engine (the rotula command line and its REST server) never prints and never queries printers.</summary>
    public const string PrintEngine = "PrintEngine";

    /// <summary>Every value name, for the template check.</summary>
    public static readonly IReadOnlyList<string> All = new[] { Language, UpdateCheck, DefaultPrinter, UserAddOns, McpPrinting, McpDatabase, PrintEngine };
}

/// <summary>
/// The settings an organization has fixed through Group Policy or Intune. A null value means "not managed": the user decides.
/// A policy can choose a value for the user or take a feature away; it never turns on a risky feature (MCP printing and
/// database access still need the MCP settings file as well).
/// </summary>
public sealed record RotulaPolicy
{
    /// <summary>No policy at all.</summary>
    public static readonly RotulaPolicy None = new();

    /// <summary>
    /// The values the Language policy accepts: "auto" and the culture names of the shipped languages, in their canonical
    /// spelling. They must match the app's Loc.SupportedLanguages and the ADMX enum (tests compare them).
    /// </summary>
    public static readonly IReadOnlyList<string> Languages = new[] { "auto", "en", "es", "pt-BR", "fr-CA" };

    /// <summary>One of <see cref="Languages"/> when the language is managed; null otherwise.</summary>
    public string? Language { get; init; }

    /// <summary>True or false when the update check is managed; null when the user decides.</summary>
    public bool? UpdateCheck { get; init; }

    /// <summary>A Windows printer name to start from when the user has not chosen a printer yet; null when not set.</summary>
    public string? DefaultPrinter { get; init; }

    /// <summary>False when add-ons in the user's own folder must not be loaded.</summary>
    public bool AllowUserAddOns { get; init; } = true;

    /// <summary>True when the MCP server must refuse to print.</summary>
    public bool McpPrintingBlocked { get; init; }

    /// <summary>True when the MCP server must refuse database access.</summary>
    public bool McpDatabaseBlocked { get; init; }

    /// <summary>True when the print engine (command line and REST server) must refuse to print and to query printers.</summary>
    public bool PrintEngineBlocked { get; init; }

    /// <summary>True when the language is chosen by the organization.</summary>
    public bool IsLanguageManaged => Language is not null;

    /// <summary>True when the update check is switched on or off by the organization.</summary>
    public bool IsUpdateCheckManaged => UpdateCheck is not null;

    /// <summary>
    /// Reads every policy value once. Values that are not understood (an unknown language, a number other than 0 or 1, a printer
    /// name that is too long) are ignored, so a typo in a policy never forces a wrong value on the user.
    /// </summary>
    public static RotulaPolicy Read(IPolicySource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        string? language = source.GetString(PolicyNames.Language)?.Trim();
        string? printer = source.GetString(PolicyNames.DefaultPrinter)?.Trim();
        return new RotulaPolicy
        {
            // Case does not matter in the registry ("PT-br" works), but the value handed on is the canonical name.
            Language = Languages.FirstOrDefault(l => string.Equals(l, language, StringComparison.OrdinalIgnoreCase)),
            UpdateCheck = source.GetInt(PolicyNames.UpdateCheck) switch { 0 => false, 1 => true, _ => null },
            // Windows printer names are at most 220 characters; anything longer is not a printer name.
            DefaultPrinter = string.IsNullOrEmpty(printer) || printer.Length > 220 ? null : printer,
            AllowUserAddOns = source.GetInt(PolicyNames.UserAddOns) != 0,
            McpPrintingBlocked = source.GetInt(PolicyNames.McpPrinting) == 0,
            McpDatabaseBlocked = source.GetInt(PolicyNames.McpDatabase) == 0,
            PrintEngineBlocked = source.GetInt(PolicyNames.PrintEngine) == 0,
        };
    }
}
