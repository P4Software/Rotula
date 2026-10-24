using Microsoft.Win32;

namespace Rotula.Files.Policy;

/// <summary>One level of policy values: a registry key in production, a dictionary in tests.</summary>
public interface IPolicySource
{
    /// <summary>The text value <paramref name="name"/>, or null when it is not set (or is not text).</summary>
    string? GetString(string name);

    /// <summary>The whole-number value <paramref name="name"/>, or null when it is not set (or is not a number).</summary>
    int? GetInt(string name);
}

/// <summary>A source with no values: no policy is set.</summary>
public sealed class EmptyPolicySource : IPolicySource
{
    /// <summary>The shared instance.</summary>
    public static readonly EmptyPolicySource Instance = new();

    private EmptyPolicySource() { }

    /// <inheritdoc />
    public string? GetString(string name) => null;

    /// <inheritdoc />
    public int? GetInt(string name) => null;
}

/// <summary>
/// Two sources read value by value: a value set for the computer wins over the same value set for the user, and a value the
/// computer does not set falls back to the user's. This is how Windows itself treats HKLM and HKCU policies.
/// </summary>
public sealed class LayeredPolicySource : IPolicySource
{
    private readonly IPolicySource _machine;
    private readonly IPolicySource _user;

    /// <summary>Creates the layered source.</summary>
    /// <param name="machine">The computer-wide values (HKLM); they win.</param>
    /// <param name="user">The per-user values (HKCU).</param>
    public LayeredPolicySource(IPolicySource machine, IPolicySource user)
    {
        _machine = machine ?? throw new ArgumentNullException(nameof(machine));
        _user = user ?? throw new ArgumentNullException(nameof(user));
    }

    /// <inheritdoc />
    public string? GetString(string name) => _machine.GetString(name) ?? _user.GetString(name);

    /// <inheritdoc />
    public int? GetInt(string name) => _machine.GetInt(name) ?? _user.GetInt(name);
}

/// <summary>Where a policy value is set.</summary>
public enum PolicyScope
{
    /// <summary>For every user of the computer (HKEY_LOCAL_MACHINE).</summary>
    Computer,
    /// <summary>For the signed-in user (HKEY_CURRENT_USER).</summary>
    User,
}

/// <summary>
/// Reads <see cref="PolicyNames.KeyPath"/> under one registry hive. Read-only; a missing key, a value of the wrong type or a
/// key the user may not read all mean "not set", never an error. Off Windows every value is "not set".
/// </summary>
public sealed class RegistryPolicySource : IPolicySource
{
    private readonly PolicyScope _scope;

    /// <summary>Creates a source for the computer-wide (HKLM) or the per-user (HKCU) policies.</summary>
    public RegistryPolicySource(PolicyScope scope) => _scope = scope;

    /// <summary>The policies of this computer: HKLM first, then HKCU, value by value.</summary>
    public static IPolicySource ForThisComputer() =>
        new LayeredPolicySource(new RegistryPolicySource(PolicyScope.Computer), new RegistryPolicySource(PolicyScope.User));

    /// <inheritdoc />
    public string? GetString(string name) => Read(name) as string;

    /// <inheritdoc />
    public int? GetInt(string name) => Read(name) switch
    {
        int i => i,
        // A QWORD written by a script instead of the template: accept it when it fits.
        long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
        _ => null,
    };

    private object? Read(string name)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            // Registry64 on purpose: the templates write to the 64-bit view, and a policy must not depend on the process bitness.
            var hive = _scope == PolicyScope.Computer ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = root.OpenSubKey(PolicyNames.KeyPath, writable: false);
            return key?.GetValue(name);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
