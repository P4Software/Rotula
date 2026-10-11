using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data.Odbc;
using System.Globalization;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rotula.App.Localization;
using Rotula.App.Services;
using Rotula.Data;

namespace Rotula.App.ViewModels;

/// <summary>
/// The connection manager: list, add, edit, delete and test the database connections saved on this computer. The engine list and the
/// form are data-driven: they come from <see cref="ProviderRegistry"/> (the five built-in engines, the presets over them, and whatever
/// add-ons registered) and from each provider's option schema, so a new engine needs no change here. Passwords go only through
/// <see cref="IConnectionStore"/> (Windows-protected) and are never shown again; an empty password box keeps the saved one. Nothing here
/// is ever written to a .rotula file.
/// </summary>
public sealed partial class ConnectionManagerViewModel : ObservableObject
{
    private readonly IDatabaseServices _db;
    private readonly IMessageService _messages;
    private readonly IFileDialogService _files;
    private readonly ProviderRegistry _registry;
    private readonly Func<IReadOnlyCollection<string>> _installedDrivers;
    private CancellationTokenSource? _cts;
    private bool _loading;
    private bool _syncingEngine;
    private SavedConnection? _editing;
    private Dictionary<string, string> _extras = new(StringComparer.OrdinalIgnoreCase);
    private IProviderDescriptor? _descriptor;
    private ProviderPreset? _preset;

    /// <summary>Creates the manager.</summary>
    /// <param name="db">Saved connections and source factory.</param>
    /// <param name="messages">Message boxes.</param>
    /// <param name="files">File and folder pickers.</param>
    /// <param name="registry">The engines on offer; defaults to <see cref="ProviderRegistry.Default"/>.</param>
    /// <param name="installedDrivers">Lists the ODBC drivers installed on this computer (for the "driver missing" hint); defaults to the registry lookup.</param>
    public ConnectionManagerViewModel(IDatabaseServices db, IMessageService messages, IFileDialogService files, ProviderRegistry? registry = null,
        Func<IReadOnlyCollection<string>>? installedDrivers = null)
    {
        _db = db;
        _messages = messages;
        _files = files;
        _registry = registry ?? ProviderRegistry.Default;
        _installedDrivers = installedDrivers ?? (() => OdbcDriverCatalogue.GetInstalledDriverNames());
        AuthChoices = new[]
        {
            new Choice<DatabaseAuthentication>(DatabaseAuthentication.WindowsIntegrated, "Conn.Auth.Windows"),
            new Choice<DatabaseAuthentication>(DatabaseAuthentication.UserPassword, "Conn.Auth.UserPassword"),
        };
        BuildEngines();
        _provider = DatabaseProvider.SqlServer;
        _selectedEngine = Engines.FirstOrDefault(e => e.Id == ProviderIds.SqlServer);
        ApplyEngine(_selectedEngine, reset: true);
        Loc.Instance.LanguageChanged += (_, _) => RefreshLanguage();
    }

    /// <summary>Raised when the window should close.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Raised when the password box must be emptied (a different connection was selected, or it was saved).</summary>
    public event EventHandler? PasswordCleared;

    /// <summary>Raised when the password box should take the keyboard focus (a test needs the password typed again).</summary>
    public event EventHandler? PasswordFocusRequested;

    /// <summary>Name of the connection saved last (the caller selects it afterwards), or null.</summary>
    public string? LastSavedName { get; private set; }

    /// <summary>Sign-in methods.</summary>
    public IReadOnlyList<Choice<DatabaseAuthentication>> AuthChoices { get; }

    // ----------------------------------------------------------------------------------- engines

    /// <summary>Every engine on offer: each provider followed by its presets, in the order of the registry.</summary>
    public IReadOnlyList<EngineEntry> Engines { get; private set; } = Array.Empty<EngineEntry>();

    /// <summary>The engine list grouped by category, for the drop-down.</summary>
    public ICollectionView EngineView { get; private set; } = CollectionViewSource.GetDefaultView(Array.Empty<EngineEntry>());

    /// <summary>The chosen engine or preset. Choosing a preset fills in its port, secure defaults and options.</summary>
    [ObservableProperty]
    private EngineEntry? _selectedEngine;

