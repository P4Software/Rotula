using System.Runtime.Versioning;

namespace Rotula.Data;

/// <summary>What kind of "pick from a list" help a form option can offer next to its text box.</summary>
public enum OptionLookupKind
{
    /// <summary>A plain box (or the option's own control).</summary>
    None,
    /// <summary>The databases the login may access, read from the server (an explicit "Load list" action).</summary>
    DatabaseList,
    /// <summary>The ODBC data sources (DSN) installed on this computer, read from the registry.</summary>
    OdbcDataSource,
}

/// <summary>
/// Decides, from the provider descriptor alone, which options get a drop-down. The form asks this instead of testing provider names, so
/// a pack that implements <see cref="IDatabaseListingProvider"/> gets the same drop-down as the built-in servers.
/// </summary>
public static class OptionLookups
{
    /// <summary>The lookup that suits <paramref name="option"/> of <paramref name="descriptor"/>.</summary>
    public static OptionLookupKind For(IProviderDescriptor descriptor, ProviderOption option)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(option);
        if (option.Key == ProviderOptionKeys.Database && option.Kind == ProviderOptionKind.Text
            && (descriptor is IDatabaseListingProvider || descriptor.Id is ProviderIds.SqlServer or ProviderIds.PostgreSql or ProviderIds.MySql))
            return OptionLookupKind.DatabaseList;
        if (option.Key == ProviderOptionKeys.Dsn && descriptor.Id == ProviderIds.Odbc) return OptionLookupKind.OdbcDataSource;
        return OptionLookupKind.None;
    }
}

/// <summary>One ODBC data source name found on this computer.</summary>
/// <param name="Name">The DSN as the ODBC administrator shows it.</param>
/// <param name="Driver">The driver the DSN uses (may be empty).</param>
/// <param name="IsSystem">True for a system DSN, false for a user DSN.</param>
public sealed record OdbcDataSourceInfo(string Name, string Driver, bool IsSystem);

/// <summary>Lists the ODBC data sources visible to this process (best effort, read-only registry access, no connection is made).</summary>
public static class OdbcDataSourceCatalogue
{
    private const string Path = @"SOFTWARE\ODBC\ODBC.INI\ODBC Data Sources";

    /// <summary>
    /// User and system DSNs, user ones first, sorted by name. The registry view is the one of the running process (Rotula is 64-bit, and
    /// the ODBC driver manager it uses only sees 64-bit data sources), so 32-bit-only DSNs are deliberately not offered: they would fail.
    /// Empty when they cannot be read or the OS has no ODBC registry.
    /// </summary>
    public static IReadOnlyList<OdbcDataSourceInfo> GetDataSources()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<OdbcDataSourceInfo>();
        var list = new List<OdbcDataSourceInfo>();
        Read(Microsoft.Win32.Registry.CurrentUser, false, list);
        Read(Microsoft.Win32.Registry.LocalMachine, true, list);
        return list;
    }

    [SupportedOSPlatform("windows")]
    private static void Read(Microsoft.Win32.RegistryKey hive, bool system, List<OdbcDataSourceInfo> into)
    {
        try
        {
            using var key = hive.OpenSubKey(Path);
            if (key is null) return;
            var found = new List<OdbcDataSourceInfo>();
            foreach (string name in key.GetValueNames())
            {
                // A DSN with a separator in its name could not be written into a connection string safely; Rotula refuses it, so do not offer it.
                if (name.Length == 0 || name.Length > 256 || name.Any(char.IsControl) || name.IndexOfAny(new[] { ';', '=', '{', '}' }) >= 0) continue;
                if (into.Any(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
                found.Add(new OdbcDataSourceInfo(name, key.GetValue(name) as string ?? string.Empty, system));
            }
            into.AddRange(found.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // An unreadable hive just means fewer suggestions; typing a DSN still works.
        }
    }
}
