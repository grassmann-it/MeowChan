using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using Avalonia.Styling;
using Avalonia.Media;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MeowChan;

public partial class MainWindow : Window
{
    private readonly Config _config;
    private readonly DesktopSessionHost _session;
    private readonly CoalescedUiUpdate _sessionRefresh;
    private readonly WindowNotificationManager _notificationManager;
    private readonly Queue<Notification> _startupNotifications = new Queue<Notification>();
    private readonly Dictionary<long, ThreadRow> _threadRows = new Dictionary<long, ThreadRow>();
    private readonly Dictionary<long, BoardRow> _boardRows = new Dictionary<long, BoardRow>();
    private readonly HashSet<string> _knownWarnings = new HashSet<string>(StringComparer.Ordinal);
    private readonly Task _restoreTask;
    private TrayIcon? _trayIcon;
    private TrayIcons? _applicationTrayIcons;
    private NativeMenuItem? _clipboardMenuItem;
    private bool _trayInteractionObserved;
    private bool _hasOpened;
    private bool _restoreCompleted;
    private bool _isClosing;
    private bool _shutdownRunning;
    private bool _allowClose;
    private bool _importing;
    private bool _applyingPreferences;
    private NativeMenuItem? _trayOpenItem;
    private NativeMenuItem? _trayFolderItem;
    private NativeMenuItem? _trayExitItem;
    private IPlatformSettings? _trayPlatformSettings;
    private readonly Dictionary<string, Bitmap> _trayMenuIcons = new Dictionary<string, Bitmap>();
    private readonly List<AdmissionFeedback> _admissionResults = new List<AdmissionFeedback>();

    private sealed record AdmissionFeedback(string Url, AdmissionStatus? Status, string? Error);

    public ObservableCollection<ThreadRow> UrlList { get; } = new ObservableCollection<ThreadRow>();
    public ObservableCollection<BoardRow> BoardsList { get; } = new ObservableCollection<BoardRow>();

    public MainWindow() : this(null) { }

