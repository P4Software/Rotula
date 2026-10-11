using System.Reflection;
using System.Security.Cryptography;
using Rotula.Data;

namespace Rotula.Tests;

/// <summary>
/// IConnectionStore.OpenWithSettingsAsync: testing an EDITED connection with its saved password, without ever exposing that password.
/// The rule is deny by default: the saved password is reused only when the edit changes nothing that decides where or how it is sent.
/// </summary>
public class DataStoreReuseTests : IDisposable
{
    private const string Saved = "Saved-Pw-marker-1";
    private const string Typed = "Typed-Pw-marker-2";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rotula-reuse-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "connections.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private sealed class SpyProtector : ISecretProtector
    {
        public int UnprotectCalls { get; private set; }
        public byte[] Protect(byte[] data, byte[] entropy) => data.Select(b => (byte)(b ^ 0x3C)).ToArray();
        public byte[] Unprotect(byte[] protectedData, byte[] entropy) { UnprotectCalls++; return protectedData.Select(b => (byte)(b ^ 0x3C)).ToArray(); }
    }

    private static DatabaseConnectionSettings Pg() => new()
    {
        Name = "prod", Provider = DatabaseProvider.PostgreSql, Host = "db.example.com", Port = 5432, Database = "labels",
        Authentication = DatabaseAuthentication.UserPassword, UserName = "reader",
    };

    private async Task<(JsonConnectionStore Store, SpyProtector Spy)> StoreWithSavedAsync(DatabaseConnectionSettings? s = null)
    {
        var spy = new SpyProtector();
        var store = new JsonConnectionStore(FilePath, spy);
        await store.SaveAsync(s ?? Pg(), Saved);
        return (store, spy);
    }

    private static string ConnectionStringOf(IDatabaseSource source) => ((AdoDatabaseSource)source).ConnectionStringForTests;

    [Fact]
    public async Task TheSavedPassword_IsUsed_WhenOnlySafeFieldsChange()
    {
        var (store, spy) = await StoreWithSavedAsync();
        var edited = Pg() with { Database = "other", CommandTimeoutSeconds = 99, ConnectTimeoutSeconds = 7 };
        var source = await store.OpenWithSettingsAsync(edited);

        Assert.Equal(1, spy.UnprotectCalls);
        string cs = ConnectionStringOf(source);
        Assert.Contains(Saved, cs);
        Assert.Contains("other", cs);                     // the EDITED database is the one tested
        Assert.Equal("other", source.Settings.Database);
    }

    [Fact]
    public async Task ANewPassword_Overrides_AndTheSavedOneIsNeverTouched()
    {
        var (store, spy) = await StoreWithSavedAsync();
        // Even a destination change is fine when the user typed a password in this very session.
        var source = await store.OpenWithSettingsAsync(Pg() with { Host = "another.example.com" }, Typed);
        Assert.Equal(0, spy.UnprotectCalls);
        string cs = ConnectionStringOf(source);
        Assert.Contains(Typed, cs);
        Assert.DoesNotContain(Saved, cs);
    }

    [Fact]
    public async Task AnEmptyNewPassword_MeansNoPassword_NotTheSavedOne()
    {
        var (store, spy) = await StoreWithSavedAsync();
        var source = await store.OpenWithSettingsAsync(Pg(), "");
        Assert.Equal(0, spy.UnprotectCalls);
        Assert.DoesNotContain(Saved, ConnectionStringOf(source));
    }

    [Fact]
    public async Task ANewConnection_CanBeTestedWithATypedPassword_WithoutBeingSaved()
    {
        var spy = new SpyProtector();
        var store = new JsonConnectionStore(FilePath, spy);
        var source = await store.OpenWithSettingsAsync(Pg() with { Name = "brand-new" }, Typed);
        Assert.Contains(Typed, ConnectionStringOf(source));
        Assert.False(File.Exists(FilePath), "testing must not write to the store");
    }

    [Fact]
    public async Task AMissingConnection_AndAWrongName_AreReported_PlainlyAndWithoutLeaking()
    {
        var (store, spy) = await StoreWithSavedAsync();
        var missing = await Assert.ThrowsAsync<DatabaseException>(() => store.OpenWithSettingsAsync(Pg() with { Name = "nope" }));
        Assert.Equal(DataKeys.ConnectionNotFound, missing.Key);
        Assert.Equal(new[] { "nope" }, missing.Args);
        Assert.DoesNotContain(Saved, missing.ToString());
        Assert.Equal(0, spy.UnprotectCalls);

        // The name comparison ignores case, exactly like the rest of the store.
        var ok = await store.OpenWithSettingsAsync(Pg() with { Name = "PROD" });
        Assert.NotNull(ok);

        // An empty store: nothing to reuse.
        var empty = new JsonConnectionStore(Path.Combine(_dir, "none.json"), new SpyProtector());
        Assert.Equal(DataKeys.ConnectionNotFound, (await Assert.ThrowsAsync<DatabaseException>(() => empty.OpenWithSettingsAsync(Pg()))).Key);
    }

