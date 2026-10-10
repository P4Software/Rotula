using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rotula.App.Localization;
using Rotula.App.Services;
using Rotula.Data;

namespace Rotula.App.ViewModels;

/// <summary>
/// The connection manager: list, add, edit, delete and test the database connections saved on this computer. Passwords go
/// only through <see cref="IConnectionStore"/> (Windows-protected) and are never shown again; an empty password box keeps the
/// saved one. Nothing here is ever written to a .rotula file.
/// </summary>
public sealed partial class ConnectionManagerViewModel : ObservableObject
{
    private readonly IDatabaseServices _db;
    private readonly IMessageService _messages;
    private readonly IFileDialogService _files;
    private CancellationTokenSource? _cts;
    private bool _loading;
    private SavedConnection? _editing;

    /// <summary>Creates the manager.</summary>
    public ConnectionManagerViewModel(IDatabaseServices db, IMessageService messages, IFileDialogService files)
    {
        _db = db;
        _messages = messages;
        _files = files;
        ProviderChoices = new[]
        {
            new Choice<DatabaseProvider>(DatabaseProvider.SqlServer, "Conn.Provider.SqlServer"),
            new Choice<DatabaseProvider>(DatabaseProvider.PostgreSql, "Conn.Provider.PostgreSql"),
            new Choice<DatabaseProvider>(DatabaseProvider.MySql, "Conn.Provider.MySql"),
            new Choice<DatabaseProvider>(DatabaseProvider.Sqlite, "Conn.Provider.Sqlite"),
            new Choice<DatabaseProvider>(DatabaseProvider.Odbc, "Conn.Provider.Odbc"),
        };
        AuthChoices = new[]
        {
            new Choice<DatabaseAuthentication>(DatabaseAuthentication.WindowsIntegrated, "Conn.Auth.Windows"),
            new Choice<DatabaseAuthentication>(DatabaseAuthentication.UserPassword, "Conn.Auth.UserPassword"),
        };
        _provider = DatabaseProvider.SqlServer;
        Loc.Instance.LanguageChanged += (_, _) => RefreshLanguage();
    }

    /// <summary>Raised when the window should close.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Raised when the password box must be emptied (a different connection was selected, or it was saved).</summary>
    public event EventHandler? PasswordCleared;

    /// <summary>Name of the connection saved last (the caller selects it afterwards), or null.</summary>
    public string? LastSavedName { get; private set; }

    /// <summary>Engines to choose from.</summary>
    public IReadOnlyList<Choice<DatabaseProvider>> ProviderChoices { get; }

    /// <summary>Sign-in methods.</summary>
    public IReadOnlyList<Choice<DatabaseAuthentication>> AuthChoices { get; }

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