    internal MainWindow(DesktopSessionHost? session)
    {
        try { InitializeComponent(); }
        catch
        {
            // A supplied session is already owned by this window, including a failed view construction.
            session?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
        _session = session ?? new DesktopSessionHost();
        _config = _session.Config;
        try
        {
            Texts.Initialize(Path.Combine(_session.ProfileDirectory, "LanguagePacks"));
            Texts.SetLanguage(_config.Language);
            if (Application.Current != null)
            {
                Application.Current.RequestedThemeVariant = _config.Theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
            }
            _sessionRefresh = new CoalescedUiUpdate(RefreshSession);
            _session.StateChanged += OnSessionChanged;
            DataContext = this;

            // The Add button is wired once in XAML. Enter uses that same action.
            this.FindControl<TextBox>("UrlInput")!.AddHandler(KeyDownEvent,
                OnUrlInputKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
            this.FindControl<MenuItem>("SettingsMenuItem")!.Click += OnSettingsMenuItemClick;
            this.FindControl<MenuItem>("AboutMenuItem")!.Click += OnAboutMenuItemClick;
            this.FindControl<MenuItem>("ExitMenuItem")!.Click += async (sender, e) => await CloseApplicationAsync();
            this.FindControl<DataGrid>("ThreadsDataGrid")!.AddHandler(PointerPressedEvent,
                OnThreadPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            this.FindControl<DataGrid>("BoardsDataGrid")!.AddHandler(PointerPressedEvent,
                OnBoardPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            AddHandler(KeyDownEvent, OnKeyDown, handledEventsToo: true);

            _notificationManager = new WindowNotificationManager(this)
            {
                Position = NotificationPosition.TopRight,
                MaxItems = 3
            };
            InitializeWorkspacePages();
            InitializeWorkspace();
            WireWorkspaceCommands();
            InitializeSelectionCommands();
            Texts.Changed += OnLanguageChanged;
            SetupTrayIcon();
            Opened += OnMainWindowOpened;
            UpdateInteractionState();
            _restoreTask = RestoreSessionAsync();
        }
        catch
        {
            Texts.Changed -= OnLanguageChanged;
            _session.StateChanged -= OnSessionChanged;
            DisposeWorkspace();
            DisposeWorkspacePages();
            _sessionRefresh?.Dispose();
            DisposeTrayIcon();
            _session.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    private void OnMainWindowOpened(object? sender, EventArgs e)
    {
        Opened -= OnMainWindowOpened;
        _hasOpened = true;
        if (!string.IsNullOrWhiteSpace(_config.LoadWarning))
        {
            ShowNotification(Texts.T("notification.settings"), _config.LoadWarning, NotificationType.Warning);
        }

        foreach (string warning in Texts.Warnings)
        {
            ShowNotification(Texts.T("notification.settings"), warning, NotificationType.Warning);
        }

        while (_startupNotifications.TryDequeue(out Notification? notification))
        {
            _notificationManager.Show(notification);
        }
    }

    private void OnSessionChanged() => _sessionRefresh.Request();

    private void UpdateInteractionState()
    {
        bool ready = _restoreCompleted && !_isClosing && !_applyingPreferences;
        bool navigationReady = ready && !WorkspaceCommandBusy;
        UpdatePauseControls(navigationReady);

        if (_settingsEditor != null) _settingsEditor.IsAvailable = ready;
        if (_rulesPage != null) _rulesPage.IsAvailable = ready;
        if (_archivePage != null) _archivePage.IsAvailable = ready;
        this.FindControl<Button>("AddUrlButton")!.IsEnabled = ready && !_importing;
        this.FindControl<TextBox>("UrlInput")!.IsEnabled = ready;
        this.FindControl<MenuItem>("SettingsMenuItem")!.IsEnabled = navigationReady;
        this.FindControl<Button>("DownloadsNavigationButton")!.IsEnabled = navigationReady;
        this.FindControl<Button>("SettingsNavigationButton")!.IsEnabled = navigationReady;
        this.FindControl<Button>("ArchiveNavigationButton")!.IsEnabled = navigationReady;
        this.FindControl<Button>("RulesNavigationButton")!.IsEnabled = navigationReady;
        this.FindControl<Button>("SourcesNavigationButton")!.IsEnabled = navigationReady;
        this.FindControl<MenuItem>("SourcesMenuItem")!.IsEnabled = navigationReady;
        this.FindControl<MenuItem>("ArchiveMenuItem")!.IsEnabled = navigationReady;
        this.FindControl<MenuItem>("RulesMenuItem")!.IsEnabled = navigationReady;
        bool diagnosticsReady = !WorkspaceCommandBusy
            && (_restoreCompleted || _session.State == DesktopSessionState.RecoveryFailed);
        this.FindControl<Button>("DiagnosticsNavigationButton")!.IsEnabled = diagnosticsReady;
        this.FindControl<Button>("ToolsNavigationButton")!.IsEnabled = diagnosticsReady;
        this.FindControl<MenuItem>("DiagnosticsMenuItem")!.IsEnabled = diagnosticsReady;
        if (_clipboardMenuItem != null)
        {
            _clipboardMenuItem.IsEnabled = navigationReady && !_importing;
        }
        this.FindControl<ComboBox>("WorkspaceModeComboBox")!.IsEnabled = navigationReady;
        this.FindControl<Button>("ThemeToggleButton")!.IsEnabled = navigationReady;
        RefreshDiagnosticPage();
        RefreshSourcesPage();
        RefreshWorkspace();
    }

    private void RefreshSession()
    {
        if (_isClosing)
        {
            return;
        }

        DesktopSessionView view = _session.GetView();
        HashSet<long> activeThreads = new HashSet<long>(view.Threads.Select(entry => entry.WatchId));
        HashSet<long> activeBoards = new HashSet<long>(view.Boards.Select(entry => entry.WatchId));
        foreach (long id in _threadRows.Keys.Where(id => !activeThreads.Contains(id)).ToArray())
        {
            ThreadRow row = _threadRows[id];
            _threadRows.Remove(id);
            UrlList.Remove(row);
            row.Dispose();
        }
        foreach (long id in _boardRows.Keys.Where(id => !activeBoards.Contains(id)).ToArray())
        {
            BoardRow row = _boardRows[id];
            _boardRows.Remove(id);
            BoardsList.Remove(row);
            row.Dispose();
        }
        foreach (DesktopThreadEntry entry in view.Threads)
        {
            if (!_threadRows.TryGetValue(entry.WatchId, out ThreadRow? row))
            {
                row = new ThreadRow(entry);
                _threadRows.Add(entry.WatchId, row);
                UrlList.Add(row);
            }
            row.UpdateSnapshot(entry);
        }
        foreach (DesktopBoardEntry entry in view.Boards)
        {
            if (!_boardRows.TryGetValue(entry.WatchId, out BoardRow? row))
            {
                row = new BoardRow(entry);
                _boardRows.Add(entry.WatchId, row);
                BoardsList.Add(row);
            }
            row.UpdateSnapshot(entry);
        }
        RefreshWorkspace();

        UpdatePauseControls(_restoreCompleted && !WorkspaceCommandBusy);
        RefreshDiagnosticPage(view);
        RefreshSourcesPage(view);
        HashSet<string> currentWarnings = new HashSet<string>(StringComparer.Ordinal);
        foreach (string warning in view.Warnings)
        {
            if (currentWarnings.Add(warning) && !_knownWarnings.Contains(warning))
            {
                ShowNotification(Texts.T("notification.session"), warning, NotificationType.Warning);
            }
        }

        _knownWarnings.Clear();
        _knownWarnings.UnionWith(currentWarnings);
    }

    private async Task RestoreSessionAsync()
    {
        await _session.StartAsync();
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_isClosing) return;
            RefreshSession();
            _restoreCompleted = _session.State == DesktopSessionState.Ready;
            if (!_restoreCompleted)
            {
                ShowNotification(Texts.T("restore.title"),
                    Texts.T("restore.failure", _session.StartupError ?? string.Empty), NotificationType.Error);
            }
            UpdateInteractionState();
        });
    }

    private async Task<DesktopAdmissionResult?> AddUrlAsync(string url)
    {
        if (!_restoreCompleted || _isClosing) return null;
        try
        {
            DesktopAdmissionResult result = await _session.AddAsync(url);
            _admissionResults.Add(new AdmissionFeedback(url, result.Status, null));
            return result;
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Failed to add URL {Url}", url);
            _admissionResults.Add(new AdmissionFeedback(url, null, exception.Message));
            return null;
        }
    }

    private async void OnAddUrl(object? sender, RoutedEventArgs e)
    {
        if (!_restoreCompleted || _isClosing || _importing || _applyingPreferences) return;
        _importing = true;
        UpdateInteractionState();
        TextBox urlInput = this.FindControl<TextBox>("UrlInput")!;
        string enteredText = urlInput.Text ?? string.Empty;
        try
        {
            bool accepted = await SubmitInputAsync(enteredText);
            if (!string.IsNullOrWhiteSpace(enteredText) && accepted && urlInput.Text == enteredText)
            {
                urlInput.Text = string.Empty;
            }
        }
        finally
        {
            _importing = false;
            UpdateInteractionState();
        }
    }

    private async Task<bool> SubmitInputAsync(string? input)
    {
        if (!_restoreCompleted || _isClosing) return false;
        _admissionResults.Clear();
        bool allAccepted = true;
        try
        {
            allAccepted = await SubmitAdmissionBatchAsync(input);
            if (_admissionResults.Count == 0 && !_isClosing && !_admissionBatchCanceled)
            {
                bool fromClipboard = string.IsNullOrWhiteSpace(input);
                ShowNotification(Texts.T(fromClipboard ? "clipboard.title" : "admission.results"),
                    Texts.T(fromClipboard ? "clipboard.empty" : "admission.empty"), NotificationType.Information);
            }
            return allAccepted && _admissionResults.Count != 0;
        }
        catch (OperationCanceledException) when (_isClosing)
        {
            return false;
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Failed to read URLs from the clipboard.");
            ShowNotification(Texts.T("notification.error"), Texts.T("clipboard.readFailed"), NotificationType.Error);
            return false;
        }
        finally
        {
            if (!_isClosing) RefreshAdmissionResults();
        }
    }

    private void RefreshAdmissionResults()
    {
        Border panel = this.FindControl<Border>("AdmissionResultsPanel")!;
        StackPanel rows = this.FindControl<StackPanel>("AdmissionResultRows")!;
        panel.IsVisible = _admissionResults.Count > 0;
        rows.Children.Clear();
        int added = _admissionResults.Count(item => item.Status == AdmissionStatus.Added);
        int duplicate = _admissionResults.Count(item => item.Status == AdmissionStatus.AlreadyPresent);
        this.FindControl<TextBlock>("AdmissionResultsTitle")!.Text = Texts.T("admission.summary", added, duplicate, _admissionResults.Count - added - duplicate);
        if (_admissionResults.Count == 0) return;
        // One selectable, scrollable report keeps large imports from creating a control per URL.
        TextBox report = new TextBox
        {
            Name = "AdmissionReport", IsReadOnly = true, AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap, MaxHeight = 150,
            Text = string.Join(Environment.NewLine, _admissionResults.Select(item =>
            {
                string key = item.Status switch
                {
                    AdmissionStatus.Added => "admission.added",
                    AdmissionStatus.AlreadyPresent => "admission.alreadyPresent",
                    AdmissionStatus.Invalid => "admission.invalid",
                    AdmissionStatus.Unsupported => "admission.unsupported",
                    _ => "admission.failed"
                };
                return Texts.T(key) + " · " + item.Url + (item.Error == null ? string.Empty : " · " + item.Error);
            }))
        };
        rows.Children.Add(report);
    }

    private async Task<string?> ReadClipboardAsync()
    {
        if (_isClosing)
        {
            return null;
        }

        if (Clipboard == null)
        {
            throw new InvalidOperationException("The clipboard is not available on this desktop.");
        }

        return await Clipboard.TryGetTextAsync();
    }

    private void OnUrlInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;
            OnAddUrl(sender, new RoutedEventArgs());
        }
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || IsNativeShortcutSource(e.Source) || !IsEnabled || _largeReaderOpen || (_gallery != null && !_gallerySuspended)) return;
        TextBox urlInput = this.FindControl<TextBox>("UrlInput")!;
        KeyModifiers pasteModifier = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        if (e.KeyModifiers == pasteModifier && e.Key == Key.L)
        {
            e.Handled = true;
            if (NavigateWorkspace(WorkspacePage.Downloads)) urlInput.Focus();
            return;
        }
        if (e.KeyModifiers == pasteModifier && e.Key == Key.V && e.Source is not TextBox &&
            (e.Source as Visual)?.FindAncestorOfType<TextBox>() == null)
        {
            e.Handled = true;
            await ImportClipboardAsync();
        }
    }

