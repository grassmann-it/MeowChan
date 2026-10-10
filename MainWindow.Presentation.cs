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
using oChan.Desktop;
using oChan.Desktop.Localization;

namespace oChan;

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
        UrlList.CollectionChanged += OnWorkspaceCollectionChanged;
        BoardsList.CollectionChanged += OnWorkspaceCollectionChanged;
        Texts.Changed += OnWorkspaceLanguageChanged;
        SizeChanged += OnWorkspaceSizeChanged;
        KeyDown += OnWorkspaceKeyDown;
        WorkspaceControl<TextBlock>("WorkspaceVersion").Text = "v" + (typeof(MainWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
            ?? typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? string.Empty);
        ApplyWorkspacePreferences();
    }

    private void DisposeWorkspace()
    {
        if (!_workspaceInitialized) return;
        _workspaceInitialized = false;
        DisposeWorkspacePages();
        _workspaceRefresh?.Dispose();
        UrlList.CollectionChanged -= OnWorkspaceCollectionChanged;
        BoardsList.CollectionChanged -= OnWorkspaceCollectionChanged;
        Texts.Changed -= OnWorkspaceLanguageChanged;
        SizeChanged -= OnWorkspaceSizeChanged;
        KeyDown -= OnWorkspaceKeyDown;
    }

    private T WorkspaceControl<T>(string name) where T : Control => this.FindControl<T>(name)
        ?? throw new InvalidOperationException("The workspace control is missing: " + name);

    private void OnWorkspaceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => _workspaceRefresh?.Request();
    private void OnWorkspaceSizeChanged(object? sender, SizeChangedEventArgs e) => UpdateWorkspaceLayout();

    private void OnWorkspaceKeyDown(object? sender, KeyEventArgs e)
    {
        if (_settingsPageSelected || e.Key != Key.Escape || !WorkspaceControl<Border>("WorkspaceInspector").IsVisible) return;
        OnDetailsToggleClick(sender, e);
        WorkspaceControl<DataGrid>(WorkspaceControl<TabControl>("DownloadsTabs").SelectedIndex == 1
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
            ComboBox modeChoice = WorkspaceControl<ComboBox>("WorkspaceModeComboBox");
            modeChoice.SelectedItem = modeChoice.Items.OfType<ComboBoxItem>()
                .First(item => string.Equals(item.Tag as string, mode, StringComparison.Ordinal));

            if (modeChanged)
            {
                if (barebones)
                {
                    _normalWorkspaceInspectorVisible = _workspaceInspectorVisible;
                    _normalWorkspaceNarrowInspectorVisible = _workspaceNarrowInspectorVisible;
                    _normalWorkspaceWidth = double.IsFinite(Width) ? Width : 1380;
                    _normalWorkspaceHeight = double.IsFinite(Height) ? Height : 850;
                    _normalWorkspaceState = WindowState;
                    WindowState = WindowState.Normal;
                    MinWidth = 640;
                    MinHeight = 480;
                    Width = 850;
                    Height = 530;
                    _workspaceInspectorVisible = false;
                    _workspaceNarrowInspectorVisible = false;
                }
                else if (_appliedWorkspaceMode == "Barebones")
                {
                    MinWidth = 760;
                    MinHeight = 500;
                    Width = Math.Max(MinWidth, _normalWorkspaceWidth);
                    Height = Math.Max(MinHeight, _normalWorkspaceHeight);
                    WindowState = _normalWorkspaceState;
                    _workspaceInspectorVisible = _normalWorkspaceInspectorVisible;
                    _workspaceNarrowInspectorVisible = _normalWorkspaceNarrowInspectorVisible;
                }
                _appliedWorkspaceMode = mode;
            }

            WorkspaceControl<Grid>("WorkspaceHeading").IsVisible = !barebones;
            WorkspaceControl<TextBlock>("WorkspaceSubtitle").IsVisible = !barebones;
            WorkspaceControl<TextBlock>("WorkspaceVersion").IsVisible = !barebones;
            WorkspaceControl<Grid>("WorkspaceHeader").Margin = barebones ? new Thickness(12, 0) : new Thickness(20, 0);
            Grid body = WorkspaceControl<Grid>("WorkspaceBody");
            body.Margin = barebones ? new Thickness(12) : new Thickness(20);
            body.RowSpacing = barebones ? 8 : 10;

            DataGrid threads = WorkspaceControl<DataGrid>("ThreadsDataGrid");
            foreach (DataGridColumn column in threads.Columns)
            {
                // Simple keeps task truth visible; Advanced adds independently sortable technical columns.
                column.IsVisible = column.SortMemberPath switch
                {
                    nameof(ThreadRow.SourceBoard) => !barebones,
                    nameof(ThreadRow.ThreadNumber) or nameof(ThreadRow.Status)
                        or nameof(ThreadRow.Queued) or nameof(ThreadRow.Active) => advanced,
                    _ => true
                };
                if (modeChanged && column.SortMemberPath == nameof(ThreadRow.Completion))
                    column.Width = new DataGridLength(advanced ? 140 : 300);
            }
            double rowHeight = barebones || _config.CompactDensity ? 32 : 44;
            threads.RowHeight = rowHeight;
            WorkspaceControl<DataGrid>("BoardsDataGrid").RowHeight = rowHeight;
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
                    _ => null
                };
                if (key != null) column.Header = Texts.T(key);
            }
        }
        WorkspaceControl<TextBox>("UrlInput").PlaceholderText = Texts.T("downloads.urlPlaceholder");
        AutomationProperties.SetName(WorkspaceControl<TextBox>("UrlInput"), Texts.T("downloads.urlPlaceholder"));
        WorkspaceControl<ComboBoxItem>("SimpleModeItem").Content = Texts.T("mode.simple");
        WorkspaceControl<ComboBoxItem>("AdvancedModeItem").Content = Texts.T("mode.advanced");
        WorkspaceControl<ComboBoxItem>("BarebonesModeItem").Content = Texts.T("mode.barebones");
        AutomationProperties.SetName(WorkspaceControl<ComboBox>("WorkspaceModeComboBox"), Texts.T("shell.mode"));
        ToolTip.SetTip(WorkspaceControl<ComboBox>("WorkspaceModeComboBox"), Texts.T("shell.mode"));
        WorkspaceControl<Button>("ThemeToggleButton").Content = Texts.T(_config.Theme == "Dark" ? "settings.light" : "settings.dark");
        ToolTip.SetTip(WorkspaceControl<Button>("InspectorCloseButton"), Texts.T("action.hideDetails") + " · Esc");
        AutomationProperties.SetName(WorkspaceControl<Button>("InspectorCloseButton"), Texts.T("action.hideDetails"));
        AutomationProperties.SetName(WorkspaceControl<MenuItem>("WorkspaceMenuItem"), Texts.T("nav.settings"));
        ToolTip.SetTip(WorkspaceControl<ProgressBar>("InspectorProgressBar"), Texts.T("detail.knownHint"));
    }

    private static void LocalizeWorkspaceControl(Control control)
    {
        if (!control.Classes.Contains("localized") || control.Tag is not string key) return;
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
        string summary = Texts.T("downloads.statusSummary", UrlList.Count, BoardsList.Count,
            UrlList.Sum(row => (long)row.Queued), UrlList.Sum(row => (long)row.Active));
        WorkspaceControl<TextBlock>("WorkspaceCounts").Text = summary;
        ToolTip.SetTip(WorkspaceControl<TextBlock>("WorkspaceCounts"), summary);
        WorkspaceControl<TabItem>("ThreadsTab").Header = Texts.T("downloads.threadsCount", UrlList.Count);
        WorkspaceControl<TabItem>("BoardsTab").Header = Texts.T("downloads.boardsCount", BoardsList.Count);
        WorkspaceControl<TextBlock>("ThreadsEmptyState").IsVisible = UrlList.Count == 0;
        WorkspaceControl<TextBlock>("BoardsEmptyState").IsVisible = BoardsList.Count == 0;
        WorkspaceControl<Button>("SettingsNavigationButton").IsEnabled = WorkspaceControl<MenuItem>("SettingsMenuItem").IsEnabled;
        WorkspaceControl<Button>("DownloadsNavigationButton").IsEnabled = WorkspaceControl<MenuItem>("SettingsMenuItem").IsEnabled;
        WorkspaceControl<Button>("CopyAllUrlsButton").IsEnabled = !_isClosing && (UrlList.Count > 0 || BoardsList.Count > 0);
        WorkspaceControl<Button>("DetailsToggleButton").Content = Texts.T(WorkspaceControl<Border>("WorkspaceInspector").IsVisible ? "action.hideDetails" : "action.showDetails");
        string stateKey = _isClosing ? "shell.stopping" : _importing ? "shell.importing" : _session.State switch
        {
            DesktopSessionState.Ready => "shell.ready",
            DesktopSessionState.RecoveryFailed => "shell.recoveryFailed",
            DesktopSessionState.Stopping or DesktopSessionState.Stopped => "shell.stopping",
            _ => "shell.starting"
        };
        WorkspaceControl<TextBlock>("WorkspaceStatus").Text = Texts.T(stateKey);
        ToolTip.SetTip(WorkspaceControl<TextBlock>("WorkspaceStatus"), _session.StartupError);
        RefreshWorkspaceInspector();
    }

    private object? GetWorkspaceSelection()
    {
        if (WorkspaceControl<TabControl>("DownloadsTabs").SelectedIndex == 1)
        {
            BoardRow? board = WorkspaceControl<DataGrid>("BoardsDataGrid").SelectedItem as BoardRow;
            return board != null && BoardsList.Contains(board) ? board : null;
        }
        ThreadRow? thread = WorkspaceControl<DataGrid>("ThreadsDataGrid").SelectedItem as ThreadRow;
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
        object? selected = GetWorkspaceSelection();
        bool hasSelection = selected != null;
        WorkspaceControl<TextBlock>("InspectorEmptyState").IsVisible = !hasSelection;
        WorkspaceControl<StackPanel>("InspectorSelection").IsVisible = hasSelection;
        if (selected == null) return;

        bool isThread = selected is ThreadRow;
        string title;
        string source;
        string status;
        string url;
        string directory;
        string error;
        if (selected is ThreadRow thread)
        {
            title = string.IsNullOrWhiteSpace(thread.Title) ? "#" + thread.ThreadId : thread.Title;
            source = thread.HostName + " · " + thread.BoardName + " · #" + thread.ThreadId;
            status = thread.Status;
            url = thread.Url;
            directory = thread.DownloadDirectory;
            error = thread.ErrorDetails;
            WorkspaceControl<SelectableTextBlock>("InspectorProgress").Text = thread.Progress;
            WorkspaceControl<ProgressBar>("InspectorProgressBar").Value = thread.Completion;
            WorkspaceControl<TextBlock>("InspectorQueue").Text = Texts.T("detail.queue") + ": " + thread.Queued + " / " + thread.Active;
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
        WorkspaceControl<TextBlock>("InspectorKind").Text = Texts.T(isThread ? "detail.thread" : "detail.board");
        WorkspaceControl<SelectableTextBlock>("InspectorTitle").Text = title;
        WorkspaceControl<SelectableTextBlock>("InspectorSource").Text = source;
        WorkspaceControl<SelectableTextBlock>("InspectorStatus").Text = status;
        WorkspaceControl<SelectableTextBlock>("InspectorUrl").Text = url;
        WorkspaceControl<SelectableTextBlock>("InspectorDirectory").Text = directory;
        WorkspaceControl<StackPanel>("InspectorMediaSection").IsVisible = isThread;
        SelectableTextBlock errors = WorkspaceControl<SelectableTextBlock>("InspectorError");
        errors.Text = string.IsNullOrWhiteSpace(error) ? Texts.T("detail.noError") : error;
        errors.Classes.Set("error", !string.IsNullOrWhiteSpace(error));
        WorkspaceControl<StackPanel>("InspectorErrorSection").IsVisible = !string.IsNullOrWhiteSpace(error);
        WorkspaceControl<TextBlock>("InspectorRemoveHint").Text = Texts.T(isThread ? "detail.removeHint" : "detail.boardRemoveHint");
        foreach (string name in new[] { "InspectorOpenFolderButton", "InspectorOpenBrowserButton", "InspectorCopyUrlButton" })
            WorkspaceControl<Button>(name).IsEnabled = !_isClosing;
        WorkspaceControl<Button>("InspectorRemoveButton").IsEnabled = _restoreCompleted && !_isClosing;
    }

    private void UpdateWorkspaceLayout()
    {
        if (!_workspaceInitialized || _updatingWorkspaceLayout) return;
        _updatingWorkspaceLayout = true;
        try
        {
            Grid content = WorkspaceControl<Grid>("WorkspaceContentGrid");
            Border inspector = WorkspaceControl<Border>("WorkspaceInspector");
            GridSplitter splitter = WorkspaceControl<GridSplitter>("WorkspaceInspectorSplitter");
            bool narrow = Bounds.Width > 0 && Bounds.Width < 1120;
            bool shortWindow = Bounds.Height > 0 && Bounds.Height < 760;
            bool compactIntake = Bounds.Height > 0 && Bounds.Height < 840;
            bool barebones = _config.WorkspaceMode == "Barebones";
            bool reducedChrome = barebones || shortWindow;
            WorkspaceControl<Grid>("WorkspaceHeading").IsVisible = !reducedChrome;
            WorkspaceControl<TextBlock>("UrlInputHint").IsVisible = !reducedChrome && _config.WorkspaceMode == "Advanced";
            // Both pages and the mode selector remain reachable in compact windows.
            WorkspaceControl<StackPanel>("WorkspaceNavigationButtons").IsVisible = true;
            WorkspaceControl<Border>("WorkspaceNavigation").Padding = new Thickness(12, reducedChrome ? 2 : 5);
            WorkspaceControl<Grid>("WorkspaceRoot").RowDefinitions[3].Height = new GridLength(reducedChrome ? 40 : 52);
            WorkspaceControl<Grid>("WorkspaceBody").RowSpacing = reducedChrome ? 8 : 10;
            WorkspaceControl<Grid>("WorkspaceBody").Margin = barebones ? new Thickness(12)
                : shortWindow ? new Thickness(16, 12) : new Thickness(20);
            // Keep intake and the latest results usable without allowing large pasted
            // batches to push the table's header and first complete row offscreen.
            WorkspaceControl<TextBox>("UrlInput").MaxHeight = compactIntake ? 40 : 72;
            WorkspaceControl<Border>("WorkspaceIntakeCard").Padding = compactIntake ? new Thickness(8) : new Thickness(12, 10);
            WorkspaceControl<StackPanel>("AdmissionResultRows").Margin = new Thickness(0, compactIntake ? 4 : 6, 0, 0);
            WorkspaceControl<ScrollViewer>("AdmissionResultsScroll").MaxHeight = compactIntake ? 40 : 140;
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
            WorkspaceControl<Button>("DetailsToggleButton").Content = Texts.T(visible ? "action.hideDetails" : "action.showDetails");
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

    private void OnOpenWorkspaceDetailsClick(object? sender, RoutedEventArgs e) => OpenWorkspaceDetails();
    private void OnWorkspaceSelectionChanged(object? sender, SelectionChangedEventArgs e) => RefreshWorkspace();
    private void OnWorkspaceTabChanged(object? sender, SelectionChangedEventArgs e) => RefreshWorkspace();
    private void OnDownloadsNavigationClick(object? sender, RoutedEventArgs e)
    {
        if (NavigateWorkspace(false)) WorkspaceControl<TextBox>("UrlInput").Focus();
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
