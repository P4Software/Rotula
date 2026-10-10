using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using Rotula.Data;
using Rotula.Data.Packs;

namespace Rotula.Tests;

/// <summary>
/// Provider packs: discovery from the two folders, the manifest and API-version checks, isolated loading with the pack's own managed and
/// native dependencies, the trust rules for packs in the user's folder, and failure isolation. The fixture is tests/Rotula.Data.SamplePack,
/// copied next to the tests as testpacks/sample (it is deliberately NOT referenced, so the default load context cannot resolve it).
/// </summary>
public class DataPackTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rotula-packs-" + Guid.NewGuid().ToString("N"));
    private static string SampleSource => Path.Combine(AppContext.BaseDirectory, "testpacks", "sample");

    public DataPackTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        // Loaded native libraries keep their files open until the process ends; a leftover temp folder is harmless.
        try { Directory.Delete(_root, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private string Place(string parent, string name = "sample")
    {
        string dest = Path.Combine(_root, parent, name);
        CopyDirectory(SampleSource, dest);
        return dest;
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
        foreach (string d in Directory.GetDirectories(from)) CopyDirectory(d, Path.Combine(to, Path.GetFileName(d)));
    }

    private PackLoadOptions Options(bool user = false, ProviderRegistry? registry = null) => new()
    {
        Locations = new PackLocations(Path.Combine(_root, "bundled"), Path.Combine(_root, "user")),
        TrustStore = new PackTrustStore(Path.Combine(_root, "trusted.json")),
        Registry = registry ?? new ProviderRegistry(),
        IncludeUserPacks = user,
    };

    private static void EditManifest(string folder, Func<string, string> edit)
    {
        string p = Path.Combine(folder, "pack.json");
        File.WriteAllText(p, edit(File.ReadAllText(p)));
    }

    [Fact]
    public void TheFixture_IsBuilt_AndNextToTheTests()
    {
        Assert.True(File.Exists(Path.Combine(SampleSource, "pack.json")), SampleSource);
        Assert.True(File.Exists(Path.Combine(SampleSource, "Rotula.Data.SamplePack.dll")));
        Assert.True(File.Exists(Path.Combine(SampleSource, "Rotula.Data.SamplePack.Dep.dll")));
        Assert.False(File.Exists(Path.Combine(SampleSource, "Rotula.Data.dll")), "the pack must not carry its own copy of the contract assembly");
    }

    // ---------------------------------------------------------------- loading and isolation

    [Fact]
    public void ABundledPack_IsDiscovered_Loaded_AndRegistered()
    {
        Place("bundled");
        var options = Options();
        var report = PackLoader.LoadAll(options);

        Assert.Empty(report.Warnings);
        var loaded = Assert.Single(report.Loaded);
        Assert.Equal("sample", loaded.Candidate.Manifest!.Id);
        Assert.Equal(PackOrigin.Bundled, loaded.Candidate.Origin);
        Assert.NotNull(loaded.LicenseNoticesPath);
        Assert.Contains("sample", options.Registry.PackIds);
        var d = options.Registry.Get("sample-memory");
        Assert.Equal("Sample.Provider", d.DisplayNameKey);
        Assert.Equal(6, options.Registry.Providers.Count);
        Assert.Equal("sample", options.Registry.GetDisplayEntries().Single(e => e.Id == "sample-memory").PackId);
        // The five original providers are untouched.
        Assert.Equal(typeof(DatabaseProvider).Assembly, options.Registry.Get("sqlserver").GetType().Assembly);
    }

    [Fact]
    public void APack_RunsInItsOwnContext_AndResolvesItsOwnManagedDependency()
    {
        Place("bundled");
        var report = PackLoader.LoadAll(Options());
        var loaded = Assert.Single(report.Loaded);

        var packAssembly = loaded.Pack.GetType().Assembly;
        Assert.NotSame(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(packAssembly));
        Assert.StartsWith("RotulaPack:", AssemblyLoadContext.GetLoadContext(packAssembly)!.Name);
        // The contract type is shared with the host (otherwise the cast in the loader could not have worked) ...
        Assert.Same(typeof(IDatabaseProviderPack).Assembly, loaded.Pack.GetType().GetInterface(nameof(IDatabaseProviderPack))!.Assembly);
        // ... while the pack-private dependency exists only inside the pack's context.
        Assert.Contains("pack-private-dependency", loaded.Pack.GetProviders()[0].DisplayName);
        Assert.DoesNotContain(AssemblyLoadContext.Default.Assemblies, a => a.GetName().Name == "Rotula.Data.SamplePack.Dep");
        Assert.Contains(loaded.Context.Assemblies, a => a.GetName().Name == "Rotula.Data.SamplePack.Dep");
        // The pack's copy of the contract assemblies is never used.
        Assert.DoesNotContain(loaded.Context.Assemblies, a => a.GetName().Name is "Rotula.Data" or "Rotula.Core" or "Rotula.Data.Abstractions");
    }

    [Fact]
    public void APack_FindsItsOwnNativeLibrary_InsideItsFolder()
    {
        string folder = Place("bundled");
        var report = PackLoader.LoadAll(Options());
        var loaded = Assert.Single(report.Loaded);

        var type = loaded.Pack.GetType().Assembly.GetType("Rotula.Data.SamplePack.SampleDescriptor")!;
        int version = (int)type.GetMethod("NativeVersionNumber")!.Invoke(null, null)!;
        Assert.True(version >= 3_000_000, "sqlite3_libversion_number returned " + version);

        // The library that answered came from the pack folder, not from anywhere else the host happens to search.
        var modules = Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Select(m => m.FileName);
        Assert.Contains(modules, m => m.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && m.EndsWith("e_sqlite3.dll", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TwoLoadsOfTheSamePack_DoNotCollide_InSeparateRegistries()
    {
        Place("bundled");
        var a = PackLoader.LoadAll(Options());
        var b = PackLoader.LoadAll(Options());
        Assert.Single(a.Loaded);
        Assert.Single(b.Loaded);
        Assert.NotSame(a.Loaded[0].Context, b.Loaded[0].Context);
    }

    [Fact]
    public void LoadingTheSamePackTwice_IntoOneRegistry_IsAWarning_NotACrash()
    {
        Place("bundled", "one");
        Place("bundled", "two");
        var report = PackLoader.LoadAll(Options());
        Assert.Single(report.Loaded);
        var w = Assert.Single(report.Warnings);
        Assert.Contains(w.Key, new[] { PackKeys.DuplicatePack, PackKeys.DuplicateProvider });
    }

    // ---------------------------------------------------------------- manifest and API version

    [Theory]
    [InlineData("""{ "id":"ok", "providerIds":["a"], "entryAssembly":"x.dll" }""", true)]
    [InlineData("""{ "id":"ok", "providerIds":["a","b"], "entryAssembly":"x.dll", "minDataApiVersion":"1.2", "licenseNoticesFile":"N.txt", "entryType":"A.B" }""", true)]
    [InlineData("""{ "id":"Bad Id", "providerIds":["a"], "entryAssembly":"x.dll" }""", false)]
    [InlineData("""{ "providerIds":["a"], "entryAssembly":"x.dll" }""", false)]
    [InlineData("""{ "id":"ok", "entryAssembly":"x.dll" }""", false)]
    [InlineData("""{ "id":"ok", "providerIds":[], "entryAssembly":"x.dll" }""", false)]
    [InlineData("""{ "id":"ok", "providerIds":["a","A"], "entryAssembly":"x.dll" }""", false)]
    [InlineData("""{ "id":"ok", "providerIds":["Bad"], "entryAssembly":"x.dll" }""", false)]
    [InlineData("""{ "id":"ok", "providerIds":["a"] }""", false)]
    [InlineData("""{ "id":"ok", "providerIds":["a"], "entryAssembly":"..\\evil.dll" }""", false)]
    [InlineData("""{ "id":"ok", "providerIds":["a"], "entryAssembly":"sub/x.dll" }""", false)]
    [InlineData("""{ "id":"ok", "providerIds":["a"], "entryAssembly":"x.exe" }""", false)]
    [InlineData("""{ "id":"ok", "providerIds":["a"], "entryAssembly":"x.dll", "licenseNoticesFile":"..\\N.txt" }""", false)]
    [InlineData("""{ "id":"ok", "providerIds":["a"], "entryAssembly":"x.dll", "minDataApiVersion":"banana" }""", false)]
    [InlineData("[]", false)]
    [InlineData("not json", false)]
    [InlineData("", false)]
    public void Manifests_AreValidated(string json, bool valid)
    {
        var m = PackManifest.TryParse(json, out var error);
        Assert.Equal(valid, m is not null);
        Assert.Equal(valid, error is null);
    }

    [Fact]
    public void AMissingOrBrokenManifest_IsAWarning_AndNothingIsLoaded()
    {
        string folder = Place("bundled");
        File.Delete(Path.Combine(folder, "pack.json"));
        Directory.CreateDirectory(Path.Combine(_root, "bundled", "empty"));
        string broken = Place("bundled", "broken");
        File.WriteAllText(Path.Combine(broken, "pack.json"), "{ nope");
        var options = Options();
        var report = PackLoader.LoadAll(options);
        Assert.Empty(report.Loaded);
        Assert.Equal(3, report.Warnings.Count);
        Assert.All(report.Warnings, w => Assert.Equal(PackKeys.ManifestInvalid, w.Key));
        Assert.Equal(5, options.Registry.Providers.Count);
    }

    [Fact]
    public void APackForANewerRotula_IsRefused_WithItsOwnKey()
    {
        string folder = Place("bundled");
        EditManifest(folder, s => s.Replace("\"minDataApiVersion\": \"1.0\"", "\"minDataApiVersion\": \"2.0\""));
        var report = PackLoader.LoadAll(Options());
        var w = Assert.Single(report.Warnings);
        Assert.Equal(PackKeys.NeedsNewerRotula, w.Key);
        Assert.Equal("sample", w.PackId);
        Assert.Empty(report.Loaded);

        // A newer minor version is also too new; an equal or lower one is fine.
        EditManifest(folder, s => s.Replace("\"minDataApiVersion\": \"2.0\"", "\"minDataApiVersion\": \"1." + (DataApi.Current.Minor + 1) + "\""));
        Assert.Equal(PackKeys.NeedsNewerRotula, Assert.Single(PackLoader.LoadAll(Options()).Warnings).Key);
        EditManifest(folder, s => s.Replace("\"minDataApiVersion\": \"1." + (DataApi.Current.Minor + 1) + "\"", "\"minDataApiVersion\": \"1.0\""));
        Assert.Single(PackLoader.LoadAll(Options()).Loaded);
    }

    [Fact]
    public void APackBuiltForAnOlderIncompatibleApi_IsRefused()
    {
        string folder = Place("bundled");
        EditManifest(folder, s => s.Replace("\"minDataApiVersion\": \"1.0\"", "\"minDataApiVersion\": \"0.9\""));
        var w = Assert.Single(PackLoader.LoadAll(Options()).Warnings);
        Assert.Equal(PackKeys.BuiltForOlderRotula, w.Key);
    }

    // ---------------------------------------------------------------- failure isolation

    [Fact]
    public void ACorruptAssembly_IsAWarning_NeverAnException()
    {
        string folder = Place("bundled");
        File.WriteAllBytes(Path.Combine(folder, "Rotula.Data.SamplePack.dll"), new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 });
        var options = Options();
        var report = PackLoader.LoadAll(options);
        var w = Assert.Single(report.Warnings);
        Assert.Equal(PackKeys.LoadFailed, w.Key);
        Assert.NotNull(w.Detail);
        Assert.Empty(report.Loaded);
        Assert.Equal(5, options.Registry.Providers.Count);
        Assert.Contains("sample", w.EnglishText);
    }

    [Fact]
    public void AMissingEntryAssembly_AndAMissingDependency_AreWarnings()
    {
        string folder = Place("bundled");
        File.Delete(Path.Combine(folder, "Rotula.Data.SamplePack.dll"));
        Assert.Equal(PackKeys.LoadFailed, Assert.Single(PackLoader.LoadAll(Options()).Warnings).Key);

        string folder2 = Place("bundled2");
        File.Delete(Path.Combine(folder2, "Rotula.Data.SamplePack.Dep.dll"));
        var options = new PackLoadOptions
        {
            Locations = new PackLocations(Path.Combine(_root, "bundled2"), null),
            TrustStore = new PackTrustStore(Path.Combine(_root, "t2.json")),
            Registry = new ProviderRegistry(),
            IncludeUserPacks = false,
        };
        var report = PackLoader.LoadAll(options);
        // The dependency is needed as soon as the pack describes itself, which is part of starting.
        Assert.Empty(report.Loaded);
        Assert.Single(report.Warnings);
    }

    [Fact]
    public void ACodeErrorInsideThePack_IsContained()
    {
        string folder = Place("bundled");
        EditManifest(folder, s => s.Replace("Rotula.Data.SamplePack.SamplePack", "Rotula.Data.SamplePack.ThrowingPack"));
        var options = Options();
        var report = PackLoader.LoadAll(options);
        var w = Assert.Single(report.Warnings);
        Assert.Equal(PackKeys.StartupFailed, w.Key);
        Assert.Contains("boom from pack code", w.Detail);
        Assert.Empty(report.Loaded);
        Assert.DoesNotContain("sample", options.Registry.PackIds);
        Assert.Contains("Rotula itself is not affected", w.EnglishText);
    }

    private PackLoadOptions OptionsFor(string parent) => new()
    {
        Locations = new PackLocations(Path.Combine(_root, parent), null),
        TrustStore = new PackTrustStore(Path.Combine(_root, parent + ".json")),
        Registry = new ProviderRegistry(),
        IncludeUserPacks = false,
    };

    [Fact]
    public void AMissingEntryClass_AndMismatchedProviders_AreEntryMismatches()
    {
        string folder = Place("m1");
        EditManifest(folder, s => s.Replace("Rotula.Data.SamplePack.SamplePack", "Rotula.Data.SamplePack.DoesNotExist"));
        Assert.Equal(PackKeys.EntryMismatch, Assert.Single(PackLoader.LoadAll(OptionsFor("m1")).Warnings).Key);

        string folder2 = Place("m2");
        EditManifest(folder2, s => s.Replace("\"sample-memory\"", "\"something-else\""));
        var w = Assert.Single(PackLoader.LoadAll(OptionsFor("m2")).Warnings);
        Assert.Equal(PackKeys.EntryMismatch, w.Key);
        Assert.Contains("providerIds", w.Detail);

        string folder3 = Place("m3");
        EditManifest(folder3, s => s.Replace("\"id\": \"sample\"", "\"id\": \"another\""));
        Assert.Equal(PackKeys.EntryMismatch, Assert.Single(PackLoader.LoadAll(OptionsFor("m3")).Warnings).Key);
    }

    [Fact]
    public void APackCannotTakeOrReplaceACoreProviderId()
    {
        string folder = Place("bundled");
        EditManifest(folder, s => s.Replace("Rotula.Data.SamplePack.SamplePack", "Rotula.Data.SamplePack.ClashingPack").Replace("\"sample-memory\"", "\"sqlserver\""));
        var options = Options();
        var original = options.Registry.Get("sqlserver");
        var report = PackLoader.LoadAll(options);
        var w = Assert.Single(report.Warnings);
        Assert.Equal(PackKeys.DuplicateProvider, w.Key);
        Assert.Equal(new[] { "sqlserver" }, w.Args);
        Assert.Same(original, options.Registry.Get("sqlserver"));
        Assert.Empty(report.Loaded);
    }

    [Fact]
    public void OnePackFailing_DoesNotStopTheOthers()
    {
        Place("bundled", "a-broken");
        File.WriteAllBytes(Path.Combine(_root, "bundled", "a-broken", "Rotula.Data.SamplePack.dll"), new byte[] { 0 });
        Place("bundled", "b-good");
        var report = PackLoader.LoadAll(Options());
        Assert.Single(report.Loaded);
        Assert.Single(report.Warnings);
    }

    [Fact]
    public void EveryPackWarning_HasPlainLanguageEnglishText_WithTheIdAndWithoutTechnicalNoise()
    {
        foreach (var field in typeof(PackKeys).GetFields().Where(f => f.IsLiteral && f.Name is not ("TrustTitle" or "TrustWarning")))
        {
            string key = (string)field.GetRawConstantValue()!;
            string text = new PackWarning(key, "my-pack", new[] { "my-pack" }).EnglishText;
            Assert.Contains("my-pack", text);
            Assert.DoesNotContain("Exception", text);
            Assert.DoesNotContain("Data.Pack.", text);
            Assert.True(text.EndsWith('.'));
        }
    }

    // ---------------------------------------------------------------- the user's folder needs an explicit approval

    [Fact]
    public void AUserPack_IsNotLoaded_UntilTheUserApproves_ThenLoadsOnlyWhileUnchanged()
    {
        string folder = Place("user");
        var options = Options(user: true);

        var first = PackLoader.LoadAll(options);
        Assert.Empty(first.Loaded);
        Assert.Equal(PackKeys.UserPackNeedsApproval, Assert.Single(first.Warnings).Key);
        var pending = Assert.Single(first.PendingApproval);
        Assert.NotNull(pending.Fingerprint);
        Assert.DoesNotContain("sample", options.Registry.PackIds);

        // The prompt says, in plain language, what an approval means.
        var prompt = PackLoader.CreateTrustPrompt(pending);
        Assert.Equal(PackKeys.TrustTitle, prompt.TitleKey);
        Assert.Equal(PackKeys.TrustWarning, prompt.WarningKey);
        Assert.Contains("is a program", prompt.EnglishWarning);
        Assert.Contains("trust", prompt.EnglishWarning);
        Assert.Equal(12, prompt.ShortFingerprint.Length);
        Assert.Contains(folder, prompt.EnglishWarning);

        // Approving remembers the SHA-256 of the pack ...
        PackLoader.Approve(prompt, options.TrustStore);
        var saved = Assert.Single(options.TrustStore.Load());
        Assert.Equal("sample", saved.PackId);
        Assert.Equal(pending.Fingerprint, saved.Sha256);
        Assert.Matches("^[0-9a-f]{64}$", saved.Sha256);

        var second = PackLoader.LoadAll(options);
        Assert.Empty(second.Warnings);
        Assert.Equal(PackOrigin.User, Assert.Single(second.Loaded).Candidate.Origin);
    }

    [Fact]
    public void ATamperedUserPack_AsksAgain_WhateverFileWasChanged()
    {
        // Tampering happens after the approval and before the load. (A loaded pack keeps its files open, so it cannot be changed afterwards.)
        foreach (string victim in new[] { "NOTICES.txt", "Rotula.Data.SamplePack.Dep.dll", "pack.json", "Rotula.Data.SamplePack.dll" })
        {
            string parent = "user-" + victim;
            Place(parent);
            var options = new PackLoadOptions
            {
                Locations = new PackLocations(Path.Combine(_root, "none"), Path.Combine(_root, parent)),
                TrustStore = new PackTrustStore(Path.Combine(_root, victim + ".json")),
                Registry = new ProviderRegistry(),
            };
            var pending = Assert.Single(PackLoader.LoadAll(options).PendingApproval);
            PackLoader.Approve(PackLoader.CreateTrustPrompt(pending), options.TrustStore);

            // Swap one file after the approval: a dependency, the entry assembly, a notice file or the manifest.
            string path = Path.Combine(_root, parent, "sample", victim);
            File.AppendAllText(path, victim.EndsWith(".dll") ? " " : " ");
            var report = PackLoader.LoadAll(options);
            Assert.Empty(report.Loaded);
            Assert.Equal(PackKeys.UserPackChanged, Assert.Single(report.Warnings).Key);
            Assert.Single(report.PendingApproval);
        }
    }

    [Fact]
    public void AFileAddedToAnApprovedUserPack_AlsoInvalidatesTheApproval()
    {
        Place("user");
        var options = Options(user: true);
        PackLoader.Approve(PackLoader.CreateTrustPrompt(Assert.Single(PackLoader.LoadAll(options).PendingApproval)), options.TrustStore);
        File.WriteAllText(Path.Combine(_root, "user", "sample", "extra.dll"), "x");
        Assert.Empty(PackLoader.LoadAll(options).Loaded);
    }

    [Fact]
    public void ApprovalIsPerPack_AndCanBeRevoked()
    {
        Place("user");
        var options = Options(user: true);
        PackLoader.Approve(PackLoader.CreateTrustPrompt(Assert.Single(PackLoader.LoadAll(options).PendingApproval)), options.TrustStore);
        Assert.True(options.TrustStore.HasEarlierApproval("sample"));
        Assert.False(options.TrustStore.HasEarlierApproval("other"));
        options.TrustStore.Revoke("sample");
        Assert.Empty(options.TrustStore.Load());
        Assert.Empty(PackLoader.LoadAll(options).Loaded);
    }

    [Fact]
    public void TheUserFolder_IsIgnoredWhenNotAskedFor_AndNoOtherFolderIsEverSearched()
    {
        Place("user");
        Place("elsewhere");
        Place("bundled-other-name", "sample");
        var options = Options(user: false);
        PackLoader.Approve(PackLoader.CreateTrustPrompt(new PackLoader_Helper(_root).UserCandidate()), options.TrustStore);   // approved, but the folder is switched off
        var report = PackLoader.LoadAll(options);
        Assert.Empty(report.Loaded);
        Assert.Empty(report.Warnings);
        Assert.Empty(PackLoader.Discover(options));
    }

    private sealed class PackLoader_Helper
    {
        private readonly string _root;
        public PackLoader_Helper(string root) => _root = root;
        public PackCandidate UserCandidate()
        {
            var o = new PackLoadOptions { Locations = new PackLocations(Path.Combine(_root, "none"), Path.Combine(_root, "user")), TrustStore = new PackTrustStore(Path.Combine(_root, "x.json")), Registry = new ProviderRegistry() };
            return PackLoader.Discover(o).Single();
        }
    }

    [Fact]
    public void BundledPacks_NeedNoApproval_AndHaveNoPrompt()
    {
        Place("bundled");
        var candidate = Assert.Single(PackLoader.Discover(Options()));
        Assert.Equal(PackOrigin.Bundled, candidate.Origin);
        Assert.Null(candidate.Fingerprint);
        Assert.Throws<InvalidOperationException>(() => PackLoader.CreateTrustPrompt(candidate));
    }

    [Fact]
    public void LinkedPackFolders_AreSkipped()
    {
        string real = Place("real");
        string linkParent = Path.Combine(_root, "user");
        Directory.CreateDirectory(linkParent);
        try { Directory.CreateSymbolicLink(Path.Combine(linkParent, "sample"), real); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException) { return; }   // no right to create links here: nothing to test
        var options = Options(user: true);
        var report = PackLoader.LoadAll(options);
        Assert.Empty(report.Loaded);
        Assert.Empty(report.PendingApproval);
        Assert.Equal(PackKeys.UnsafeFolder, Assert.Single(report.Warnings).Key);
    }

    [Fact]
    public void AFingerprint_ChangesWithAnyFile_AndIsStableOtherwise()
    {
        string a = Place("fp", "a");
        string b = Place("fp", "b");
        string? ha = PackFingerprint.Compute(a);
        Assert.NotNull(ha);
        Assert.Equal(ha, PackFingerprint.Compute(b));        // same content in another folder, same fingerprint
        Assert.Equal(ha, PackFingerprint.Compute(a));
        File.WriteAllText(Path.Combine(b, "NOTICES.txt"), "changed");
        Assert.NotEqual(ha, PackFingerprint.Compute(b));
        File.Move(Path.Combine(a, "NOTICES.txt"), Path.Combine(a, "NOTICES2.txt"));   // a rename counts too
        Assert.NotEqual(ha, PackFingerprint.Compute(a));
        Assert.Null(PackFingerprint.Compute(Path.Combine(_root, "does-not-exist")));
    }

    [Fact]
    public void TheTrustStore_ReadsDamagedFilesAsNothingApproved()
    {
        string path = Path.Combine(_root, "damaged.json");
        File.WriteAllText(path, "{ not json");
        var store = new PackTrustStore(path);
        Assert.Empty(store.Load());
        Assert.False(store.IsTrusted("sample", new string('a', 64)));
        File.WriteAllText(path, """{ "version": 99, "packs": [ { "packId": "sample", "sha256": "%" } ] }""");
        Assert.Empty(store.Load());
        File.WriteAllText(path, new string(' ', 300_000));
        Assert.Empty(store.Load());
        Assert.Throws<ArgumentException>(() => store.Trust("Bad Id", new string('a', 64)));
        Assert.Throws<ArgumentException>(() => store.Trust("ok", "short"));
        store.Trust("ok", new string('A', 64));
        Assert.True(store.IsTrusted("ok", new string('a', 64)));          // hex case does not matter
        Assert.Single(store.Load());
    }

    [Fact]
    public void TheDefaultLocations_AreNextToTheProgram_AndInTheUsersAppData()
    {
        var l = PackLocations.Default;
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "packs"), l.BundledFolder);
        Assert.EndsWith(Path.Combine("Rotula", "packs"), l.UserFolder);
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), l.UserFolder);
        // Nothing there on a developer machine: loading is a quiet no-op.
        var report = PackLoader.LoadAll(new PackLoadOptions { Registry = new ProviderRegistry(), TrustStore = new PackTrustStore(Path.Combine(_root, "d.json")), IncludeUserPacks = false });
        Assert.Empty(report.Loaded);
    }

    [Fact]
    public async Task ASettingsRecord_ForAPackProvider_SurvivesTheStore_EvenWhileThePackIsMissing()
    {
        // The connection of a pack that is not loaded must not vanish from the file on the next save.
        string file = Path.Combine(_root, "connections.json");
        var store = new JsonConnectionStore(file, new XorProtector());
        var packConn = new DatabaseConnectionSettings
        {
            Name = "ora", Provider = DatabaseProvider.Custom, ProviderId = "oracle", Host = "h", Authentication = DatabaseAuthentication.UserPassword, UserName = "u",
            Extras = ProviderExtras.Of(("serviceName", "FREEPDB1")),
        };
        // Written while a pack knows the provider is not possible here, so write through the raw file format a newer build would produce.
        File.WriteAllText(file, """{ "version": 1, "connections": [ { "name": "ora", "provider": "Custom", "providerId": "oracle", "host": "h", "authentication": "UserPassword", "userName": "u", "extras": { "serviceName": "FREEPDB1" } } ] }""");
        var loaded = await store.LoadAsync();
        Assert.Equal(0, loaded.SkippedEntries);
        var saved = Assert.Single(loaded.Connections).Settings;
        Assert.Equal(packConn, saved);
        // Saving another connection keeps it.
        await store.SaveAsync(new DatabaseConnectionSettings { Name = "lite", Provider = DatabaseProvider.Sqlite, FilePath = "C:\\a.db" }, null);
        var after = await store.LoadAsync();
        Assert.Equal(2, after.Connections.Count);
        Assert.Contains(after.Connections, c => c.Settings.ProviderId == "oracle" && c.Settings.Extras.Get("serviceName") == "FREEPDB1");
        // Opening it explains why it cannot run, instead of pretending it is another engine.
        var ex = await Assert.ThrowsAsync<DatabaseException>(() => store.OpenAsync("ora"));
        Assert.Equal(ProviderKeys.ProviderUnavailable, ex.Key);
        Assert.Equal("oracle", ex.Args[0]);
    }

    private sealed class XorProtector : ISecretProtector
    {
        public byte[] Protect(byte[] data, byte[] entropy) => data.Select(b => (byte)(b ^ 0x5A)).ToArray();
        public byte[] Unprotect(byte[] protectedData, byte[] entropy) => protectedData.Select(b => (byte)(b ^ 0x5A)).ToArray();
    }
}