    private async Task ImportClipboardAsync()
    {
        if (!_restoreCompleted || _importing || WorkspaceCommandBusy)
        {
            return;
        }

        NavigateWorkspace(WorkspacePage.Downloads);
        _importing = true;
        UpdateInteractionState();
        try
        {
            await SubmitInputAsync(null);
        }
        finally
        {
            _importing = false;
            UpdateInteractionState();
        }
    }

    private void SetupTrayIcon()
    {
        try
        {
            _trayIcon = new TrayIcon();
            ConfigureTrayIcon(_trayIcon);
            Application application = Application.Current ?? throw new InvalidOperationException("The desktop application is unavailable.");
            _applicationTrayIcons = TrayIcon.GetIcons(application);
            if (_applicationTrayIcons == null)
            {
                _applicationTrayIcons = new TrayIcons();
                TrayIcon.SetIcons(application, _applicationTrayIcons);
            }
            // Avalonia 12 creates the native implementation when the icon joins this collection.
            _applicationTrayIcons.Add(_trayIcon);
            if (_trayIcon.NativeMenuExporter == null)
            {
                DisposeTrayIcon();
                Log.Information("Tray menus are unavailable; minimize will keep the window in the taskbar.");
                return;
            }
        }
        catch (Exception exception)
        {
            DisposeTrayIcon();
            Log.Warning(exception, "Could not create the tray icon; minimize will keep the window in the taskbar.");
        }
    }