    [Fact]
    public async Task InvalidEditedSettings_AreRefused_BeforeAnythingIsUnprotected()
    {
        var (store, spy) = await StoreWithSavedAsync();
        var ex = await Assert.ThrowsAsync<DatabaseException>(() => store.OpenWithSettingsAsync(Pg() with { Host = "bad host;x" }));
        Assert.Equal(DataKeys.InvalidSettings, ex.Key);
        Assert.Equal(0, spy.UnprotectCalls);
    }

    [Fact]
    public async Task AConnectionWithoutASavedPassword_StaysPasswordless()
    {
        var spy = new SpyProtector();
        var store = new JsonConnectionStore(FilePath, spy);
        await store.SaveAsync(Pg(), null);
        var source = await store.OpenWithSettingsAsync(Pg() with { Database = "x" });
        Assert.Equal(0, spy.UnprotectCalls);
        Assert.NotNull(source);
    }

    [Fact]
    public async Task AnUnreadableSavedPassword_IsReportedWithItsOwnKey()
    {
        var (_, _) = await StoreWithSavedAsync();
        var broken = new JsonConnectionStore(FilePath, new ThrowingProtector());
        var ex = await Assert.ThrowsAsync<DatabaseException>(() => broken.OpenWithSettingsAsync(Pg()));
        Assert.Equal(DataKeys.StorePasswordUnreadable, ex.Key);
    }

    private sealed class ThrowingProtector : ISecretProtector
    {
        public byte[] Protect(byte[] data, byte[] entropy) => data;
        public byte[] Unprotect(byte[] protectedData, byte[] entropy) => throw new CryptographicException("another user");
    }

    // ---------------------------------------------------------------- deny by default

    private static readonly Dictionary<string, Func<DatabaseConnectionSettings, DatabaseConnectionSettings>> Edits = new()
    {
        ["host"] = s => s with { Host = "evil.example.com" },
        ["host case"] = s => s with { Host = "DB.EXAMPLE.COM" },
        ["port"] = s => s with { Port = 5433 },
        ["port cleared"] = s => s with { Port = null },
        ["user"] = s => s with { UserName = "admin" },
        ["authentication"] = s => s with { Authentication = DatabaseAuthentication.WindowsIntegrated },
        ["encrypt off"] = s => s with { Encrypt = false },
        ["trust certificate on"] = s => s with { TrustServerCertificate = true },
        ["provider"] = s => s with { Provider = DatabaseProvider.MySql },
        ["provider id"] = s => s with { ProviderId = "postgresql" },
        ["preset"] = s => s with { PresetId = "neon" },
        ["ca certificate"] = s => s with { CaCertificatePath = "C:/other-ca.pem" },
        ["extras: pooler switch"] = s => s with { Extras = ProviderExtras.Of(("noStartupOptions", "true")) },
        ["dsn"] = s => s with { Dsn = "SomeDsn" },
        ["odbc string"] = s => s with { OdbcConnectionString = "Driver={X};Server=evil" },
        ["file path"] = s => s with { FilePath = "C:/x.db" },
    };

