using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace Rotula.Data.Packs;

/// <summary>A problem with a pack, reported to the user instead of breaking the application.</summary>
/// <param name="Key">One of the <see cref="PackKeys"/> message keys (the UI supplies the plain-language text).</param>
/// <param name="PackId">Pack id when known (otherwise the folder name).</param>
/// <param name="Args">Culture-neutral format arguments for the message.</param>
/// <param name="Detail">Technical detail (exception type and message) for a "show details" expander. Contains no secrets.</param>
public sealed record PackWarning(string Key, string PackId, IReadOnlyList<string> Args, string? Detail = null)
{
    /// <summary>English fallback sentence for logs and tools without resources. Plain language: what happened and what to do.</summary>
    public string EnglishText => Key switch
    {
        PackKeys.ManifestInvalid => $"The add-on in the folder \"{PackId}\" has no valid description file (pack.json), so it was not loaded. Ask the person who gave you the add-on for a corrected copy.",
        PackKeys.NeedsNewerRotula => $"The add-on \"{PackId}\" needs a newer version of Rotula. Update Rotula, then restart it.",
        PackKeys.BuiltForOlderRotula => $"The add-on \"{PackId}\" was made for an older version of Rotula and cannot be used. Ask for an updated add-on.",
        PackKeys.LoadFailed => $"The add-on \"{PackId}\" could not be opened. A file may be missing or damaged, or built for another kind of computer. Reinstall the add-on.",
        PackKeys.StartupFailed => $"The add-on \"{PackId}\" stopped with an error while starting, so it was turned off. Rotula itself is not affected. Reinstall or update the add-on.",
        PackKeys.EntryMismatch => $"The add-on \"{PackId}\" does not match its description file, so it was not loaded. Reinstall the add-on.",
        PackKeys.InvalidProviderId or PackKeys.DuplicateProvider => $"The add-on \"{PackId}\" offers a data source name that is already used or not allowed, so it was not loaded.",
        PackKeys.DuplicatePack => $"An add-on named \"{PackId}\" is already loaded; the second copy was ignored.",
        PackKeys.UserPackNeedsApproval => $"The add-on \"{PackId}\" in your own folder was NOT loaded. An add-on is a program: only approve it if you trust where it came from.",
        PackKeys.UserPackChanged => $"The add-on \"{PackId}\" has changed since you approved it, so it was NOT loaded. Approve it again only if you expected the change.",
        PackKeys.UnsafeFolder => $"The add-on folder \"{PackId}\" contains shortcuts (links) or is too large, so it was skipped.",
        _ => $"The add-on \"{PackId}\" could not be used.",
    };
}

/// <summary>One pack folder found on disk.</summary>
public sealed class PackCandidate
{
    internal PackCandidate(string folder, PackOrigin origin, PackManifest? manifest, string? manifestError, string? fingerprint)
    {
        Folder = folder;
        Origin = origin;
        Manifest = manifest;
        ManifestError = manifestError;
        Fingerprint = fingerprint;
    }

    /// <summary>Full folder path.</summary>
    public string Folder { get; }
    /// <summary>Where it was found.</summary>
    public PackOrigin Origin { get; }
    /// <summary>The parsed pack.json, or null when invalid.</summary>
    public PackManifest? Manifest { get; }
    /// <summary>English reason when <see cref="Manifest"/> is null.</summary>
    public string? ManifestError { get; }
    /// <summary>SHA-256 of the whole folder (user packs only; null for bundled packs and unsafe folders).</summary>
    public string? Fingerprint { get; }
    /// <summary>The pack id, or the folder name when the manifest is unusable.</summary>
    public string DisplayId => Manifest?.Id ?? Path.GetFileName(Folder);
}