    private void ConfigureTrayIcon(TrayIcon trayIcon)
    {
        _trayPlatformSettings = Application.Current?.PlatformSettings;
        using Stream iconStream = AssetLoader.Open(new Uri("avares://MeowChan/Assets/meowchan.png"));
        trayIcon.Icon = new WindowIcon(iconStream);
        trayIcon.ToolTipText = "MeowChan - Image Downloader";
        NativeMenu menu = new NativeMenu();
        NativeMenuItem openItem = CreateTrayMenuItem(Texts.T("tray.open"), "list");
        _trayOpenItem = openItem;
        openItem.Click += (sender, e) =>
        {
            _trayInteractionObserved = true;
            RestoreWindow();
        };
        menu.Items.Add(openItem);
        NativeMenuItem folderItem = CreateTrayMenuItem(Texts.T("tray.folder"), "folder");
        _trayFolderItem = folderItem;
        folderItem.Click += (sender, e) => OpenDirectory(_config.DownloadPath);
        menu.Items.Add(folderItem);
        _clipboardMenuItem = CreateTrayMenuItem(Texts.T("tray.clipboard"), "clipboard");
        _clipboardMenuItem.Click += async (sender, e) => await ImportClipboardAsync();
        menu.Items.Add(_clipboardMenuItem);
        NativeMenuItem exitItem = CreateTrayMenuItem(Texts.T("tray.exit"), "power");
        _trayExitItem = exitItem;
        exitItem.Click += async (sender, e) => await CloseApplicationAsync();
        menu.Items.Add(exitItem);
        trayIcon.Menu = menu;
        trayIcon.Clicked += (sender, e) =>
        {
            _trayInteractionObserved = true;
            if (IsVisible && WindowState != WindowState.Minimized)
            {
                Hide();
            }
            else
            {
                RestoreWindow();
            }
        };
        trayIcon.IsVisible = true;
        if (_trayPlatformSettings != null)
            _trayPlatformSettings.ColorValuesChanged += OnTrayColorValuesChanged;
    }