    public static IEnumerable<object[]> DangerousEdits() => Edits.Keys.Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(DangerousEdits))]
    public async Task AnyDestinationOrTrustChange_RequiresTheUserToTypeThePasswordAgain(string what)
    {
        var edit = Edits[what];
        var (store, spy) = await StoreWithSavedAsync();
        var edited = edit(Pg());
        DatabaseException? ex = await Record.ExceptionAsync(() => store.OpenWithSettingsAsync(edited)) as DatabaseException;
        // Some edits are not even valid settings; either way the saved password is never unprotected for them.
        Assert.NotNull(ex);
        Assert.Equal(0, spy.UnprotectCalls);
        if (ex!.Key != DataKeys.InvalidSettings)
        {
            Assert.Equal(ProviderKeys.PasswordReenterRequired, ex.Key);
            Assert.Equal(new[] { "prod" }, ex.Args);
        }
        Assert.DoesNotContain(Saved, ex.ToString());
    }

    [Fact]
    public async Task AfterTheEdit_TypingThePasswordAgain_Works()
    {
        var (store, spy) = await StoreWithSavedAsync();
        var source = await store.OpenWithSettingsAsync(Pg() with { Host = "evil.example.com" }, Typed);
        Assert.Equal(0, spy.UnprotectCalls);
        Assert.Contains(Typed, ConnectionStringOf(source));
        Assert.DoesNotContain(Saved, ConnectionStringOf(source));
    }

    [Fact]
    public void TheReusePolicy_DecidesEveryPropertyOfTheSettings_AndNewPropertiesAreDeniedByDefault()
    {
        var baseline = Pg() with { Host = "h", Dsn = "d", FilePath = "f", CaCertificatePath = "c", PresetId = "p", ProviderId = null, Extras = ProviderExtras.Of(("k", "v")) };
        Assert.True(SavedPasswordPolicy.CanReuse(baseline, baseline with { }));

        foreach (var (property, compared) in SavedPasswordPolicy.Classify())
        {
            if (!property.CanWrite) continue;
            var changed = Mutate(baseline, property);
            Assert.NotEqual(baseline, changed);
            bool reusable = SavedPasswordPolicy.CanReuse(baseline, changed);
            if (compared) Assert.False(reusable, $"changing {property.Name} must force the password to be typed again");
            else Assert.True(reusable, $"{property.Name} is on the allowlist and must not matter");
        }

        // The allowlist is short and fixed: adding to it is a security decision that needs this test to change.
        Assert.Equal(new[] { "CommandTimeoutSeconds", "ConnectTimeoutSeconds", "Database", "Name" }, SavedPasswordPolicy.SafeToChange.OrderBy(x => x, StringComparer.Ordinal));

        // Every public property is either compared or explicitly allowed; none is silently ignored.
        var all = typeof(DatabaseConnectionSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).ToHashSet();
        Assert.Equal(all, SavedPasswordPolicy.Classify().Select(c => c.Property.Name).ToHashSet());
        foreach (string safe in SavedPasswordPolicy.SafeToChange) Assert.Contains(safe, all);
    }

    private static DatabaseConnectionSettings Mutate(DatabaseConnectionSettings s, PropertyInfo p)
    {
        object? current = p.GetValue(s);
        object? next = current switch
        {
            string str => str + "x",
            null when p.PropertyType == typeof(string) => "x",
            null when p.PropertyType == typeof(int?) => 1,
            int i => i + 1,
            bool b => !b,
            DatabaseProvider => DatabaseProvider.MySql,
            DatabaseAuthentication a => a == DatabaseAuthentication.UserPassword ? DatabaseAuthentication.WindowsIntegrated : DatabaseAuthentication.UserPassword,
            ProviderExtras e => e.With("zzz", "1"),
            _ => throw new InvalidOperationException("No mutation known for " + p.Name + ": decide whether it belongs on the allowlist and teach this test."),
        };
        var copy = s with { };
        p.SetValue(copy, next);
        return copy;
    }

    [Fact]
    public void ALookAlikeHost_IsADifferentHost()
    {
        var a = Pg() with { Host = "db.example.com" };
        Assert.False(SavedPasswordPolicy.CanReuse(a, a with { Host = "db.example.com." }));
        Assert.False(SavedPasswordPolicy.CanReuse(a, a with { Host = "db.example.com@evil" }));
        Assert.False(SavedPasswordPolicy.CanReuse(a, a with { Host = "db.examp1e.com" }));
        Assert.True(SavedPasswordPolicy.CanReuse(a, a with { Host = "db.example.com" }));
    }

    /// <summary>In Oracle's alias mode the Database field is a TNS alias that picks the server, so editing it must ask for the password again.</summary>
    [Fact]
    public void ATnsAlias_IsADestination_SoChangingItDoesNotReuseThePassword()
    {
        var a = Pg() with { Database = "SALES", Extras = ProviderExtras.Of(("connectBy", "alias")) };
        Assert.False(SavedPasswordPolicy.CanReuse(a, a with { Database = "EVIL" }));
        Assert.True(SavedPasswordPolicy.CanReuse(a, a with { Name = "renamed" }));
        // A plain service name on the same host is still a free switch.
        var service = Pg() with { Database = "one" };
        Assert.True(SavedPasswordPolicy.CanReuse(service, service with { Database = "two" }));
    }

    [Fact]
    public void NullAndEmptyText_AreTheSameNotSet()
    {
        var a = Pg() with { CaCertificatePath = null, Dsn = "" };
        Assert.True(SavedPasswordPolicy.CanReuse(a, a with { CaCertificatePath = "", Dsn = null }));
    }

    [Fact]
    public void AnOdbcPasswordTypedIntoTheString_DoesNotCountAsAChange()
    {
        var saved = new DatabaseConnectionSettings { Name = "o", Provider = DatabaseProvider.Odbc, OdbcConnectionString = "Driver={X};Server=s" };
        Assert.True(SavedPasswordPolicy.CanReuse(saved, saved with { OdbcConnectionString = "Driver={X};Server=s;PWD=typed" }));
        Assert.False(SavedPasswordPolicy.CanReuse(saved, saved with { OdbcConnectionString = "Driver={X};Server=other;PWD=typed" }));
    }

    [Fact]
    public void PackProvidedProviders_AreComparedLikeTheBuiltInOnes()
    {
        var saved = new DatabaseConnectionSettings
        {
            Name = "ora", Provider = DatabaseProvider.Custom, ProviderId = "oracle", Host = "h", UserName = "u",
            Authentication = DatabaseAuthentication.UserPassword, Extras = ProviderExtras.Of(("serviceName", "FREE")),
        };
        Assert.True(SavedPasswordPolicy.CanReuse(saved, saved with { Database = "x", CommandTimeoutSeconds = 5 }));
        Assert.False(SavedPasswordPolicy.CanReuse(saved, saved with { ProviderId = "oracle2" }));
        Assert.False(SavedPasswordPolicy.CanReuse(saved, saved with { ProviderId = "other-pack-provider" }));
        Assert.False(SavedPasswordPolicy.CanReuse(saved, saved with { Extras = ProviderExtras.Of(("serviceName", "OTHER")) }));
        Assert.False(SavedPasswordPolicy.CanReuse(saved, saved with { Extras = saved.Extras.With("walletPath", "C:\\w") }));
        Assert.False(SavedPasswordPolicy.CanReuse(saved, saved with { Host = "evil" }));
        Assert.False(SavedPasswordPolicy.CanReuse(saved, saved with { TrustServerCertificate = true }));
        Assert.False(SavedPasswordPolicy.CanReuse(saved, saved with { Provider = DatabaseProvider.Odbc, ProviderId = null }));
    }

    // ---------------------------------------------------------------- the secret stays secret

    [Fact]
    public async Task AFailingConnection_NeverEchoesThePassword_InMessageDetailOrSettings()
    {
        // Port 1 on the local machine refuses at once; the driver's own message may name the host, never the password.
        var spy = new SpyProtector();
        var store = new JsonConnectionStore(FilePath, spy);
        var local = Pg() with { Host = "127.0.0.1", Port = 1, ConnectTimeoutSeconds = 3, Encrypt = false };
        await store.SaveAsync(local, Saved);

        foreach (string? typed in new string?[] { null, Typed })
        {
            var source = await store.OpenWithSettingsAsync(local with { Database = "d2" }, typed);
            var ex = await Assert.ThrowsAsync<DatabaseException>(() => source.TestConnectionAsync());
            string everything = ex + "|" + ex.Message + "|" + ex.Detail + "|" + string.Join("|", ex.Args) + "|" + source.Settings;
            Assert.DoesNotContain(Saved, everything);
            Assert.DoesNotContain(Typed, everything);
        }
        Assert.DoesNotContain(Saved, File.ReadAllText(FilePath));      // and the file never held it in clear
    }

    [Fact]
    public void ThePublicSurface_HasNoWayToReadAPasswordBack()
    {
        var publicMembers = typeof(IDatabaseSource).GetMembers().Concat(typeof(IConnectionStore).GetMembers()).Select(m => m.Name);
        Assert.DoesNotContain(publicMembers, n => n.Contains("Password", StringComparison.OrdinalIgnoreCase) && n.StartsWith("get", StringComparison.OrdinalIgnoreCase));
        foreach (var m in typeof(IConnectionStore).GetMethods())
            Assert.NotEqual(typeof(string), (m.ReturnType.IsGenericType ? m.ReturnType.GetGenericArguments()[0] : m.ReturnType));
        Assert.DoesNotContain(typeof(DatabaseConnectionSettings).GetProperties(), p => p.Name.Contains("Password", StringComparison.OrdinalIgnoreCase));
    }
}