/// <summary>What the user must be told before approving a pack from their own folder.</summary>
/// <param name="TitleKey">Message key of the dialog title.</param>
/// <param name="WarningKey">Message key of the plain-language warning (a pack is a program that runs with your rights).</param>
/// <param name="Candidate">The pack.</param>
/// <param name="ShortFingerprint">First 12 characters of the fingerprint, to show the user.</param>
public sealed record PackTrustPrompt(string TitleKey, string WarningKey, PackCandidate Candidate, string ShortFingerprint)
{
    /// <summary>English text of the warning, for tools without resources.</summary>
    public string EnglishWarning =>
        $"\"{Candidate.Manifest?.Name ?? Candidate.DisplayId}\" is an add-on from your own folder ({Candidate.Folder}). " +
        "An add-on is a program: if you approve it, it can do anything you can do on this computer, including reading your files. " +
        "Only approve it if you know who made it and you got it from a source you trust. You can remove the approval later.";
}

/// <summary>A pack that is loaded and registered.</summary>
public sealed class LoadedPack
{
    internal LoadedPack(PackCandidate candidate, IDatabaseProviderPack pack, AssemblyLoadContext context)
    {
        Candidate = candidate;
        Pack = pack;
        Context = context;
    }

    /// <summary>Where it came from.</summary>
    public PackCandidate Candidate { get; }
    /// <summary>The pack object.</summary>
    public IDatabaseProviderPack Pack { get; }
    /// <summary>The isolated load context holding the pack and its own dependencies.</summary>
    public AssemblyLoadContext Context { get; }
    /// <summary>Full path of the licence and notices file shipped with the pack, if it exists.</summary>
    public string? LicenseNoticesPath
        => Candidate.Manifest?.LicenseNoticesFile is { } f && File.Exists(Path.Combine(Candidate.Folder, f)) ? Path.Combine(Candidate.Folder, f) : null;
}

/// <summary>The outcome of loading packs. Never thrown: every problem is a warning.</summary>
public sealed class PackLoadReport
{
    /// <summary>Packs now registered.</summary>
    public List<LoadedPack> Loaded { get; } = new();
    /// <summary>Packs that were skipped or failed, in plain-language form.</summary>
    public List<PackWarning> Warnings { get; } = new();
    /// <summary>User packs waiting for an explicit approval (not loaded).</summary>
    public List<PackCandidate> PendingApproval { get; } = new();
}