    private void RestoreWindow()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    private void MinimizeWindow()
    {
        // Avalonia 11 exposes requested visibility, not whether a shell hosts the icon.
        // Only a real tray interaction proves there is a route back to a hidden window.
        if (_trayInteractionObserved && _trayIcon?.IsVisible == true)
        {
            Hide();
        }
        else
        {
            ShowInTaskbar = true;
            WindowState = WindowState.Minimized;
        }
    }

    private void DisposeTrayIcon()
    {
        if (_trayPlatformSettings != null)
            _trayPlatformSettings.ColorValuesChanged -= OnTrayColorValuesChanged;
        _trayPlatformSettings = null;
        TrayIcon? trayIcon = _trayIcon;
        _trayIcon = null;
        _clipboardMenuItem = null;
        _trayOpenItem = null;
        _trayFolderItem = null;
        _trayExitItem = null;
        _trayInteractionObserved = false;
        try
        {
            if (trayIcon != null)
            {
                try { _applicationTrayIcons?.Remove(trayIcon); }
                finally { trayIcon.Dispose(); }
            }
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Could not dispose the tray icon.");
        }
        _applicationTrayIcons = null;
        foreach (Bitmap icon in _trayMenuIcons.Values)
            icon.Dispose();
        _trayMenuIcons.Clear();
    }

    private NativeMenuItem CreateTrayMenuItem(string header, string icon)
    {
        return new NativeMenuItem
        {
            Header = header,
            Icon = GetTrayMenuIcon(icon)
        };
    }

    private Bitmap GetTrayMenuIcon(string name)
    {
        // Windows uses an Avalonia popup; Linux/macOS menus follow the desktop theme.
        bool dark = OperatingSystem.IsWindows() ? _config.Theme != "Light"
            : _trayPlatformSettings?.GetColorValues().ThemeVariant == PlatformThemeVariant.Dark;
        string asset = (dark ? "light/" : string.Empty) + name;
        if (_trayMenuIcons.TryGetValue(asset, out Bitmap? cached))
            return cached;
        using Stream stream = AssetLoader.Open(new Uri($"avares://MeowChan/Assets/icons/{asset}.png"));
        Bitmap bitmap = new Bitmap(stream);
        _trayMenuIcons.Add(asset, bitmap);
        return bitmap;
    }

    private void OnTrayColorValuesChanged(object? sender, PlatformColorValues values)
    {
        Dispatcher.UIThread.Post(RefreshTrayMenuIcons);
    }