    /// <summary>The engine.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsServer), nameof(IsSqlite), nameof(IsOdbc), nameof(CanWindowsAuth), nameof(ShowUserFields), nameof(ShowAuth), nameof(PortHint),
        nameof(ShowEncryptWarning), nameof(ShowTrustWarning))]
    private DatabaseProvider _provider;

    partial void OnProviderChanged(DatabaseProvider value)
    {
        if (_loading) return;
        // Each engine has a usual way to sign in; start there instead of on a method it cannot use.
        Authentication = value is DatabaseProvider.PostgreSql or DatabaseProvider.MySql ? DatabaseAuthentication.UserPassword : DatabaseAuthentication.WindowsIntegrated;
        ClearTestResult();
    }

    /// <summary>Sign-in method.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUserFields))]
    private DatabaseAuthentication _authentication = DatabaseAuthentication.WindowsIntegrated;

    /// <summary>Server host.</summary>
    [ObservableProperty]
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

    /// <summary>SQLite file.</summary>
    [ObservableProperty]
    private string _filePath = string.Empty;

    /// <summary>ODBC data source name.</summary>
    [ObservableProperty]
    private string _dsn = string.Empty;

    /// <summary>ODBC connection string (a password in it is moved into the protected store).</summary>
    [ObservableProperty]
    private string _odbcConnectionString = string.Empty;

    /// <summary>Server-style engines (SQL Server, PostgreSQL, MySQL/MariaDB).</summary>
    public bool IsServer => Provider is DatabaseProvider.SqlServer or DatabaseProvider.PostgreSql or DatabaseProvider.MySql;

    /// <summary>SQLite file.</summary>
    public bool IsSqlite => Provider == DatabaseProvider.Sqlite;

    /// <summary>ODBC.</summary>
    public bool IsOdbc => Provider == DatabaseProvider.Odbc;

    /// <summary>True where the engine can use the current Windows account (MySQL/MariaDB cannot).</summary>
    public bool CanWindowsAuth => Provider is DatabaseProvider.SqlServer or DatabaseProvider.PostgreSql or DatabaseProvider.Odbc;

    /// <summary>True when the sign-in choice is shown (not for a file).</summary>
    public bool ShowAuth => !IsSqlite;

    /// <summary>True when user name and password boxes are shown.</summary>
    public bool ShowUserFields => !IsSqlite && (Authentication == DatabaseAuthentication.UserPassword || !CanWindowsAuth);

    /// <summary>The usual port of the chosen engine, for the empty box.</summary>
    public string PortHint => Provider switch
    {
        DatabaseProvider.SqlServer => "1433", DatabaseProvider.PostgreSql => "5432", DatabaseProvider.MySql => "3306", _ => string.Empty,
    };

    /// <summary>Amber warning shown while encryption is off.</summary>
    public bool ShowEncryptWarning => IsServer && !Encrypt;

    /// <summary>Amber warning shown while an unverified certificate is accepted.</summary>
    public bool ShowTrustWarning => IsServer && TrustServerCertificate;

    partial void OnEncryptChanged(bool value) => OnPropertyChanged(nameof(ShowEncryptWarning));

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
            Provider = DatabaseProvider.SqlServer;
            Authentication = DatabaseAuthentication.WindowsIntegrated;
            Host = PortText = Database = UserName = Password = FilePath = Dsn = OdbcConnectionString = string.Empty;
            Encrypt = true;
            TrustServerCertificate = false;
            HasSavedPassword = false;
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
            Provider = s.Provider;
            Authentication = s.Authentication;
            Host = s.Host ?? string.Empty;
            PortText = s.Port?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            Database = s.Database ?? string.Empty;
            UserName = s.UserName ?? string.Empty;
            Password = string.Empty; // the saved password is never shown
            HasSavedPassword = saved.HasPassword;
            Encrypt = s.Encrypt;
            TrustServerCertificate = s.TrustServerCertificate;
            FilePath = s.FilePath ?? string.Empty;
            Dsn = s.Dsn ?? string.Empty;
            OdbcConnectionString = s.OdbcConnectionString ?? string.Empty;
        }
        finally { _loading = was; }
        OnPropertyChanged(nameof(ShowUserFields));
        ClearTestResult();
        SetFormError(string.Empty);
        PasswordCleared?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Asks for a SQLite file.</summary>
    [RelayCommand]
    private void BrowseFile()
    {
        string? path = _files.PickOpenDatabaseFile();
        if (path is not null) FilePath = path;
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
        int? port = null;
        if (IsServer && PortText.Trim().Length > 0)
        {
            if (!int.TryParse(PortText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int p) || p is < 1 or > 65535)
            {
                SetFormError(Loc.Get("Conn.Port.Invalid"));
                return false;
            }
            port = p;
        }
        var auth = !IsSqlite && ShowUserFields ? DatabaseAuthentication.UserPassword : DatabaseAuthentication.WindowsIntegrated;
        settings = new DatabaseConnectionSettings
        {
            // A test of a form that has no name yet uses a placeholder; the name never leaves the machine or reaches the server.
            Name = name.Length > 0 ? name : "test",
            Provider = Provider,
            Host = IsServer ? NullIfEmpty(Host) : null,
            Port = port,
            Database = IsServer || IsOdbc ? NullIfEmpty(Database) : null,
            Authentication = IsSqlite ? DatabaseAuthentication.WindowsIntegrated : auth,
            UserName = !IsSqlite && auth == DatabaseAuthentication.UserPassword ? NullIfEmpty(UserName) : null,
            Encrypt = !IsServer || Encrypt,
            TrustServerCertificate = IsServer && TrustServerCertificate,
            FilePath = IsSqlite ? NullIfEmpty(FilePath) : null,
            Dsn = IsOdbc ? NullIfEmpty(Dsn) : null,
            OdbcConnectionString = IsOdbc ? NullIfEmpty(OdbcConnectionString) : null,
            // Timeouts are not edited here; an existing connection keeps the ones it has.
            CommandTimeoutSeconds = _editing?.Settings.CommandTimeoutSeconds ?? DatabaseConnectionSettings.DefaultCommandTimeoutSeconds,
            ConnectTimeoutSeconds = _editing?.Settings.ConnectTimeoutSeconds ?? DatabaseConnectionSettings.DefaultConnectTimeoutSeconds,
        };
        SetFormError(string.Empty);
        return true;
    }

    private static string? NullIfEmpty(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    // ----------------------------------------------------------------------------------- save, delete

    /// <summary>Saves the connection.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    public async Task SaveAsync()
    {
        if (!TryBuildSettings(out var settings, forTest: false)) return;
        // Store semantics: null keeps the saved password (and lets the store move a PWD= typed inside an ODBC string into the protected
        // slot), "" removes it. An empty box therefore means "keep". A method that needs no password removes any old one so a stale
        // secret does not linger, EXCEPT for ODBC with a connection string: its password may live in that string (or already did, and
        // was moved to the store on an earlier save), so sending "" there would silently delete it.
        string? password;
        if (Password.Length > 0 && settings.Authentication == DatabaseAuthentication.UserPassword) password = Password;
        else if (IsOdbc && settings.OdbcConnectionString is not null) password = null;
        else if (settings.Authentication == DatabaseAuthentication.UserPassword && !IsSqlite) password = null;
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
    [RelayCommand(CanExecute = nameof(IsIdle))]
    public async Task TestAsync()
    {
        if (!TryBuildSettings(out var settings, forTest: true)) return;
        ShowTrustOffer = false;
        // A saved password cannot be read back. Test with it only when nothing but the password could differ from what is saved.
        // An ODBC string that carries its own PWD= needs nothing from the store.
        bool ownPassword = IsOdbc && settings.OdbcConnectionString is { } raw && SecretRedactor.ExtractPassword(raw).Password is not null;
        bool needsSaved = HasSavedPassword && Password.Length == 0 && !ownPassword
            && (settings.Authentication == DatabaseAuthentication.UserPassword || (IsOdbc && settings.OdbcConnectionString is not null));
        if (needsSaved && (_editing is null || _editing.Settings != settings))
        {
            SetTestResult(Loc.Get("Conn.Test.RetypePassword"), true);
            return;
        }
        string? typed = Password.Length > 0 ? Password : null;
        await RunAsync(Loc.Get("Conn.Busy.Testing"), async ct =>
        {
            IDatabaseSource source = needsSaved
                ? await _db.Connections.OpenAsync(settings.Name, ct)
                : _db.Create(settings, settings.Authentication == DatabaseAuthentication.UserPassword || IsOdbc ? typed : null);
            await source.TestConnectionAsync(ct);
            SetTestResult(Loc.Get("Conn.Test.Ok"), false);
        });
    }

    /// <summary>After a certificate failure: trust this server for this connection, save, and test again.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
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
            else
            {
                SetTestResult(DataMessages.Describe(ex), true, ex.Detail);
                ShowTrustOffer = ex.Key == DataKeys.CertificateNotTrusted && IsServer && !TrustServerCertificate;
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
        foreach (var c in ProviderChoices) c.Refresh();
        foreach (var c in AuthChoices) c.Refresh();
        foreach (var i in Items) i.Refresh();
        OnPropertyChanged(nameof(PasswordHint));
    }
}