    private void BuildEngines()
    {
        var entries = _registry.GetDisplayEntries();
        var list = new List<EngineEntry>();
        // Presets are listed under their provider (the registry lists all providers first, then all presets), and providers are
        // grouped by category in a fixed order, so the five original engines keep their positions.
        var providers = entries.Where(e => e.Kind == ProviderEntryKind.Provider).ToList();
        foreach (var group in providers.GroupBy(p => p.Category).OrderBy(g => g.Key))
            foreach (var p in group)
            {
                list.Add(new EngineEntry(p, p.Category));
                foreach (var preset in entries.Where(e => e.Kind == ProviderEntryKind.Preset && string.Equals(e.ProviderId, p.Id, StringComparison.OrdinalIgnoreCase)))
                    list.Add(new EngineEntry(preset, p.Category));
            }
        Engines = list;
        var view = new ListCollectionView(list);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(EngineEntry.GroupName)));
        EngineView = view;
        OnPropertyChanged(nameof(Engines));
        OnPropertyChanged(nameof(EngineView));
    }

    partial void OnSelectedEngineChanged(EngineEntry? value)
    {
        if (_loading || _syncingEngine || value is null) return;
        ApplyEngine(value, reset: true);
        ClearTestResult();
    }

    /// <summary>
    /// Makes <paramref name="engine"/> the current engine: finds its descriptor and preset, rebuilds the form, and (for a fresh choice)
    /// applies the engine's usual sign-in method and the preset's port, secure defaults and options.
    /// </summary>
    private void ApplyEngine(EngineEntry? engine, bool reset)
    {
        if (engine is null) return;
        _preset = engine.IsPreset ? _registry.FindPreset(engine.Id) : null;
        _descriptor = _registry.TryGet(engine.ProviderId);
        _syncingEngine = true;
        try
        {
            ProviderId = engine.ProviderId;
            PresetId = _preset?.Id;
            Provider = ProviderIds.ToEnum(engine.ProviderId);
        }
        finally { _syncingEngine = false; }
        IsPackMissing = _descriptor is null;
        MissingPackText = string.Empty;

        if (reset)
        {
            _extras = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            CaCertificatePath = string.Empty;
            Encrypt = true;
            TrustServerCertificate = false;
            if (_preset is not null)
            {
                foreach (var kv in _preset.Extras) _extras[kv.Key] = kv.Value;
                Encrypt = _preset.Encrypt;
                TrustServerCertificate = _preset.TrustServerCertificate;
                PortText = _preset.DefaultPort?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            }
            else PortText = string.Empty;
            Authentication = _descriptor?.SupportedAuthentication.FirstOrDefault() ?? DatabaseAuthentication.WindowsIntegrated;
        }
        RebuildFields();
        RebuildHints();
        NotifyEngineProperties();
    }

    private void NotifyEngineProperties()
    {
        OnPropertyChanged(nameof(IsServer));
        OnPropertyChanged(nameof(IsSqlite));
        OnPropertyChanged(nameof(IsOdbc));
        OnPropertyChanged(nameof(CanWindowsAuth));
        OnPropertyChanged(nameof(ShowUserFields));
        OnPropertyChanged(nameof(ShowAuth));
        OnPropertyChanged(nameof(ShowAuthChoice));
        OnPropertyChanged(nameof(PortHint));
        OnPropertyChanged(nameof(ShowEncryptWarning));
        OnPropertyChanged(nameof(ShowTrustWarning));
        OnPropertyChanged(nameof(IsFormAvailable));
        OnPropertyChanged(nameof(DetectedPresetText));
        OnPropertyChanged(nameof(HasDetectedPreset));
        SaveCommand.NotifyCanExecuteChanged();
        TestCommand.NotifyCanExecuteChanged();
    }

    // ----------------------------------------------------------------------------------- the data-driven fields

    /// <summary>Fields shown directly in the form.</summary>
    public ObservableCollection<OptionFieldViewModel> Fields { get; } = new();

    /// <summary>Yes/no switches that are not advanced (encrypt the connection); shown after the sign-in block, under "Security".</summary>
    public ObservableCollection<OptionFieldViewModel> SwitchFields { get; } = new();

    /// <summary>True when there are switches to show.</summary>
    public bool HasSwitchFields => SwitchFields.Count > 0;

    /// <summary>Fields hidden behind "More options".</summary>
    public ObservableCollection<OptionFieldViewModel> AdvancedFields { get; } = new();

    /// <summary>True when there are fields behind "More options".</summary>
    public bool HasAdvancedFields => AdvancedFields.Count > 0;

    /// <summary>Hints under the form: the preset's hints, the read-only account advice, a missing ODBC driver.</summary>
    public ObservableCollection<FormHint> Hints { get; } = new();

    /// <summary>True when there is at least one hint.</summary>
    public bool HasHints => Hints.Count > 0;

    private void RebuildFields()
    {
        Fields.Clear();
        SwitchFields.Clear();
        AdvancedFields.Clear();
        if (_descriptor is not null)
        {
            if (_preset?.Odbc is { } odbc)
            {
                // A file format served by an ODBC driver (Access, Excel, text): one path, the driver and its read-only switches come from the preset.
                bool folder = odbc.PathKind == "folder";
                var option = new ProviderOption(ProviderOptionKeys.FilePath, folder ? "Conn.Preset.Folder" : "Conn.Preset.File", ProviderOptionKind.FilePath)
                    { Label = folder ? "Folder" : "File", Required = true };
                Fields.Add(new OptionFieldViewModel(option, GetOptionValue, SetOptionValue, Browse, advanced: false)
                    { IsFolder = folder, LabelKeyOverride = folder ? "Conn.Preset.Folder" : "Conn.Preset.File" });
            }
            else
            {
                foreach (var option in _descriptor.Options)
                {
                    // The user name belongs to the sign-in block (it is only needed for a user and password).
                    if (option.Key == ProviderOptionKeys.UserName) continue;
                    // Port is a short box next to the address, as it always was, even though the schema marks it as advanced.
                    // The certificate switch stays with the other security switches, so its warning is never hidden in a collapsed section.
                    bool advanced = option.Advanced && option.Key != ProviderOptionKeys.Port && option.Key != ProviderOptionKeys.TrustServerCertificate;
                    var field = new OptionFieldViewModel(option, GetOptionValue, SetOptionValue,
                        option.Kind == ProviderOptionKind.FilePath ? Browse : null, advanced, option.Key == ProviderOptionKeys.Port ? PortHint : string.Empty)
                    {
                        // The preset says what the addresses of its service look like (its host patterns).
                        HostPatterns = option.Key == ProviderOptionKeys.Host && _preset is { HostPatterns.Count: > 0 } hp ? hp.HostPatterns.Take(2).ToArray() : Array.Empty<string>(),
                    };
                    (advanced ? AdvancedFields : field.IsBool ? SwitchFields : Fields).Add(field);
                }
            }
        }
        OnPropertyChanged(nameof(HasAdvancedFields));
        OnPropertyChanged(nameof(HasSwitchFields));
    }

    private void RebuildHints()
    {
        Hints.Clear();
        if (_preset is { } p)
        {
            foreach (string k in p.HintKeys) Hints.Add(new FormHint(k, k == ProviderKeys.HintEntraOnly));
            if (!p.Verified) Hints.Add(new FormHint("Conn.Preset.Unverified", true));
            if (p.Odbc is { } odbc && p.DriverMissingHintKey is { } missing)
            {
                var installed = _installedDrivers();
                if (!odbc.DriverNames.Any(d => installed.Contains(d, StringComparer.OrdinalIgnoreCase))) Hints.Add(new FormHint(missing, true));
            }
        }
        // A server that cannot make a session read-only needs a read-only account: say so once, whichever engine it is.
        if (_descriptor is { ReadOnly.GuardIsPrimaryControl: true } && Hints.All(h => h.Key != ProviderKeys.HintReadOnlyAccount))
            Hints.Add(new FormHint(ProviderKeys.HintReadOnlyAccount, false));
        OnPropertyChanged(nameof(HasHints));
    }

    private string GetOptionValue(string key)
    {
        switch (key.ToLowerInvariant())
        {
            case "host": return Host;
            case "port": return PortText;
            case "database": return Database;
            case "username": return UserName;
            case "filepath": return FilePath;
            case "cacertificatepath": return CaCertificatePath;
            case "dsn": return Dsn;
            case "odbcconnectionstring": return OdbcConnectionString;
            case "encrypt": return Encrypt ? "true" : "false";
            case "trustservercertificate": return TrustServerCertificate ? "true" : "false";
        }
        return _extras.TryGetValue(key, out var v) ? v : string.Empty;
    }

    private void SetOptionValue(string key, string value)
    {
        bool On() => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        switch (key.ToLowerInvariant())
        {
            case "host": Host = value; return;
            case "port": PortText = value; return;
            case "database": Database = value; return;
            case "username": UserName = value; return;
            case "filepath": FilePath = value; return;
            case "cacertificatepath": CaCertificatePath = value; return;
            case "dsn": Dsn = value; return;
            case "odbcconnectionstring": OdbcConnectionString = value; return;
            case "encrypt": Encrypt = On(); return;
            case "trustservercertificate": TrustServerCertificate = On(); return;
        }
        if (value.Length == 0 || value == "false") _extras.Remove(key); else _extras[key] = value;
    }

    private bool HasOption(string key) => _descriptor is not null && _descriptor.Options.Any(o => o.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    private void Browse(OptionFieldViewModel field)
    {
        string? picked;
        if (field.IsFolder) picked = _files.PickFolder(Loc.Get("Conn.BrowseFolder"));
        else if (field.Key == ProviderOptionKeys.CaCertificatePath) picked = _files.PickOpenFile(field.Label, new[] { ".pem", ".crt", ".cer" });
        else if (_preset?.Odbc is { FileExtensions.Count: > 0 } odbc) picked = _files.PickOpenFile(field.Label, odbc.FileExtensions);
        else picked = _files.PickOpenDatabaseFile();
        if (picked is not null) field.Text = picked;
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        // A value changed behind the fields' back (a saved connection was loaded, an engine applied a default): show it.
        if (e.PropertyName is nameof(Host) or nameof(PortText) or nameof(Database) or nameof(UserName) or nameof(FilePath)
            or nameof(CaCertificatePath) or nameof(Dsn) or nameof(OdbcConnectionString) or nameof(Encrypt) or nameof(TrustServerCertificate))
        {
            foreach (var f in Fields) f.Refresh();
            foreach (var f in SwitchFields) f.Refresh();
            foreach (var f in AdvancedFields) f.Refresh();
        }
    }

    // ----------------------------------------------------------------------------------- list

    /// <summary>The saved connections.</summary>
    public ObservableCollection<ConnectionChoice> Items { get; } = new();

    /// <summary>True when nothing is saved yet.</summary>
    public bool HasNoItems => Items.Count == 0;

    /// <summary>The connection being edited, or null for a new one.</summary>
    [ObservableProperty]
    private ConnectionChoice? _selectedItem;

    partial void OnSelectedItemChanged(ConnectionChoice? value)
    {
        if (_loading || value is null) return;
        Fill(value.Saved);
    }

    /// <summary>Reads the saved connections and selects <paramref name="select"/> (or starts a new one when there are none).</summary>
    public async Task LoadAsync(string? select = null)
    {
        // Add-ons load in the background at start-up: wait for them, otherwise a connection of an add-on would look "missing" for a moment.
        await PackStartup.Ready.ConfigureAwait(true);
        if (_registry.GetDisplayEntries().Count != Engines.Count)
        {
            string? keep = SelectedEngine?.Id;
            BuildEngines();
            _syncingEngine = true;
            try { SelectedEngine = Engines.FirstOrDefault(e => e.Id == keep) ?? Engines.FirstOrDefault(); }
            finally { _syncingEngine = false; }
        }
        _loading = true;
        try
        {
            ConnectionStoreContent content;
            try { content = await _db.Connections.LoadAsync(); }
            catch (DatabaseException ex) { SetFormError(DataMessages.Describe(ex)); return; }
            Items.Clear();
            foreach (var c in content.Connections.OrderBy(c => c.Settings.Name, StringComparer.CurrentCultureIgnoreCase)) Items.Add(new ConnectionChoice(c));
            OnPropertyChanged(nameof(HasNoItems));
            if (content.ErrorKey is not null) SetFormError(DataMessages.DescribeStore(content.ErrorKey));
            var pick = select is null ? null : Items.FirstOrDefault(i => string.Equals(i.Name, select, StringComparison.OrdinalIgnoreCase));
            pick ??= Items.FirstOrDefault();
            SelectedItem = pick;
            if (pick is not null) Fill(pick.Saved);
            else StartNew();
        }
        finally { _loading = false; }
    }

    // ----------------------------------------------------------------------------------- form

    /// <summary>True while the form describes a connection that is not saved yet.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditName), nameof(CanDelete))]
    private bool _isNew = true;

    /// <summary>The name is fixed once saved: labels refer to it.</summary>
    public bool CanEditName => IsNew;

    /// <summary>True when the selected connection can be deleted.</summary>
    public bool CanDelete => !IsNew && !IsBusy;

    /// <summary>Name labels use to refer to this connection.</summary>
    [ObservableProperty]
    private string _name = string.Empty;

    /// <summary>The engine (a built-in value, or <see cref="DatabaseProvider.Custom"/> for an add-on engine, in which case <see cref="ProviderId"/> says which).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsServer), nameof(IsSqlite), nameof(IsOdbc), nameof(CanWindowsAuth), nameof(ShowUserFields), nameof(ShowAuth), nameof(PortHint),
        nameof(ShowEncryptWarning), nameof(ShowTrustWarning))]
    private DatabaseProvider _provider;

    partial void OnProviderChanged(DatabaseProvider value)
    {
        if (_loading || _syncingEngine) return;
        // Setting the engine from code (or a test) selects its row in the list; choosing it applies its defaults.
        var row = Engines.FirstOrDefault(e => !e.IsPreset && e.Id == ProviderIds.For(value));
        if (row is not null && !ReferenceEquals(row, SelectedEngine)) SelectedEngine = row;
        else if (row is not null) { ApplyEngine(row, reset: true); ClearTestResult(); }
    }

    /// <summary>Id of the provider (<c>sqlserver</c>, <c>oracle</c>...). For the five built-in engines it matches <see cref="Provider"/>.</summary>
    [ObservableProperty]
    private string _providerId = ProviderIds.SqlServer;

    /// <summary>Id of the chosen preset, or null.</summary>
    [ObservableProperty]
    private string? _presetId;

    /// <summary>True when this saved connection belongs to an add-on that is not installed. The form is read-only then and Save is off.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFormAvailable))]
    private bool _isPackMissing;

    /// <summary>Explains that the add-on of the selected connection is not installed.</summary>
    [ObservableProperty]
    private string _missingPackText = string.Empty;

    /// <summary>True when the form can be edited (the engine is available).</summary>
    public bool IsFormAvailable => !IsPackMissing;

    /// <summary>Sign-in method.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUserFields))]
    private DatabaseAuthentication _authentication = DatabaseAuthentication.WindowsIntegrated;

    /// <summary>Server host.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetectedPresetText), nameof(HasDetectedPreset))]
    private string _host = string.Empty;

    /// <summary>Port as typed (empty = the engine's usual port).</summary>
    [ObservableProperty]
    private string _portText = string.Empty;

    /// <summary>Database name.</summary>
    [ObservableProperty]
    private string _database = string.Empty;

    /// <summary>User name.</summary>
    [ObservableProperty]
    private string _userName = string.Empty;

    /// <summary>The password as typed; never read back from the store.</summary>
    [ObservableProperty]
    private string _password = string.Empty;

    /// <summary>True when the store already holds a password for the selected connection.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PasswordHint))]
    private bool _hasSavedPassword;

    /// <summary>Tells the user what an empty password box does.</summary>
    public string PasswordHint => HasSavedPassword ? Loc.Get("Conn.Password.Saved") : Loc.Get("Conn.Password.None");

    /// <summary>Require an encrypted connection.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEncryptWarning))]
    private bool _encrypt = true;

    /// <summary>Accept a certificate that cannot be verified.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTrustWarning))]
    private bool _trustServerCertificate;

    /// <summary>Certificate authority file (PEM) for servers with a private certificate chain.</summary>
    [ObservableProperty]
    private string _caCertificatePath = string.Empty;

    /// <summary>SQLite file, or the file or folder of an ODBC file preset.</summary>
    [ObservableProperty]
    private string _filePath = string.Empty;

    /// <summary>ODBC data source name.</summary>
    [ObservableProperty]
    private string _dsn = string.Empty;

    /// <summary>ODBC connection string (a password in it is moved into the protected store).</summary>
    [ObservableProperty]
    private string _odbcConnectionString = string.Empty;

    /// <summary>Server-style built-in engines (SQL Server, PostgreSQL, MySQL/MariaDB).</summary>
    public bool IsServer => Provider is DatabaseProvider.SqlServer or DatabaseProvider.PostgreSql or DatabaseProvider.MySql;

    /// <summary>SQLite file.</summary>
    public bool IsSqlite => Provider == DatabaseProvider.Sqlite;

    /// <summary>ODBC.</summary>
    public bool IsOdbc => Provider == DatabaseProvider.Odbc;

    /// <summary>True where the engine can use the current Windows account.</summary>
    public bool CanWindowsAuth => _descriptor?.SupportedAuthentication.Contains(DatabaseAuthentication.WindowsIntegrated) == true;

    private bool CanUserPassword => _descriptor?.SupportedAuthentication.Contains(DatabaseAuthentication.UserPassword) == true;

    /// <summary>True when the sign-in choice is shown (not for a file engine, which has no accounts).</summary>
    public bool ShowAuth => _descriptor is { SupportedAuthentication.Count: > 0 } && _preset?.Odbc is null;

    /// <summary>True when user name and password boxes are shown.</summary>
    public bool ShowUserFields => ShowAuth && CanUserPassword && (Authentication == DatabaseAuthentication.UserPassword || !CanWindowsAuth);

    /// <summary>True when the choice between Windows and user/password is offered (the engine supports both).</summary>
    public bool ShowAuthChoice => ShowAuth && CanWindowsAuth && CanUserPassword;

    /// <summary>The usual port of the chosen engine or preset, for the empty box.</summary>
    public string PortHint => (_preset?.DefaultPort ?? _descriptor?.DefaultPort)?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>Amber warning shown while encryption is off.</summary>
    public bool ShowEncryptWarning => HasOption(ProviderOptionKeys.Encrypt) && !Encrypt;

    /// <summary>Amber warning shown while an unverified certificate is accepted.</summary>
    public bool ShowTrustWarning => HasOption(ProviderOptionKeys.TrustServerCertificate) && TrustServerCertificate;

    partial void OnEncryptChanged(bool value) => OnPropertyChanged(nameof(ShowEncryptWarning));

    /// <summary>The preset that the typed host looks like, when the user chose a plain engine (Supabase recognised from its address).</summary>
    private ProviderPreset? DetectedPreset
    {
        get
        {
            if (_preset is not null || _descriptor is null || string.IsNullOrWhiteSpace(Host)) return null;
            int? port = int.TryParse(PortText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int p) ? p : null;
            return ProviderPresets.Detect(Host, port, _registry.Presets).FirstOrDefault(x => string.Equals(x.ProviderId, _descriptor.Id, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Sentence offering the preset that matches the typed address, or empty.</summary>
    public string DetectedPresetText => DetectedPreset is { } d ? Loc.Get("Conn.Preset.Detected", Loc.Instance.HasKey(d.NameKey) ? Loc.Get(d.NameKey) : d.Name) : string.Empty;

    /// <summary>True when a preset matches the typed address.</summary>
    public bool HasDetectedPreset => DetectedPreset is not null;

    /// <summary>Switches to the preset that matches the typed address, keeping what was typed.</summary>
    [RelayCommand]
    private void UseDetectedPreset()
    {
        if (DetectedPreset is not { } d) return;
        var row = Engines.FirstOrDefault(e => e.IsPreset && e.Id == d.Id);
        if (row is null) return;
        string host = Host, database = Database, user = UserName;
        SelectedEngine = row;
        Host = host;
        Database = database;
        UserName = user;
    }

    /// <summary>Starts a blank form.</summary>
    [RelayCommand]
    private void NewConnection() => StartNew();

    private void StartNew()
    {
        bool was = _loading;
        _loading = true;
        try
        {
            SelectedItem = null;
            _editing = null;
            IsNew = true;
            Name = string.Empty;
            Host = Database = UserName = Password = FilePath = Dsn = OdbcConnectionString = CaCertificatePath = string.Empty;
            HasSavedPassword = false;
            SelectedEngine = Engines.FirstOrDefault(e => e.Id == ProviderIds.SqlServer) ?? Engines.FirstOrDefault();
            ApplyEngine(SelectedEngine, reset: true);
        }
        finally { _loading = was; }
        OnPropertyChanged(nameof(ShowUserFields));
        ClearTestResult();
        SetFormError(string.Empty);
        PasswordCleared?.Invoke(this, EventArgs.Empty);
    }

    private void Fill(SavedConnection saved)
    {
        bool was = _loading;
        _loading = true;
        try
        {
            var s = saved.Settings;
            _editing = saved;
            IsNew = false;
            Name = s.Name;
            string effective = s.GetEffectiveProviderId();
            // The row to show: the preset the connection was made from, else its provider. A connection of an add-on that is not
            // installed has neither; it is shown as such and left untouched (never coerced into a built-in engine).
            var row = (s.PresetId is { } pid ? Engines.FirstOrDefault(e => e.IsPreset && e.Id.Equals(pid, StringComparison.OrdinalIgnoreCase) && e.ProviderId.Equals(effective, StringComparison.OrdinalIgnoreCase)) : null)
                      ?? Engines.FirstOrDefault(e => !e.IsPreset && e.Id.Equals(effective, StringComparison.OrdinalIgnoreCase));
            _extras = new Dictionary<string, string>(s.Extras, StringComparer.OrdinalIgnoreCase);
            if (row is null)
            {
                _descriptor = null;
                _preset = null;
                SelectedEngine = null;
                ProviderId = effective;
                PresetId = s.PresetId;
                Provider = s.Provider;
                IsPackMissing = true;
                MissingPackText = Loc.Get("Conn.PackMissing", effective);
                Fields.Clear();
                SwitchFields.Clear();
                AdvancedFields.Clear();
                Hints.Clear();
                OnPropertyChanged(nameof(HasAdvancedFields));
                OnPropertyChanged(nameof(HasHints));
            }
            else
            {
                SelectedEngine = row;
                ApplyEngine(row, reset: false);
                _extras = new Dictionary<string, string>(s.Extras, StringComparer.OrdinalIgnoreCase);
            }
            Authentication = s.Authentication;
            Host = s.Host ?? string.Empty;
            PortText = s.Port?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            Database = s.Database ?? string.Empty;
            UserName = s.UserName ?? string.Empty;
            Password = string.Empty; // the saved password is never shown
            HasSavedPassword = saved.HasPassword;
            Encrypt = s.Encrypt;
            TrustServerCertificate = s.TrustServerCertificate;
            CaCertificatePath = s.CaCertificatePath ?? string.Empty;
            FilePath = s.FilePath ?? string.Empty;
            Dsn = s.Dsn ?? string.Empty;
            OdbcConnectionString = s.OdbcConnectionString ?? string.Empty;
            if (_preset?.Odbc is { } odbc && s.OdbcConnectionString is { Length: > 0 } cs)
                FilePath = ReadOdbcPath(cs, odbc.PathKey);
        }
        finally { _loading = was; }
        NotifyEngineProperties();
        OnPropertyChanged(nameof(ShowUserFields));
        foreach (var f in Fields.Concat(SwitchFields).Concat(AdvancedFields)) f.Refresh();
        ClearTestResult();
        SetFormError(string.Empty);
        PasswordCleared?.Invoke(this, EventArgs.Empty);
    }

    private static string ReadOdbcPath(string connectionString, string key)
    {
        try
        {
            var b = new OdbcConnectionStringBuilder(connectionString);
            return b.TryGetValue(key, out var v) ? v?.ToString() ?? string.Empty : string.Empty;
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }

    // ----------------------------------------------------------------------------------- building settings

    /// <summary>Form error (name, port...), or empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFormError))]
    private string _formError = string.Empty;

    /// <summary>True when the form has a problem to show.</summary>
    public bool HasFormError => FormError.Length > 0;

    private void SetFormError(string text) => FormError = text;

    private bool TryBuildSettings(out DatabaseConnectionSettings settings, bool forTest)
    {
        settings = new DatabaseConnectionSettings();
        if (_descriptor is null) { SetFormError(MissingPackText); return false; }
        string name = Name.Trim();
        if (!forTest || name.Length > 0)
        {
            if (name.Length == 0) { SetFormError(Loc.Get("Conn.Name.Required")); return false; }
        }
        if (IsNew && !forTest && Items.Any(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            SetFormError(Loc.Get("Conn.Name.Exists", name));
            return false;
        }
        foreach (var field in Fields.Concat(SwitchFields).Concat(AdvancedFields))
        {
            if (field.ValidateNumber() is { } problem) { SetFormError(problem); return false; }
        }

        var auth = ShowUserFields ? DatabaseAuthentication.UserPassword : (CanWindowsAuth ? DatabaseAuthentication.WindowsIntegrated : Authentication);
        // Timeouts are not edited here; an existing connection keeps the ones it has.
        int command = _editing?.Settings.CommandTimeoutSeconds ?? DatabaseConnectionSettings.DefaultCommandTimeoutSeconds;
        int connect = _editing?.Settings.ConnectTimeoutSeconds ?? DatabaseConnectionSettings.DefaultConnectTimeoutSeconds;
        // A test of a form that has no name yet uses a placeholder; the name never leaves the machine or reaches the server.
        string settingsName = name.Length > 0 ? name : "test";

        if (_preset?.Odbc is not null)
        {
            // Access, Excel and text files go through a vendor ODBC driver; the preset builds the connection string with the driver's
            // builder so the path cannot add keywords.
            try
            {
                var built = _preset.CreateOdbcSettings(settingsName, FilePath.Trim(), _installedDrivers());
                settings = built with { CommandTimeoutSeconds = command, ConnectTimeoutSeconds = connect };
            }
            catch (DatabaseException ex)
            {
                SetFormError(DataMessages.Describe(ex));
                return false;
            }
            SetFormError(string.Empty);
            return true;
        }

        var s = new DatabaseConnectionSettings
        {
            Name = settingsName,
            Provider = Provider,
            // Built-in engines keep an empty id (the file format of connections.json is unchanged); an add-on engine always carries its id.
            ProviderId = Provider == DatabaseProvider.Custom ? ProviderId : null,
            PresetId = PresetId,
            Authentication = auth,
            CommandTimeoutSeconds = command,
            ConnectTimeoutSeconds = connect,
            // Provider options without a dedicated property, including those set by a preset. Never dropped on re-save.
            Extras = new ProviderExtras(_extras),
        };
        foreach (var option in _descriptor.Options)
        {
            if (option.Key == ProviderOptionKeys.UserName && auth != DatabaseAuthentication.UserPassword) continue;
            if (ProviderOptionKeys.IsStandard(option.Key))
                s = ProviderOptionKeys.WithValue(s, option.Key, GetOptionValue(option.Key).Trim());
        }
        settings = s;
        SetFormError(string.Empty);
        return true;
    }

    // ----------------------------------------------------------------------------------- save, delete

    private bool CanAct => IsIdle && IsFormAvailable;

    /// <summary>Saves the connection.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    public async Task SaveAsync()
    {
        if (!TryBuildSettings(out var settings, forTest: false)) return;
        // Store semantics: null keeps the saved password (and lets the store move a PWD= typed inside an ODBC string into the protected
        // slot), "" removes it. An empty box therefore means "keep". A method that needs no password removes any old one so a stale
        // secret does not linger, EXCEPT for ODBC with a connection string: its password may live in that string (or already did, and
        // was moved to the store on an earlier save), so sending "" there would silently delete it.
        bool hasAccounts = _descriptor?.SupportedAuthentication.Count > 0;
        string? password;
        if (Password.Length > 0 && settings.Authentication == DatabaseAuthentication.UserPassword) password = Password;
        else if (settings.OdbcConnectionString is not null && IsOdbc) password = null;
        else if (settings.Authentication == DatabaseAuthentication.UserPassword && hasAccounts) password = null;
        else password = HasSavedPassword ? string.Empty : null;
        await RunAsync(Loc.Get("Conn.Busy.Saving"), async ct =>
        {
            await _db.Connections.SaveAsync(settings, password, ct);
            LastSavedName = settings.Name;
            await LoadAsync(settings.Name);
            Password = string.Empty;
            PasswordCleared?.Invoke(this, EventArgs.Empty);
            SetTestResult(Loc.Get("Conn.Saved", settings.Name), false);
        });
    }

    /// <summary>Deletes the selected connection after asking.</summary>
    [RelayCommand(CanExecute = nameof(CanDelete))]
    public async Task DeleteAsync()
    {
        if (_editing is null) return;
        string name = _editing.Settings.Name;
        if (!_messages.Confirm(Loc.Get("Conn.Delete.Title"), Loc.Get("Conn.Delete.Body", name))) return;
        await RunAsync(Loc.Get("Conn.Busy.Deleting"), async ct =>
        {
            await _db.Connections.DeleteAsync(name, ct);
            await LoadAsync();
            SetTestResult(Loc.Get("Conn.Deleted", name), false);
        });
    }

    // ----------------------------------------------------------------------------------- test

    /// <summary>Result of the last test, in plain language.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTestResult), nameof(HasTestError), nameof(HasTestSuccess))]
    private string _testText = string.Empty;

    /// <summary>True when the last test failed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTestError), nameof(HasTestSuccess))]
    private bool _testIsError;

    /// <summary>Technical detail of the last failure (free of secrets), or empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTestDetail))]
    private string _testDetail = string.Empty;

    /// <summary>True when the last failure carries a technical detail.</summary>
    public bool HasTestDetail => TestDetail.Length > 0;

    /// <summary>True when the last test failed because the server certificate is not trusted: offers "trust this server".</summary>
    [ObservableProperty]
    private bool _showTrustOffer;

    /// <summary>True when there is a test line.</summary>
    public bool HasTestResult => TestText.Length > 0;

    /// <summary>True when the test line is an error.</summary>
    public bool HasTestError => HasTestResult && TestIsError;

    /// <summary>True when the test line is good news.</summary>
    public bool HasTestSuccess => HasTestResult && !TestIsError;

    private void SetTestResult(string text, bool isError, string? detail = null)
    {
        TestText = text;
        TestIsError = isError;
        TestDetail = detail ?? string.Empty;
    }

    private void ClearTestResult()
    {
        SetTestResult(string.Empty, false);
        ShowTrustOffer = false;
    }

    /// <summary>Opens a connection with the values in the form and closes it again. Nothing is saved.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    public async Task TestAsync()
    {
        if (!TryBuildSettings(out var settings, forTest: true)) return;
        ShowTrustOffer = false;
        // Which password goes with the test:
        //  - one typed in the box wins;
        //  - a sign-in without a password, or an ODBC string that carries its own PWD=, needs none ("" = explicitly no password);
        //  - otherwise the saved one, but only when editing a saved connection under its own name: the store then decides whether the
        //    edit still allows it (it refuses when the address or account changed). A new or unnamed form never borrows a saved
        //    password, so the placeholder name "test" cannot pick up the password of a real connection called "test".
        bool odbcString = IsOdbc && settings.OdbcConnectionString is not null;
        bool ownPassword = odbcString && SecretRedactor.ExtractPassword(settings.OdbcConnectionString!).Password is not null;
        bool wantsPassword = settings.Authentication == DatabaseAuthentication.UserPassword || odbcString;
        string? passwordArg;
        if (Password.Length > 0 && wantsPassword) passwordArg = Password;
        else if (!wantsPassword || ownPassword) passwordArg = string.Empty;
        else if (!IsNew && HasSavedPassword && _editing is not null) passwordArg = null;
        else passwordArg = string.Empty;
        await RunAsync(Loc.Get("Conn.Busy.Testing"), async ct =>
        {
            IDatabaseSource source = await _db.Connections.OpenWithSettingsAsync(settings, passwordArg, ct);
            await source.TestConnectionAsync(ct);
            SetTestResult(Loc.Get("Conn.Test.Ok"), false);
        });
    }

    /// <summary>After a certificate failure: trust this server for this connection, save, and test again.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task TrustAndSaveAsync()
    {
        TrustServerCertificate = true;
        await SaveAsync();
        if (!HasFormError) await TestAsync();
    }

    // ----------------------------------------------------------------------------------- running work

    /// <summary>True while an operation runs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(CanDelete))]
    private bool _isBusy;

    /// <summary>The opposite of <see cref="IsBusy"/>.</summary>
    public bool IsIdle => !IsBusy;

    partial void OnIsBusyChanged(bool value)
    {
        SaveCommand.NotifyCanExecuteChanged();
        TestCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        TrustAndSaveCommand.NotifyCanExecuteChanged();
    }

    /// <summary>What the running operation is doing.</summary>
    [ObservableProperty]
    private string _busyText = string.Empty;

    /// <summary>Stops the running operation.</summary>
    [RelayCommand]
    public void CancelOperation()
    {
        try { _cts?.Cancel(); }
        catch (ObjectDisposedException) { /* already finished */ }
    }

    private async Task RunAsync(string busyText, Func<CancellationToken, Task> work)
    {
        if (IsBusy) return;
        using var cts = new CancellationTokenSource();
        _cts = cts;
        IsBusy = true;
        BusyText = busyText;
        try
        {
            await work(cts.Token);
        }
        catch (OperationCanceledException)
        {
            SetTestResult(Loc.Get("Data.Error.Cancelled"), false);
        }
        catch (DatabaseException ex)
        {
            if (ex.IsCancellation) SetTestResult(Loc.Get("Data.Error.Cancelled"), false);
            else if (ex.Key == ProviderKeys.PasswordReenterRequired)
            {
                // The store refused to reuse the saved password because where or how the connection signs in was edited: ask for it again.
                SetTestResult(Loc.Get("Conn.Test.RetypeFocus"), true);
                PasswordFocusRequested?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                SetTestResult(DataMessages.Describe(ex), true, ex.Detail);
                ShowTrustOffer = ex.Key == DataKeys.CertificateNotTrusted && HasOption(ProviderOptionKeys.TrustServerCertificate) && !TrustServerCertificate;
            }
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
            _cts = null;
        }
    }

    /// <summary>Closes the window.</summary>
    [RelayCommand]
    private void Close()
    {
        CancelOperation();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshLanguage()
    {
        foreach (var e in Engines) e.Refresh();
        // The group headings are text, so the grouped view is built again.
        var keep = SelectedEngine;
        BuildEngines();
        foreach (var c in AuthChoices) c.Refresh();
        foreach (var i in Items) i.Refresh();
        foreach (var f in Fields.Concat(SwitchFields).Concat(AdvancedFields)) f.Refresh();
        foreach (var h in Hints) h.Refresh();
        if (keep is not null)
        {
            _syncingEngine = true;
            try { SelectedEngine = Engines.FirstOrDefault(e => e.Id == keep.Id && e.IsPreset == keep.IsPreset) ?? keep; }
            finally { _syncingEngine = false; }
        }
        if (IsPackMissing && _editing is not null) MissingPackText = Loc.Get("Conn.PackMissing", _editing.Settings.GetEffectiveProviderId());
        OnPropertyChanged(nameof(PasswordHint));
        OnPropertyChanged(nameof(DetectedPresetText));
    }
}