    private void RefreshTrayMenuIcons()
    {
        if (_isClosing || _trayIcon == null) return;
        if (_trayOpenItem != null) _trayOpenItem.Icon = GetTrayMenuIcon("list");
        if (_trayFolderItem != null) _trayFolderItem.Icon = GetTrayMenuIcon("folder");
        if (_clipboardMenuItem != null) _clipboardMenuItem.Icon = GetTrayMenuIcon("clipboard");
        if (_trayExitItem != null) _trayExitItem.Icon = GetTrayMenuIcon("power");
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        Log.Debug("Window closing requested: reason {CloseReason}, allowed {AllowClose}, shutdown running {ShutdownRunning}, owned windows {OwnedWindowCount}.",
            e.CloseReason, _allowClose, _shutdownRunning, OwnedWindows.Count);
        base.OnClosing(e);
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        if (_isClosing)
        {
            return;
        }

        if (_config.MinimizeToTray && e.CloseReason == WindowCloseReason.WindowClosing)
        {
            MinimizeWindow();
            return;
        }

        await CloseApplicationAsync();
    }

    private async Task CloseApplicationAsync()
    {
        if (_shutdownRunning || _allowClose)
        {
            return;
        }

        _isClosing = true;
        _shutdownRunning = true;
        _settingsEditor?.EndLifetime();
        UpdateInteractionState();
        Log.Information("Application shutdown started.");
        try
        {
            Log.Debug("Shutdown: waiting for session restoration.");
            await _restoreTask;
            CloseGallery(restoreOrigin: false);
            await _galleryCleanupTask;
            Log.Debug("Shutdown: gallery media released; stopping the session.");
            await _session.StopAsync();
            Log.Debug("Shutdown: session stopped; disposing the session.");
            Log.Debug("Shutdown: session disposed; releasing UI subscriptions.");
            _session.StateChanged -= OnSessionChanged;
            Texts.Changed -= OnLanguageChanged;
            DisposeWorkspace();
            _sessionRefresh.Dispose();
            foreach (ThreadRow row in _threadRows.Values)
            {
                row.Dispose();
            }

            foreach (BoardRow row in _boardRows.Values)
            {
                row.Dispose();
            }

            _clipboardMenuItem = null;
            if (OperatingSystem.IsLinux())
            {
                // Only DBus needs its menu subscriptions detached and queued layout updates drained.
                // Avalonia 11.3.22's macOS exporter rejects Menu=null; other platforms dispose directly.
                Log.Debug("Shutdown: detaching the DBus tray menu; tray present {TrayPresent}.", _trayIcon != null);
                if (_trayIcon != null)
                {
                    _trayIcon.Menu = null;
                }

                Log.Debug("Shutdown: DBus tray menu detached; waiting for the background dispatcher.");
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                Log.Debug("Shutdown: background dispatcher completed.");
            }

            Log.Debug("Shutdown: disposing the tray icon; tray present {TrayPresent}.", _trayIcon != null);
            DisposeTrayIcon();
            Log.Debug("Shutdown: tray disposal returned; releasing the profile lease.");
            await _session.DisposeAsync();
            Log.Debug("Shutdown: profile lease released; requesting final window close.");
            _allowClose = true;
            Close();
            Log.Information("Application shutdown window close returned; visible {IsVisible}.", IsVisible);
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Failed to stop the application.");
            ShowNotification(Texts.T("notification.error"), Texts.T("shutdown.failed"), NotificationType.Error);
        }
        finally
        {
            _shutdownRunning = false;
        }
    }

    private void ShowNotification(string title, string message, NotificationType type)
    {
        if (_allowClose)
        {
            return;
        }

        Notification notification = new Notification(title, message, type);
        if (!_hasOpened)
        {
            _startupNotifications.Enqueue(notification);
        }
        else
        {
            _notificationManager.Show(notification);
        }
    }

    private ThreadRow? SelectedThread => LeadThread;
    private BoardRow? SelectedBoard => LeadBoard;

    private void OnOpenThreadInBrowserClick(object? sender, RoutedEventArgs e)
    {
        if (!_isClosing && SelectedThread is ThreadRow row) OpenTarget(row.Url);
    }