/// <summary>Where packs are searched. Nothing else is ever searched.</summary>
/// <param name="BundledFolder">The <c>packs</c> folder next to the program (trusted: installed with Rotula).</param>
/// <param name="UserFolder">The user's own folder; packs there need an explicit approval.</param>
public sealed record PackLocations(string BundledFolder, string? UserFolder)
{
    /// <summary>
    /// The standard locations. Uses <see cref="AppContext.BaseDirectory"/>, which is the folder of the executable also for a single-file
    /// publish (where <see cref="Assembly.Location"/> is empty).
    /// </summary>
    public static PackLocations Default { get; } = new(
        Path.Combine(AppContext.BaseDirectory, "packs"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Rotula", "packs"));
}

/// <summary>Settings for one loading run.</summary>
public sealed class PackLoadOptions
{
    /// <summary>Search folders (default: next to the program, and the user's folder).</summary>
    public PackLocations Locations { get; init; } = PackLocations.Default;
    /// <summary>Approvals for user packs.</summary>
    public PackTrustStore TrustStore { get; init; } = new();
    /// <summary>Where providers are registered (default: the application registry).</summary>
    public ProviderRegistry Registry { get; init; } = ProviderRegistry.Default;
    /// <summary>When false, the user's folder is not even looked at.</summary>
    public bool IncludeUserPacks { get; init; } = true;
}

/// <summary>
/// Finds provider packs in two folders, checks them, loads each into its own <see cref="AssemblyLoadContext"/> and registers its
/// providers. A pack that cannot be used never breaks the application: the result is a <see cref="PackWarning"/>.
/// </summary>
/// <remarks>
/// <para>Trust model: packs are code. The <c>packs</c> folder next to the program is installed with Rotula and trusted. A pack in
/// <c>%AppData%\Rotula\packs</c> is loaded only if the user approved this exact content (SHA-256 of every file) through
/// <see cref="PackTrustStore"/>; no other folder is ever searched, and folders that contain links are skipped.</para>
/// <para>A pack carries its own dependencies. The loader shares only the contract assemblies and the .NET framework with the host
/// (so <see cref="IDatabaseProviderPack"/> is the same type on both sides); everything else, managed or native, is resolved from the
/// pack's folder through <see cref="AssemblyDependencyResolver"/> plus a probe of <c>runtimes/{rid}/native</c>.</para>
/// </remarks>
public static class PackLoader
{
    /// <summary>Finds pack folders without loading anything (to list packs, or to ask for approval).</summary>
    public static IReadOnlyList<PackCandidate> Discover(PackLoadOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var list = new List<PackCandidate>();
        AddFrom(list, options.Locations.BundledFolder, PackOrigin.Bundled);
        if (options.IncludeUserPacks && options.Locations.UserFolder is { } user) AddFrom(list, user, PackOrigin.User);
        return list;
    }

    private static void AddFrom(List<PackCandidate> list, string root, PackOrigin origin)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            if (PackFingerprint.IsLink(new DirectoryInfo(root))) { list.Add(new PackCandidate(root, origin, null, "The packs folder is a link.", null)); return; }
            foreach (string folder in Directory.EnumerateDirectories(root).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                list.Add(Inspect(folder, origin));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable packs folder means no packs; the application keeps working.
        }
    }

    private static PackCandidate Inspect(string folder, PackOrigin origin)
    {
        try
        {
            if (PackFingerprint.IsLink(new DirectoryInfo(folder))) return new PackCandidate(folder, origin, null, "The folder is a link.", null);
            string manifestPath = Path.Combine(folder, "pack.json");
            var info = new FileInfo(manifestPath);
            if (!info.Exists || PackFingerprint.IsLink(info)) return new PackCandidate(folder, origin, null, "pack.json is missing.", null);
            if (info.Length > PackManifest.MaxBytes) return new PackCandidate(folder, origin, null, "pack.json is too large.", null);
            var manifest = PackManifest.TryParse(File.ReadAllText(manifestPath), out string? error);
            // Bundled packs are trusted by location and can be big (native libraries); only user packs are fingerprinted.
            string? fingerprint = origin == PackOrigin.User && manifest is not null ? PackFingerprint.Compute(folder) : null;
            if (origin == PackOrigin.User && manifest is not null && fingerprint is null)
                return new PackCandidate(folder, origin, null, "The folder contains links, or is too large.", null);
            return new PackCandidate(folder, origin, manifest, error, fingerprint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new PackCandidate(folder, origin, null, "The folder could not be read: " + ex.Message, null);
        }
    }

    /// <summary>The warning text a user must see before approving <paramref name="candidate"/>.</summary>
    public static PackTrustPrompt CreateTrustPrompt(PackCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.Origin != PackOrigin.User || candidate.Fingerprint is null) throw new InvalidOperationException("Only user packs are approved.");
        return new PackTrustPrompt(PackKeys.TrustTitle, PackKeys.TrustWarning, candidate, candidate.Fingerprint[..12]);
    }

    /// <summary>Records the user's approval of exactly what was shown in the prompt. Call it only after the user confirmed.</summary>
    public static void Approve(PackTrustPrompt prompt, PackTrustStore store)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(store);
        store.Trust(prompt.Candidate.Manifest!.Id, prompt.Candidate.Fingerprint!);
    }

    /// <summary>Discovers and loads every usable pack. Never throws for a pack problem.</summary>
    public static PackLoadReport LoadAll(PackLoadOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var report = new PackLoadReport();
        foreach (var candidate in Discover(options)) LoadOne(candidate, options, report);
        return report;
    }

