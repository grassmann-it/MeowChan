using System;
using System.Collections.Specialized;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class MainWindow
{
    private bool _workspaceInitialized;
    private bool _updatingWorkspacePreferences;
    private bool _updatingWorkspaceLayout;
    private bool _workspaceInspectorVisible;
    private bool _normalWorkspaceInspectorVisible;
    private bool _workspaceNarrowInspectorVisible;
    private bool _normalWorkspaceNarrowInspectorVisible;
    private bool _workspaceNarrowLayout;
    private CoalescedUiUpdate? _workspaceRefresh;
    private string? _appliedWorkspaceMode;
    private double _workspaceInspectorWidth = 320;
    private double _normalWorkspaceWidth = 1380;
    private double _normalWorkspaceHeight = 850;
    private WindowState _normalWorkspaceState;

    private void InitializeWorkspace()
    {
        if (_workspaceInitialized) return;
        _workspaceInitialized = true;
        _workspaceRefresh = new CoalescedUiUpdate(RefreshWorkspace);
        // Escape clears the typed search first (SearchBox); in an empty field it closes the search bar.
        DownloadSearchInput.ExitRequested += (_, e) => { if (_downloadSearchOpen) { OnDownloadSearchClose(DownloadSearchInput, new RoutedEventArgs()); e.Handled = true; } };
        UrlList.CollectionChanged += OnWorkspaceCollectionChanged;
        BoardsList.CollectionChanged += OnWorkspaceCollectionChanged;
        Texts.Changed += OnWorkspaceLanguageChanged;
        SizeChanged += OnWorkspaceSizeChanged;
        WorkspaceBody.SizeChanged += OnWorkspaceBodySizeChanged;
        ShellActionErrorBanner.PropertyChanged += OnShellFeedbackVisibilityChanged;
        ApplyWindowChrome();
        PropertyChanged += OnWindowChromePropertyChanged;
        KeyDown += OnWorkspaceKeyDown;
        InitializeShellPresentation();
        InitializeDownloadsHeader();
        SetupInspectorLocalActions();
        WorkspaceVersion.Text = "v" + (typeof(MainWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
            ?? typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? string.Empty);
        ApplyWorkspacePreferences();
        InitializeWindowPreferences();
        InstallMacMenu();
        StartSpeedSampling();
    }

    private void DisposeWorkspace()
    {
        CloseGallery(restoreOrigin: false);
        CloseLargeReader(restoreOrigin: false);
        if (!_workspaceInitialized) return;
        _workspaceInitialized = false;
        StopSpeedSampling();
        DisposeWorkspacePages();
        _workspaceRefresh?.Dispose();
        UrlList.CollectionChanged -= OnWorkspaceCollectionChanged;
        BoardsList.CollectionChanged -= OnWorkspaceCollectionChanged;
        Texts.Changed -= OnWorkspaceLanguageChanged;
        SizeChanged -= OnWorkspaceSizeChanged;
        WorkspaceBody.SizeChanged -= OnWorkspaceBodySizeChanged;
        ShellActionErrorBanner.PropertyChanged -= OnShellFeedbackVisibilityChanged;
        PropertyChanged -= OnWindowChromePropertyChanged;
        KeyDown -= OnWorkspaceKeyDown;
        DisposeShellPresentation();
        DisposeWindowPreferences();
    }

    private T WorkspaceControl<T>(string name) where T : Control => this.FindControl<T>(name)
        ?? throw new InvalidOperationException("The workspace control is missing: " + name);

    private void OnWorkspaceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => _workspaceRefresh?.Request();
    // Visibility selects compact feedback immediately, including while Downloads is hidden.
    private void OnShellFeedbackVisibilityChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty) UpdateWorkspaceLayout();
    }
    // A persistent shell error changes the page allocation without resizing the window.
    private void OnWorkspaceBodySizeChanged(object? sender, SizeChangedEventArgs e) => UpdateWorkspaceLayout();
    private void OnWorkspaceSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        RefreshWorkspaceMinimumAfterResize();
        bool showGraph = Bounds.Width >= 1000, showSpeed = Bounds.Width >= 760;
        if (TransferSpeedGraph.IsVisible != showGraph || TransferSpeed.IsVisible != showSpeed)
        {
            TransferSpeedGraph.IsVisible = showGraph;
            TransferSpeed.IsVisible = showSpeed;
            UpdateSpeedSamplingState();
            _ioUpdate?.RequestImmediate(); // a graph that just appeared needs its history now
        }
        if (ShellHelpOverlay.IsVisible)
            ShellHelpOverlay.MaxHeight = Math.Max(180, ClientSize.Height - 40);
        RememberNormalGeometry();
        QueueWindowPreferences();
        UpdateWorkspaceLayout();
        UpdateDownloadsHeaderLayout();
    }

    private void OnWorkspaceKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || IsNativeShortcutSource(e.Source) || _largeReaderOpen || (_gallery != null && !_gallerySuspended)) return;
        if (_selectedPage != WorkspacePage.Downloads || e.Key != Key.Escape || !WorkspaceInspector.IsVisible) return;
        OnDetailsToggleClick(sender, e);
        WorkspaceControl<DataGrid>(DownloadsTabs.SelectedIndex == 1
            ? "BoardsDataGrid" : "ThreadsDataGrid").Focus();
        e.Handled = true;
    }

    private void OnWorkspaceLanguageChanged(object? sender, EventArgs e)
    {
        if (!_workspaceInitialized) return;
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnWorkspaceLanguageChanged(sender, e));
            return;
        }
        LocalizeWorkspace();
        foreach (ThreadRow row in UrlList) row.UpdateSnapshot(new DesktopThreadEntry(row.WatchId, row.Snapshot, row.HostName, row.BoardName));
        foreach (BoardRow row in BoardsList) row.UpdateSnapshot(new DesktopBoardEntry(row.WatchId, row.Snapshot, row.HostName, row.NiceName));
        RefreshWorkspace();
    }

    private void ApplyWorkspacePreferences()
    {
        if (!_workspaceInitialized) return;
        _updatingWorkspacePreferences = true;
        try
        {
            string mode = _config.WorkspaceMode;
            bool barebones = mode == "Barebones";
            bool advanced = mode == "Advanced";
            bool modeChanged = _appliedWorkspaceMode != mode;
            Classes.Set("workspace-advanced", advanced);
            ComboBox modeChoice = WorkspaceModeComboBox;
            modeChoice.SelectedItem = modeChoice.Items.OfType<ComboBoxItem>()
                .First(item => string.Equals(item.Tag as string, mode, StringComparison.Ordinal));

            if (modeChanged)
            {
                if (barebones)
                {
                    _normalWorkspaceInspectorVisible = _workspaceInspectorVisible;
                    _normalWorkspaceNarrowInspectorVisible = _workspaceNarrowInspectorVisible;
                    // Initial mode selects presentation only. Persisted geometry is
                    // already installed before the first native window is opened.
                    if (_appliedWorkspaceMode != null)
                    {
                        _normalWorkspaceWidth = double.IsFinite(Width) ? Width : 1380;
                        _normalWorkspaceHeight = double.IsFinite(Height) ? Height : 850;
                        _normalWorkspaceState = WindowState;
                        WindowState = WindowState.Normal;
                        SetWorkspaceWindowSize(850, 530);
                    }
                    _workspaceInspectorVisible = false;
                    _workspaceNarrowInspectorVisible = false;
                }
                else if (_appliedWorkspaceMode == "Barebones")
                {
                    SetWorkspaceWindowSize(Math.Max(WorkspaceMinimumSize.Width, _normalWorkspaceWidth),
                        Math.Max(WorkspaceMinimumSize.Height, _normalWorkspaceHeight));
                    WindowState = _normalWorkspaceState;
                    _workspaceInspectorVisible = _normalWorkspaceInspectorVisible;
                    _workspaceNarrowInspectorVisible = _normalWorkspaceNarrowInspectorVisible;
                }
                _appliedWorkspaceMode = mode;
                ApplyArchiveModePreferences();
            }

            WorkspaceHeading.IsVisible = !barebones;
            UpdatePageNavigation();
            WorkspaceVersion.IsVisible = true;
            WorkspaceHeader.Margin = barebones ? new Thickness(12, 0) : new Thickness(20, 0);
            Grid body = WorkspaceBody;
            body.Margin = barebones ? new Thickness(12) : new Thickness(20);
            body.RowSpacing = barebones ? 8 : 10;

            DataGrid threads = ThreadsDataGrid;
            _applyingColumnProfile = true;
            try
            {
            foreach (DataGridColumn column in threads.Columns)
            {
                // Simple keeps task truth visible; Advanced adds independently sortable technical columns.
                bool defaultVisible = column.SortMemberPath switch
                {
                    nameof(ThreadRow.SourceBoard) => !barebones,
                    nameof(ThreadRow.MissingMedia) or nameof(ThreadRow.EarliestRetry) => false,
                    nameof(ThreadRow.ThreadNumber) or nameof(ThreadRow.Status)
                        or nameof(ThreadRow.Queued) or nameof(ThreadRow.Active) => advanced,
                    _ => true
                };
                string key = "ThreadsDataGrid." + column.SortMemberPath;
                column.IsVisible = IsAdvancedWorkColumn(column) ? advanced && (_config.ColumnVisibility.TryGetValue(key, out bool workVisible) && workVisible)
                    : _config.ColumnVisibility.TryGetValue(key, out bool visible) ? visible : defaultVisible;
            }
            foreach (string gridName in new[] { "ThreadsDataGrid", "BoardsDataGrid" })
            {
                foreach (DataGridColumn column in WorkspaceControl<DataGrid>(gridName).Columns)
                {
                    string key = gridName + "." + column.SortMemberPath;
                    if (gridName == "BoardsDataGrid")
                        column.IsVisible = !_config.ColumnVisibility.TryGetValue(key, out bool visible) || visible;
                    if (modeChanged)
                    {
                        if (_config.ColumnWidths.TryGetValue(key, out double width) && double.IsFinite(width) && width >= 32)
                            column.Width = new DataGridLength(width);
                        else if (column.SortMemberPath == nameof(ThreadRow.Completion) && gridName == "ThreadsDataGrid")
                            column.Width = new DataGridLength(advanced ? 140 : 300);
                        else if (_defaultColumnWidths.TryGetValue(key, out DataGridLength defaultWidth))
                            column.Width = defaultWidth;
                    }
                }
                DataGrid identityGrid = WorkspaceControl<DataGrid>(gridName);
                string? restoreIdentity = RequiredDownloadIdentityToRestore(gridName,
                    identityGrid.Columns.Where(column => column.IsVisible).Select(column => column.SortMemberPath).ToArray());
                if (restoreIdentity != null)
                    identityGrid.Columns.First(column => column.SortMemberPath == restoreIdentity).IsVisible = true;
                if (modeChanged)
                {
                    DataGrid grid = WorkspaceControl<DataGrid>(gridName);
                    DataGridColumn[] ordered = grid.Columns
                        .OrderBy(column => _config.ColumnOrder.TryGetValue(gridName + "." + column.SortMemberPath,
                            out int index) ? index : grid.Columns.IndexOf(column)).ToArray();
                    for (int index = 0; index < ordered.Length; index++)
                        ordered[index].DisplayIndex = index;
                }
            }
            }
            finally { _applyingColumnProfile = false; }
            double rowHeight = barebones || _config.CompactDensity ? WorkspaceMetrics.CompactRowHeight : WorkspaceMetrics.NormalRowHeight;
            threads.RowHeight = rowHeight;
            BoardsDataGrid.RowHeight = rowHeight;
            LocalizeWorkspace();
            UpdateWorkspaceLayout();
            RefreshWorkspace();
        }
        finally
        {
            _updatingWorkspacePreferences = false;
        }
    }

    private void LocalizeWorkspace()
    {
        if (!_workspaceInitialized) return;
        foreach (Control control in this.GetLogicalDescendants().OfType<Control>()) LocalizeWorkspaceControl(control);
        if (ToolsNavigationButton.Flyout is MenuFlyout tools)
            foreach (MenuItem item in tools.Items.OfType<MenuItem>()) LocalizeWorkspaceControl(item);
        foreach (string gridName in new[] { "ThreadsDataGrid", "BoardsDataGrid" })
        {
            DataGrid grid = WorkspaceControl<DataGrid>(gridName);
            if (grid.ContextMenu is ContextMenu menu)
                foreach (Control control in menu.GetLogicalDescendants().OfType<Control>()) LocalizeWorkspaceControl(control);
            foreach (DataGridColumn column in grid.Columns)
            {
                string? key = column.SortMemberPath switch
                {
                    nameof(ThreadRow.SourceBoard) => "column.sourceBoard",
                    nameof(ThreadRow.HostName) => "column.source",
                    nameof(ThreadRow.BoardName) or nameof(BoardRow.NiceName) => "column.board",
                    nameof(ThreadRow.ThreadNumber) => "column.threadId",
                    nameof(ThreadRow.Title) => "column.threadTitle",
                    nameof(ThreadRow.Status) => "column.status",
                    nameof(ThreadRow.Completion) => "column.mediaTransfer",
                    nameof(ThreadRow.Queued) => "column.queued",
                    nameof(ThreadRow.Active) => "column.active",
                    nameof(ThreadRow.MissingMedia) => "column.missingMedia",
                    nameof(ThreadRow.EarliestRetry) => "column.earliestRetry",
                    _ => null
                };
                if (key != null) column.Header = Texts.T(key);
            }
        }
        UrlInput.PlaceholderText = Texts.T("downloads.urlPlaceholder");
        AutomationProperties.SetName(UrlInput, Texts.T("downloads.urlPlaceholder"));
        RefreshWorkspaceModeDescriptions();
        AutomationProperties.SetName(WorkspaceModeComboBox, Texts.T("shell.mode"));
        ToolTip.SetTip(WorkspaceModeComboBox, Texts.T("shell.mode"));
        SetupInspectorLocalActions();
        RefreshAdmissionResults();
        UpdateThemeIcon();
        RefreshBrandNavigation();
        LocalizeDownloadsColumnsButton();
        CommandBindings.ApplyLabeled(AddUrlButton, "watch.add-url", Texts.T("action.add"));
        CommandBindings.Apply(ThreadsEmptyImportButton, "watch.import-clipboard", iconWithLabel: true);
        CommandBindings.Apply(BoardsEmptyImportButton, "watch.import-clipboard", iconWithLabel: true);
        CommandBindings.ApplyLabeled(DownloadSearchButton, "input.focus-download-search", Texts.T("downloads.search"));
        CommandBindings.ApplyLabeled(CopyAllUrlsButton, "watch.copy-all-links", Texts.T("downloads.copySessionUrls", UrlList.Count + BoardsList.Count));
        CommandBindings.ApplyLabeled(InspectorOpenFolderButton, "location.open-folder", Texts.T("action.openFolder"));
        CommandBindings.ApplyLabeled(InspectorOpenBrowserButton, "location.open-source", Texts.T("action.openBrowser"));
        CommandBindings.ApplyLabeled(InspectorMediaRetryButton, "watch.retry-media", Texts.T("work.retry"));
        CommandBindings.ApplyLabeled(InspectorCopyUrlButton, "selection.copy-links", Texts.T("action.copyUrl"));
        CommandBindings.ApplyLabeled(InspectorRemoveButton, "watch.remove", Texts.T("action.remove"));
        RefreshShellCommands();
        RefreshSpeedLabel();
        ShowStoreLimitWarning();
        ToolTip.SetTip(InspectorCloseButton, Texts.T("action.hideDetails") + " · Esc");
        AutomationProperties.SetName(InspectorCloseButton, Texts.T("action.hideDetails"));
        AutomationProperties.SetName(WorkspaceMenuItem, Texts.T("nav.settings"));
        ToolTip.SetTip(InspectorProgressBar, Texts.T("detail.knownHint"));
        UpdateWindowControls();
        RefreshSourcesPage();
        UpdateDownloadsHeaderLayout();
    }

    private static void LocalizeWorkspaceControl(Control control)
    {
        if (!control.Classes.Contains("localized") || control.Classes.Contains("command-icon")
            || control.Tag is not string key) return;
        string label = Texts.T(key);
        switch (control)
        {
            case TextBlock text: text.Text = label; break;
            case MenuItem menu: menu.Header = label; break;
            case Button button: button.Content = label; break;
        }
    }

    private void RefreshWorkspace()
    {
        if (!_workspaceInitialized) return;
        if (_galleryCoversWorkspace || _sessionRowsApplying)
        {
            // Covered by the gallery: only the small always-visible shell state stays current. Filtering,
            // collection reconciliation, counts and the inspector are marked dirty and run once on return.
            _sessionDirtyWhileCovered = true;
            SettingsNavigationButton.IsEnabled = SettingsMenuItem.IsEnabled;
            DownloadsNavigationButton.IsEnabled = SettingsMenuItem.IsEnabled;
            RefreshWorkspaceStatusText();
            return;
        }
        if (DownloadSearchNeedsReconciliation)
        {
            _sessionRefresh.RequestImmediate();
            return;
        }
        RefreshDownloadSearchPresentation();
        RefreshMediaRetryControls();
        DesktopSessionProjection priorityFrame = _appliedSessionProjection ?? _session.Projection;
        foreach (ThreadRow row in UrlList) row.ApplyUserPriority(priorityFrame.HighPriorityWatches.Contains(row.WatchId));
        string summary, fullSummary;
        if (DownloadResultsCurrent)
        {
            summary = Texts.T("downloads.statusSummary", UrlList.Count, BoardsList.Count,
                UrlList.Sum(row => (long)row.Queued), UrlList.Sum(row => (long)row.Active));
            var concurrency = priorityFrame.MediaConcurrency;
            fullSummary = summary + " · " + Texts.T("downloads.protectionDetails", concurrency.Configured, concurrency.Effective);
            if (concurrency.DeferredSlots > 0)
                summary += " · " + Texts.T("downloads.protectionShort", concurrency.Effective);
        }
        else summary = fullSummary = DownloadResultsPendingText;
        WorkspaceCounts.Text = summary;
        ToolTip.SetTip(WorkspaceCounts, fullSummary);
        AutomationProperties.SetName(WorkspaceCounts, fullSummary);
        SettingsNavigationButton.IsEnabled = SettingsMenuItem.IsEnabled;
        DownloadsNavigationButton.IsEnabled = SettingsMenuItem.IsEnabled;
        Button sessionUrls = CopyAllUrlsButton;
        sessionUrls.IsEnabled = !_isClosing && (UrlList.Count > 0 || BoardsList.Count > 0);
        CommandBindings.ApplyLabeled(sessionUrls, "watch.copy-all-links", Texts.T("downloads.copySessionUrls", UrlList.Count + BoardsList.Count));
        ToolTip.SetTip(sessionUrls, Texts.T("downloads.copySessionUrlsHint"));
        AutomationProperties.SetHelpText(sessionUrls, Texts.T("downloads.copySessionUrlsHint"));
        RefreshDetailsToggleIcon();
        RefreshWorkspaceStatusText();
        RefreshWorkspaceInspector();
        UpdateDownloadsHeaderLayout();
    }

    private void RefreshWorkspaceStatusText()
    {
        string stateKey = _isClosing ? "shell.stopping" : _session.Projection.IsPauseTransitioning ? "shell.pausing" : _importing ? "shell.importing" : _session.Projection.View.State switch
        {
            DesktopSessionState.Ready => _session.Projection.IsPaused ? "shell.paused" : "shell.ready",
            DesktopSessionState.RecoveryFailed => "shell.recoveryFailed",
            DesktopSessionState.Stopping or DesktopSessionState.Stopped => "shell.stopping",
            _ => "shell.starting"
        };
        WorkspaceStatus.Text = Texts.T(stateKey);
        ToolTip.SetTip(WorkspaceStatus, _session.StartupError);
        RefreshStoreLimitWarning();
    }

    private void RefreshDetailsToggleIcon()
    {
        bool visible = WorkspaceInspector.IsVisible;
        CommandBindings.ApplyLabeled(DetailsToggleButton,
            visible ? "inspector.close" : "thread.details",
            Texts.T(visible ? "action.hideDetails" : "action.showDetails"));
    }

    private bool _statusColumnVisible;

    // With the Status column shown, the compact status in the transfer column only appears when it adds something.
    private void InitializeStatusColumnRedundancy()
    {
        DataGridColumn? status = ThreadsDataGrid.Columns.FirstOrDefault(column => column.SortMemberPath == nameof(ThreadRow.Status));
        if (status == null) return;
        void Sync()
        {
            _statusColumnVisible = status.IsVisible;
            foreach (ThreadRow row in _threadRows.Values) row.StatusColumnVisible = _statusColumnVisible;
        }
        status.PropertyChanged += (_, e) => { if (e.Property == DataGridColumn.IsVisibleProperty) Sync(); };
        Sync();
    }

    private object? GetWorkspaceSelection()
    {
        if (_sessionRowsApplying) return null;
        if (DownloadsTabs.SelectedIndex == 1)
        {
            BoardRow? board = LeadBoard;
            return board != null && BoardsList.Contains(board) ? board : null;
        }
        ThreadRow? thread = LeadThread;
        return thread != null && UrlList.Contains(thread) ? thread : null;
    }

    private string? GetInspectorUrl() => GetWorkspaceSelection() switch
    {
        ThreadRow thread => thread.Url,
        BoardRow board => board.Url,
        _ => null
    };

    private void RefreshWorkspaceInspector()
    {
        if (_sessionRowsApplying) return;
        RefreshInspectorLocalActions();
        object? selected = GetWorkspaceSelection();
        bool hasSelection = selected != null;
        InspectorEmptyState.IsVisible = !hasSelection;
        InspectorSelection.IsVisible = hasSelection;
        if (selected == null) return;

        bool isThread = selected is ThreadRow;
        string title;
        string source;
        string status;
        string url;
        string directory;
        string error;
        string context = string.Empty;
        if (selected is ThreadRow thread)
        {
            title = string.IsNullOrWhiteSpace(thread.Title) ? "#" + thread.ThreadId : thread.Title;
            source = thread.HostName + " · " + thread.BoardName + " · #" + thread.ThreadId;
            status = thread.Status;
            url = thread.Url;
            directory = thread.DownloadDirectory;
            error = thread.IssueDetails;
            context = thread.ContextDetails;
            InspectorProgress.Text = thread.Progress;
            InspectorProgressBar.Value = thread.Completion;
            AutomationProperties.SetName(InspectorProgressBar, thread.Progress);
            InspectorKnownMedia.Text = Texts.T("detail.knownMedia", thread.KnownMedia, thread.ExcludedMedia);
            InspectorQueue.Text = Texts.T("detail.queue") + ": " + thread.Queued + " / " + thread.Active;
        }
        else
        {
            BoardRow board = (BoardRow)selected;
            title = board.NiceName;
            source = board.HostName;
            status = board.Status;
            url = board.Url;
            directory = board.DownloadDirectory;
            error = board.ErrorDetails;
        }
        InspectorKind.Text = Texts.T(isThread ? "detail.thread" : "detail.board");
        InspectorTitle.Text = title;
        InspectorSource.Text = source;
        InspectorStatus.Text = status;
        InspectorUrl.Text = url;
        InspectorDirectory.Text = directory;
        InspectorMediaSection.IsVisible = isThread;
        SelectableTextBlock workContext = InspectorContext;
        workContext.Text = context;
        workContext.IsVisible = !string.IsNullOrWhiteSpace(context);
        SelectableTextBlock errors = InspectorError;
        errors.Text = string.IsNullOrWhiteSpace(error) ? Texts.T("detail.noError") : error;
        errors.Classes.Set("error", !string.IsNullOrWhiteSpace(error));
        InspectorErrorSection.IsVisible = !string.IsNullOrWhiteSpace(error);
        // A retry is the one useful action when the work needs it: it gets the primary look and a named next step.
        bool needsRetry = selected is ThreadRow { NeedsIntervention: true } && !_isClosing;
        InspectorMediaRetryButton.Classes.Set("primary", needsRetry);
        InspectorMediaRetryButton.Classes.Set("subtle", !needsRetry);
        InspectorNextStep.IsVisible = needsRetry;
        InspectorNextStep.Text = needsRetry ? WorkNextStep.Text(CommandCatalog.Label("watch.retry-media"),
            CommandCatalog.ShortcutDisplay("watch.retry-media")) : string.Empty;
        InspectorRemoveHint.Text = Texts.T(isThread ? "detail.removeHint" : "detail.boardRemoveHint");
        foreach (string name in new[] { "InspectorOpenFolderButton", "InspectorOpenBrowserButton", "InspectorCopyUrlButton" })
            WorkspaceControl<Button>(name).IsEnabled = !_isClosing;
        InspectorRemoveButton.IsEnabled = _restoreCompleted && !_isClosing;
        DiagnosticTarget target = selected is ThreadRow selectedThread
            ? new DiagnosticTarget(DiagnosticTargetKind.Thread, selectedThread.WatchId)
            : new DiagnosticTarget(DiagnosticTargetKind.Board, ((BoardRow)selected).WatchId);
        CommandBindings.ApplyLabeled(InspectorCheckButton, "watch.check",
            Texts.T(isThread ? "check.action" : "check.boardAction"));
        InspectorCheckButton.IsEnabled = _restoreCompleted && !WorkspaceCommandBusy
            && _session.CanRequestCheck(target);
    }

    private void UpdateWorkspaceLayout()
    {
        if (!_workspaceInitialized || _updatingWorkspaceLayout) return;
        _updatingWorkspaceLayout = true;
        try
        {
            Grid content = WorkspaceContentGrid;
            Border inspector = WorkspaceInspector;
            GridSplitter splitter = WorkspaceInspectorSplitter;
            bool narrow = Bounds.Width > 0 && Bounds.Width < 1120;
            bool shortWindow = Bounds.Height > 0 && Bounds.Height < 760;
            bool compactIntake = Bounds.Height > 0 && Bounds.Height < 840;
            bool barebones = _config.WorkspaceMode == "Barebones";
            bool reducedChrome = barebones || shortWindow;
            bool retainedErrorLayout = shortWindow && ShellActionErrorBanner.IsVisible;
            Grid feedbackLayout = ShellFeedbackLayout;
            ScrollViewer shellError = ShellActionErrorScroll;
            Button diagnostics = ShellFeedbackDiagnosticsButton;
            Grid.SetColumnSpan(shellError, retainedErrorLayout ? 1 : 2);
            Grid.SetColumn(diagnostics, retainedErrorLayout ? 1 : 0);
            Grid.SetRow(diagnostics, retainedErrorLayout ? 0 : 1);
            feedbackLayout.RowSpacing = retainedErrorLayout ? 0 : 4;
            feedbackLayout.ColumnSpacing = retainedErrorLayout ? 8 : 0;
            shellError.MaxHeight = retainedErrorLayout ? 32 : 64;
            ShellActionErrorBanner.Padding = retainedErrorLayout ? new Thickness(8, 4) : new Thickness(12);
            WorkspaceHeading.IsVisible = !reducedChrome;
            UrlInputHint.IsVisible = !reducedChrome && _config.WorkspaceMode == "Advanced";
            UpdatePageNavigation();
            // All pages and the mode selector remain reachable in compact windows.
            WorkspaceNavigationButtons.IsVisible = true;
            // Keep the shared footer's height and padding from XAML in every view.
            Grid body = WorkspaceBody;
            body.RowSpacing = retainedErrorLayout ? 4 : reducedChrome ? 8 : 10;
            body.Margin = retainedErrorLayout ? new Thickness(barebones ? 8 : 12, 6)
                : barebones ? new Thickness(12) : shortWindow ? new Thickness(16, 12) : new Thickness(20);
            // Keep intake and the latest results usable without allowing large pasted
            // batches to push the table's header and first complete row offscreen.
            UrlInput.MaxHeight = retainedErrorLayout ? 36 : compactIntake ? 40 : 72;
            WorkspaceIntakeCard.Padding = retainedErrorLayout ? new Thickness(4)
                : compactIntake ? new Thickness(8) : new Thickness(12, 10);
            WorkspaceIntakeStack.Spacing = retainedErrorLayout ? 3 : 7;
            // Reserve tabs, the column header and one complete data row. Extra-long
            // repair/report content scrolls in intake instead of reducing the table to zero.
            double rowHeight = barebones || _config.CompactDensity ? 32 : 44;
            double tableMinimum = Math.Max(38, DownloadsTabs.Bounds.Height) + 32 + rowHeight + 4;
            content.MinHeight = retainedErrorLayout ? tableMinimum : 0;
            WorkspaceIntakeScroll.MaxHeight = retainedErrorLayout
                ? Math.Max(0, body.Bounds.Height - 2 * body.RowSpacing - tableMinimum)
                : double.PositiveInfinity;
            AdmissionRepairPanel.Padding = compactIntake ? new Thickness(8, 4) : new Thickness(12);
            Expander admissionResults = AdmissionResultsExpander;
            // Semi's expander spaces its header/content through resource keys rather
            // than the control's Padding. Scope compact spacing to this one report.
            if (compactIntake)
            {
                admissionResults.Resources["ExpanderHeaderMargin"] = new Thickness(0);
                admissionResults.Resources["ExpanderContentPadding"] = new Thickness(8, 0, 8, 4);
            }
            else
            {
                admissionResults.Resources.Remove("ExpanderHeaderMargin");
                admissionResults.Resources.Remove("ExpanderContentPadding");
            }
            AdmissionResultRows.Margin = new Thickness(0, compactIntake ? 4 : 6, 0, 0);
            AdmissionResultsScroll.MaxHeight = compactIntake ? 32 : 140;
            if (!_workspaceNarrowLayout && content.ColumnDefinitions[2].ActualWidth > 0)
                _workspaceInspectorWidth = Math.Clamp(content.ColumnDefinitions[2].ActualWidth, 260, 520);
            _workspaceNarrowLayout = narrow;
            bool visible = narrow ? _workspaceNarrowInspectorVisible : _workspaceInspectorVisible;
            inspector.IsVisible = visible;
            splitter.IsVisible = visible && !narrow;
            // A narrow window opens details over the right side of the table. Reserving a
            // fixed lower row would consume the entire list at small window heights.
            Grid.SetColumn(inspector, narrow ? 0 : 2);
            Grid.SetColumnSpan(inspector, narrow ? 3 : 1);
            inspector.HorizontalAlignment = narrow ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
            inspector.Width = narrow ? 320 : double.NaN;
            content.ColumnDefinitions[1].Width = new GridLength(visible && !narrow ? 6 : 0);
            content.ColumnDefinitions[2].Width = new GridLength(visible && !narrow ? _workspaceInspectorWidth : 0);
            content.RowDefinitions[1].Height = new GridLength(0);
            RefreshDetailsToggleIcon();
        }
        finally { _updatingWorkspaceLayout = false; }
    }

    private void OpenWorkspaceDetails()
    {
        if (!_workspaceInitialized || GetWorkspaceSelection() == null) return;
        if (_workspaceNarrowLayout) _workspaceNarrowInspectorVisible = true;
        else _workspaceInspectorVisible = true;
        UpdateWorkspaceLayout();
        RefreshWorkspace();
    }

    private void OnDetailsToggleClick(object? sender, RoutedEventArgs e)
    {
        if (_workspaceNarrowLayout) _workspaceNarrowInspectorVisible = !_workspaceNarrowInspectorVisible;
        else _workspaceInspectorVisible = !_workspaceInspectorVisible;
        UpdateWorkspaceLayout();
        RefreshWorkspace();
    }

    private void OnWorkspaceSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_refreshingDownloadSearch) return;
        RefreshWorkspace();
    }
    private void OnWorkspaceTabChanged(object? sender, SelectionChangedEventArgs e) => RefreshWorkspace();
    private void OnDownloadsNavigationClick(object? sender, RoutedEventArgs e) => GoToDownloads();

    // The footer button and the empty states of Archive and Media share this destination.
    private void GoToDownloads()
    {
        if (NavigateWorkspace(WorkspacePage.Downloads)) UrlInput.Focus();
    }
    private void OnSettingsNavigationClick(object? sender, RoutedEventArgs e) => OnSettingsMenuItemClick(sender, e);

    private void OnInspectorOpenFolderClick(object? sender, RoutedEventArgs e)
    {
        if (GetWorkspaceSelection() is ThreadRow) OnOpenThreadDirectoryClick(sender, e);
        else if (GetWorkspaceSelection() is BoardRow) OnOpenBoardDirectoryClick(sender, e);
    }

    private void OnInspectorOpenBrowserClick(object? sender, RoutedEventArgs e)
    {
        if (GetWorkspaceSelection() is ThreadRow) OnOpenThreadInBrowserClick(sender, e);
        else if (GetWorkspaceSelection() is BoardRow) OnOpenBoardInBrowserClick(sender, e);
    }

    private void OnInspectorRemoveClick(object? sender, RoutedEventArgs e)
    {
        if (GetWorkspaceSelection() is ThreadRow) OnRemoveThreadMenuItemClick(sender, e);
        else if (GetWorkspaceSelection() is BoardRow) OnRemoveBoardMenuItemClick(sender, e);
    }
}