    private void OnOpenBoardInBrowserClick(object? sender, RoutedEventArgs e)
    {
        if (!_isClosing && SelectedBoard is BoardRow row) OpenTarget(row.Url);
    }

    private async void OpenTarget(string target)
    {
        try
        {
            await ExternalLauncher.OpenAsync(target);
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Failed to open {Target}", target);
            ShowNotification(Texts.T("notification.error"), Texts.T("location.openFailed"), NotificationType.Error);
        }
    }

    private void OpenDirectory(string directory)
    {
        if (Directory.Exists(directory)) OpenTarget(directory);
        else ShowNotification(Texts.T("location.missingTitle"), Texts.T("location.missing"), NotificationType.Warning);
    }

    private void OnOpenThreadDirectoryClick(object? sender, RoutedEventArgs e)
    {
        if (!_isClosing && SelectedThread is ThreadRow row) OpenDirectory(row.DownloadDirectory);
    }

    private void OnOpenBoardDirectoryClick(object? sender, RoutedEventArgs e)
    {
        if (!_isClosing && SelectedBoard is BoardRow row) OpenDirectory(row.DownloadDirectory);
    }

    private async void OnRemoveThreadMenuItemClick(object? sender, RoutedEventArgs e) => await RemoveDownloadSelectionAsync(false);
    private async void OnRemoveBoardMenuItemClick(object? sender, RoutedEventArgs e) => await RemoveDownloadSelectionAsync(true);

