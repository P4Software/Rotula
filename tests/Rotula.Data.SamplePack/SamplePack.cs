using System.Data.Common;
using System.Runtime.InteropServices;
using Rotula.Data;
using Rotula.Data.SamplePack.Dep;

namespace Rotula.Data.SamplePack;

/// <summary>The sample provider: no real database, just enough surface for the loader tests.</summary>
public sealed class SampleDescriptor : IProviderDescriptor, IProviderErrorMapper
{
    /// <summary>Provider id.</summary>
    public string Id => "sample-memory";
    /// <inheritdoc />
    public string DisplayNameKey => "Sample.Provider";
    /// <summary>Proves the pack's own managed dependency was resolved.</summary>
    public string DisplayName => "Sample (" + Greeter.Text + ")";
    /// <inheritdoc />
    public ProviderCategory Category => ProviderCategory.File;
    /// <inheritdoc />
    public int? DefaultPort => null;
    /// <inheritdoc />
    public IReadOnlyList<DatabaseAuthentication> SupportedAuthentication { get; } = Array.Empty<DatabaseAuthentication>();
    /// <inheritdoc />
    public IReadOnlyList<ProviderOption> Options { get; } = new[] { StandardOptions.FilePath() };
    /// <inheritdoc />
    public bool RequiresServer => false;
    /// <inheritdoc />
    public SqlDialect Dialect => SqlDialects.Sqlite;
    /// <inheritdoc />
    public ReadOnlyPolicy ReadOnly { get; } = new() { StartTransaction = false, Description = "Sample." };
    /// <inheritdoc />
    public IProviderErrorMapper Errors => this;
    /// <inheritdoc />
    public (int? Number, string? SqlState) Extract(Exception ex) => (null, null);
    /// <inheritdoc />
    public string? ClassifyNumber(int? number, bool connecting) => null;
    /// <inheritdoc />
    public void Validate(DatabaseConnectionSettings settings) => ProviderValidation.RequireText(settings.FilePath, nameof(settings.FilePath), 260);
    /// <inheritdoc />
    public string BuildConnectionString(DatabaseConnectionSettings settings, string? password) => "Data Source=" + settings.FilePath;
    /// <inheritdoc />
    public DbConnection CreateConnection(string connectionString, DatabaseConnectionSettings settings) => throw new NotSupportedException();

    [DllImport("e_sqlite3", EntryPoint = "sqlite3_libversion_number")]
    private static extern int LibVersionNumber();

    /// <summary>Calls into the pack's own native library; the loader must find it inside the pack folder.</summary>
    public static int NativeVersionNumber() => LibVersionNumber();
}

/// <summary>The entry class named in pack.json.</summary>
public sealed class SamplePack : IDatabaseProviderPack
{
    /// <inheritdoc />
    public string PackId => "sample";
    /// <inheritdoc />
    public IReadOnlyList<IProviderDescriptor> GetProviders() => new IProviderDescriptor[] { new SampleDescriptor() };
}

/// <summary>Fixture: a pack whose code throws while starting (must become a warning, never a crash).</summary>
public sealed class ThrowingPack : IDatabaseProviderPack
{
    /// <inheritdoc />
    public string PackId => "sample";
    /// <inheritdoc />
    public IReadOnlyList<IProviderDescriptor> GetProviders() => throw new InvalidOperationException("boom from pack code");
}

/// <summary>Fixture: a pack that tries to register a provider id already used by Rotula.</summary>
public sealed class ClashingPack : IDatabaseProviderPack
{
    /// <inheritdoc />
    public string PackId => "sample";
    /// <inheritdoc />
    public IReadOnlyList<IProviderDescriptor> GetProviders() => new IProviderDescriptor[] { new ClashDescriptor() };

    private sealed class ClashDescriptor : IProviderDescriptor, IProviderErrorMapper
    {
        public string Id => "sqlserver";
        public string DisplayNameKey => "x";
        public string DisplayName => "Hijack";
        public ProviderCategory Category => ProviderCategory.Server;
        public int? DefaultPort => null;
        public IReadOnlyList<DatabaseAuthentication> SupportedAuthentication { get; } = Array.Empty<DatabaseAuthentication>();
        public IReadOnlyList<ProviderOption> Options { get; } = Array.Empty<ProviderOption>();
        public bool RequiresServer => false;
        public SqlDialect Dialect => SqlDialects.Sqlite;
        public ReadOnlyPolicy ReadOnly { get; } = new();
        public IProviderErrorMapper Errors => this;
        public (int? Number, string? SqlState) Extract(Exception ex) => (null, null);
        public string? ClassifyNumber(int? number, bool connecting) => null;
        public void Validate(DatabaseConnectionSettings settings) { }
        public string BuildConnectionString(DatabaseConnectionSettings settings, string? password) => string.Empty;
        public DbConnection CreateConnection(string connectionString, DatabaseConnectionSettings settings) => throw new NotSupportedException();
    }
}
