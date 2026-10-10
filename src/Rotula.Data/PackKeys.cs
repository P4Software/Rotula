namespace Rotula.Data;

/// <summary>
/// Message keys for provider-pack problems. A pack that fails never breaks the application: the failure becomes a
/// <see cref="Packs.PackWarning"/> carrying one of these keys (the UI maps it to plain-language text in the user's language).
/// </summary>
public static class PackKeys
{
    /// <summary>The pack folder has no readable pack.json, or it is invalid. args[0] is the folder name.</summary>
    public const string ManifestInvalid = "Data.Pack.ManifestInvalid";
    /// <summary>The pack needs a newer Rotula (its minimum Data API version is higher than this build). args: pack id, required version.</summary>
    public const string NeedsNewerRotula = "Data.Pack.NeedsNewerRotula";
    /// <summary>The pack was built for an older, incompatible Data API (different major version). args: pack id.</summary>
    public const string BuiltForOlderRotula = "Data.Pack.BuiltForOlderRotula";
    /// <summary>The pack's files could not be loaded (missing or damaged file, wrong architecture). args: pack id.</summary>
    public const string LoadFailed = "Data.Pack.LoadFailed";
    /// <summary>The pack loaded but its code raised an error while starting. args: pack id.</summary>
    public const string StartupFailed = "Data.Pack.StartupFailed";
    /// <summary>The pack has no entry class for Rotula, or the providers it registers differ from pack.json. args: pack id.</summary>
    public const string EntryMismatch = "Data.Pack.EntryMismatch";
    /// <summary>A provider or preset id of the pack is malformed. args: id.</summary>
    public const string InvalidProviderId = "Data.Pack.InvalidProviderId";
    /// <summary>A provider or preset id is already taken (a pack can never replace an existing provider). args: id.</summary>
    public const string DuplicateProvider = "Data.Pack.DuplicateProvider";
    /// <summary>A pack with this id is already loaded. args: pack id.</summary>
    public const string DuplicatePack = "Data.Pack.DuplicatePack";
    /// <summary>A pack in the user's own folder has not been approved yet; it was NOT loaded. args: pack id.</summary>
    public const string UserPackNeedsApproval = "Data.Pack.UserPackNeedsApproval";
    /// <summary>A pack the user approved earlier has changed since; it was NOT loaded until approved again. args: pack id.</summary>
    public const string UserPackChanged = "Data.Pack.UserPackChanged";
    /// <summary>The pack folder is a link or contains links, or is too large; it was skipped. args: folder name.</summary>
    public const string UnsafeFolder = "Data.Pack.UnsafeFolder";

    /// <summary>Title of the approval prompt for a pack from the user's folder.</summary>
    public const string TrustTitle = "Data.Pack.Trust.Title";
    /// <summary>Body of the approval prompt: a pack is a program; only approve packs from someone you trust.</summary>
    public const string TrustWarning = "Data.Pack.Trust.Warning";
}