    private void OnThreadPointerPressed(object? sender, PointerPressedEventArgs e) => SelectDownloadContextRow(sender, e);
    private void OnBoardPointerPressed(object? sender, PointerPressedEventArgs e) => SelectDownloadContextRow(sender, e);
    private async void OnThreadDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual tapped && (tapped is Button or TextBox || tapped.FindAncestorOfType<Button>() != null)) return;
        DataGridRow? target = (e.Source as Visual)?.FindAncestorOfType<DataGridRow>();
        if (target?.DataContext is not ThreadRow row) return;
        e.Handled = true;
        await OpenDownloadReaderAsync(row.Snapshot.Id, target);
    }
    private void OnBoardDoubleTapped(object? sender, TappedEventArgs e) => OpenWorkspaceDetails();

    private void OnSettingsMenuItemClick(object? sender, RoutedEventArgs e) => NavigateWorkspace(WorkspacePage.Settings);

    private async Task ApplySettingsAsync(Config candidate)
    {
        if (!_restoreCompleted || _isClosing || _applyingPreferences)
            throw new InvalidOperationException("The session is not ready to apply settings.");
        _applyingPreferences = true;
        UpdateInteractionState();
        try
        {
            await _session.ApplySettingsAsync(candidate);
            _config.CopyFrom(candidate);
            ApplyAppearance();
        }
        finally
        {
            _applyingPreferences = false;
            if (!_isClosing) UpdateInteractionState();
        }
    }

    private void ApplyAppearance()
    {
        if (_isClosing) return;
        if (Application.Current != null)
            Application.Current.RequestedThemeVariant = _config.Theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
        if (OperatingSystem.IsWindows()) RefreshTrayMenuIcons();
        Texts.SetLanguage(_config.Language);
        ApplyWorkspacePreferences();
    }

    private void WireWorkspaceCommands()
    {
        this.FindControl<ComboBox>("WorkspaceModeComboBox")!.SelectionChanged += async (sender, e) =>
        {
            if (_updatingWorkspacePreferences) return;
            if (!_restoreCompleted || WorkspaceCommandBusy)
            {
                ApplyWorkspacePreferences();
                return;
            }
            if (this.FindControl<ComboBox>("WorkspaceModeComboBox")!.SelectedItem is ComboBoxItem item && item.Tag is string mode && mode != _config.WorkspaceMode)
            {
                Config candidate = _config.Clone();
                candidate.WorkspaceMode = mode;
                await SaveAppearanceAsync(candidate);
            }
        };
        this.FindControl<Button>("ThemeToggleButton")!.Click += async (sender, e) =>
        {
            if (!_restoreCompleted || WorkspaceCommandBusy) return;
            Config candidate = _config.Clone();
            candidate.Theme = _config.Theme == "Dark" ? "Light" : "Dark";
            await SaveAppearanceAsync(candidate);
        };
        this.FindControl<Button>("CopyAllUrlsButton")!.Click += async (sender, e) =>
            await CopyUrlsAsync(UrlList.Select(row => row.Url).Concat(BoardsList.Select(row => row.Url)));
        this.FindControl<MenuItem>("ThreadCopyUrlMenuItem")!.Click += async (sender, e) =>
            await CopyUrlsAsync(SelectedThreads().Select(row => row.Url).ToArray());
        this.FindControl<MenuItem>("BoardCopyUrlMenuItem")!.Click += async (sender, e) =>
            await CopyUrlsAsync(SelectedBoards().Select(row => row.Url).ToArray());
        this.FindControl<Button>("InspectorCopyUrlButton")!.Click += async (sender, e) =>
            await CopyUrlsAsync(GetInspectorUrl() is string url ? new[] { url } : Array.Empty<string>());
    }

    private async Task SaveAppearanceAsync(Config candidate)
    {
        bool themeChanged = candidate.Theme != _config.Theme;
        bool modeChanged = candidate.WorkspaceMode != _config.WorkspaceMode;
        try
        {
            await ApplySettingsAsync(candidate);
            _settingsEditor?.RefreshConfirmedAppearance(themeChanged, modeChanged);
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Could not save display preferences.");
            ApplyWorkspacePreferences();
            ShowNotification(Texts.T("notification.error"), Texts.T("preferences.failed", exception.Message), NotificationType.Error);
        }
    }

    private async Task CopyUrlsAsync(IEnumerable<string> urls)
    {
        string text = string.Join(Environment.NewLine, urls.Distinct(StringComparer.Ordinal));
        if (_isClosing || string.IsNullOrEmpty(text)) return;
        try
        {
            if (Clipboard == null) throw new InvalidOperationException("Clipboard is unavailable.");
            await Clipboard.SetTextAsync(text);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Could not copy URLs.");
            ShowNotification(Texts.T("notification.error"), Texts.T("clipboard.writeFailed"), NotificationType.Error);
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnLanguageChanged(sender, e));
            return;
        }
        if (_isClosing) return;
        _sessionRefresh.Request();
        RefreshAdmissionResults();
        if (_trayOpenItem != null) _trayOpenItem.Header = Texts.T("tray.open");
        if (_trayFolderItem != null) _trayFolderItem.Header = Texts.T("tray.folder");
        if (_clipboardMenuItem != null) _clipboardMenuItem.Header = Texts.T("tray.clipboard");
        if (_trayExitItem != null) _trayExitItem.Header = Texts.T("tray.exit");
    }

    private void UpdatePauseControls(bool enabled)
    {
        bool paused = _session.IsPaused;
        MenuItem menu = this.FindControl<MenuItem>("PauseMenuItem")!;
        menu.IsEnabled = enabled && !_session.IsPauseTransitioning;
        menu.Header = Texts.T(paused ? "action.resume" : "action.pause");
        Button button = this.FindControl<Button>("PauseButton")!;
        button.IsEnabled = enabled && !_session.IsPauseTransitioning;
        button.Content = Texts.T(_session.IsPauseTransitioning ? "action.pausingShort" : paused ? "action.resumeShort" : "action.pauseShort");
        button.Classes.Set("selected", paused);
        ToolTip.SetTip(button, menu.Header);
    }

    private async void OnPauseClick(object? sender, RoutedEventArgs e)
    {
        if (!_restoreCompleted || WorkspaceCommandBusy || _session.IsPauseTransitioning) return;
        _applyingPreferences = true;
        UpdateInteractionState();
        try { await _session.SetPausedAsync(!_session.IsPaused); }
        catch (Exception exception)
        {
            ShowNotification(Texts.T("notification.error"), Texts.T("preferences.failed", exception.Message), NotificationType.Error);
        }
        finally { _applyingPreferences = false; UpdateInteractionState(); }
    }

    private async void OnAboutMenuItemClick(object? sender, RoutedEventArgs e)
    {
        if (!_isClosing)
        {
            AboutWindow aboutWindow = new AboutWindow();
            await aboutWindow.ShowDialog(this);
        }
    }
}