    /// <summary>Loads one discovered pack (after the trust check) and adds the result to <paramref name="report"/>.</summary>
    public static void LoadOne(PackCandidate candidate, PackLoadOptions options, PackLoadReport report)
    {
        string id = candidate.DisplayId;
        if (candidate.Manifest is null)
        {
            report.Warnings.Add(new PackWarning(candidate.ManifestError?.Contains("link", StringComparison.OrdinalIgnoreCase) == true ? PackKeys.UnsafeFolder : PackKeys.ManifestInvalid,
                id, new[] { Path.GetFileName(candidate.Folder) }, candidate.ManifestError));
            return;
        }
        var m = candidate.Manifest;
        if (m.MinDataApiVersion.Major != DataApi.Current.Major)
        {
            // A different major in either direction: the pack's binary contract with Rotula.Data does not hold.
            string key = m.MinDataApiVersion.Major > DataApi.Current.Major ? PackKeys.NeedsNewerRotula : PackKeys.BuiltForOlderRotula;
            report.Warnings.Add(new PackWarning(key, m.Id, new[] { m.Id, m.MinDataApiVersion.ToString() }));
            return;
        }
        if (m.MinDataApiVersion > DataApi.Current)
        {
            report.Warnings.Add(new PackWarning(PackKeys.NeedsNewerRotula, m.Id, new[] { m.Id, m.MinDataApiVersion.ToString() }));
            return;
        }

        if (candidate.Origin == PackOrigin.User)
        {
            // Re-hash right before loading: the approval is for these bytes, not for what the folder held earlier.
            string? now = PackFingerprint.Compute(candidate.Folder);
            if (now is null || !options.TrustStore.IsTrusted(m.Id, now))
            {
                bool changed = options.TrustStore.HasEarlierApproval(m.Id);
                report.Warnings.Add(new PackWarning(changed ? PackKeys.UserPackChanged : PackKeys.UserPackNeedsApproval, m.Id, new[] { m.Id }));
                if (now is not null) report.PendingApproval.Add(new PackCandidate(candidate.Folder, candidate.Origin, m, null, now));
                return;
            }
        }

        PackLoadContext? context = null;
        try
        {
            string entryPath = Path.GetFullPath(Path.Combine(candidate.Folder, m.EntryAssembly));
            if (!IsInside(candidate.Folder, entryPath) || !File.Exists(entryPath))
            {
                report.Warnings.Add(new PackWarning(PackKeys.LoadFailed, m.Id, new[] { m.Id }, "The entry assembly is missing."));
                return;
            }
            context = new PackLoadContext(m.Id, entryPath, candidate.Folder);
            var assembly = context.LoadFromAssemblyPath(entryPath);
            var pack = CreatePack(assembly, m);
            if (pack is null)
            {
                report.Warnings.Add(new PackWarning(PackKeys.EntryMismatch, m.Id, new[] { m.Id }, "No usable IDatabaseProviderPack entry class."));
                return;
            }
            if (!string.Equals(pack.PackId, m.Id, StringComparison.OrdinalIgnoreCase))
            {
                report.Warnings.Add(new PackWarning(PackKeys.EntryMismatch, m.Id, new[] { m.Id }, $"Pack id '{pack.PackId}' differs from pack.json."));
                return;
            }
            // The providers the pack really registers must be exactly the ones it declared: a user approves what the manifest says.
            var declared = new HashSet<string>(m.ProviderIds, StringComparer.OrdinalIgnoreCase);
            var providers = pack.GetProviders();
            // Read everything the UI will ask for now, while a failure (a missing dependency, a throwing property) can still be contained.
            foreach (var provider in providers) Probe(provider);
            var actual = new HashSet<string>(providers.Select(p => p.Id), StringComparer.OrdinalIgnoreCase);
            if (!declared.SetEquals(actual))
            {
                report.Warnings.Add(new PackWarning(PackKeys.EntryMismatch, m.Id, new[] { m.Id }, "The registered providers differ from providerIds in pack.json."));
                return;
            }
            options.Registry.Register(pack);
            report.Loaded.Add(new LoadedPack(candidate, pack, context));
        }
        catch (ProviderRegistrationException ex)
        {
            report.Warnings.Add(new PackWarning(ex.Key, m.Id, new[] { ex.Argument }));
        }
        // A pack is foreign code: any failure while loading or starting it (missing file, wrong architecture, exception in its
        // constructor or in GetProviders) must end as a warning, never as a crash of the application.
        catch (Exception ex) when (ex is BadImageFormatException or FileNotFoundException or FileLoadException or DllNotFoundException)
        {
            report.Warnings.Add(new PackWarning(PackKeys.LoadFailed, m.Id, new[] { m.Id }, Describe(ex)));
        }
        catch (Exception ex)
        {
            report.Warnings.Add(new PackWarning(PackKeys.StartupFailed, m.Id, new[] { m.Id }, Describe(ex)));
        }
    }

    /// <summary>Touches the descriptive members of a descriptor so that lazy failures surface during loading, not later in the UI.</summary>
    private static void Probe(IProviderDescriptor d)
    {
        _ = (d.Id, d.DisplayNameKey, d.DisplayName, d.Category, d.DefaultPort, d.RequiresServer);
        _ = (d.SupportedAuthentication.Count, d.Options.Count, d.Dialect.Id, d.ReadOnly.Description, d.Errors);
        foreach (var o in d.Options) _ = (o.Key, o.LabelKey, o.Kind, o.Choices.Count);
    }

    private static string Describe(Exception ex)
    {
        var inner = ex is TargetInvocationException { InnerException: { } i } ? i : ex;
        return inner.GetType().Name + ": " + inner.Message;
    }

    private static IDatabaseProviderPack? CreatePack(Assembly assembly, PackManifest manifest)
    {
        Type[] types;
        try { types = assembly.GetExportedTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t is not null).ToArray()!; }
        var candidates = types.Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IDatabaseProviderPack).IsAssignableFrom(t)
                                          && t.GetConstructor(Type.EmptyTypes) is not null).ToList();
        if (manifest.EntryType is not null) candidates = candidates.Where(t => t.FullName == manifest.EntryType).ToList();
        else if (candidates.Count != 1) return null;
        if (candidates.Count != 1) return null;
        return (IDatabaseProviderPack?)Activator.CreateInstance(candidates[0]);
    }

    private static bool IsInside(string folder, string path)
    {
        string root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Load context of one pack. Contract assemblies and the framework come from the default context (same types as the host);
    /// everything else comes from the pack's folder, managed through the dependency resolver and native by probing.
    /// </summary>
    private sealed class PackLoadContext : AssemblyLoadContext
    {
        // The assemblies a pack shares with the host. Anything else the host also has (SqlClient, Npgsql) is loaded privately from the pack.
        private static readonly string[] Shared = { "Rotula.Data", "Rotula.Data.Abstractions", "Rotula.Core" };
        private readonly AssemblyDependencyResolver _resolver;
        private readonly string _folder;

        public PackLoadContext(string name, string mainAssemblyPath, string folder) : base("RotulaPack:" + name, isCollectible: false)
        {
            _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
            _folder = Path.GetFullPath(folder);
        }

        protected override Assembly? Load(AssemblyName name)
        {
            string? simple = name.Name;
            if (simple is null || Shared.Contains(simple, StringComparer.OrdinalIgnoreCase) || IsFramework(simple)) return null;
            string? path = _resolver.ResolveAssemblyToPath(name);
            // Without a deps.json (hand-built pack) a plain file next to the entry assembly is accepted, but only inside the pack folder.
            path ??= Path.Combine(_folder, simple + ".dll");
            return File.Exists(path) && IsInside(_folder, Path.GetFullPath(path)) ? LoadFromAssemblyPath(path) : null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            if (path is null)
            {
                // Packs built without a deps.json keep natives under runtimes/{rid}/native; probe the usual places, never outside the folder.
                string rid = RuntimeInformation.RuntimeIdentifier;
                string[] dirs = { "", Path.Combine("runtimes", rid, "native"), Path.Combine("runtimes", rid.Split('-')[0] + "-" + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(), "native"), Path.Combine("runtimes", rid.Split('-')[0], "native") };
                string[] names = unmanagedDllName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? new[] { unmanagedDllName } : new[] { unmanagedDllName + ".dll", unmanagedDllName };
                path = dirs.SelectMany(d => names.Select(n => Path.GetFullPath(Path.Combine(_folder, d, n)))).FirstOrDefault(p => File.Exists(p) && IsInside(_folder, p));
            }
            return path is not null && IsInside(_folder, Path.GetFullPath(path)) ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
        }

        private static bool IsFramework(string simple)
            => simple.StartsWith("System.", StringComparison.Ordinal) || simple is "System" or "mscorlib" or "netstandard" or "WindowsBase" or "Microsoft.CSharp" or "Microsoft.VisualBasic.Core"
               || simple.StartsWith("Microsoft.Win32.", StringComparison.Ordinal);
    }
}
