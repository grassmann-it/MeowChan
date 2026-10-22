using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Updates;

namespace MeowChan
{
    public partial class SettingsEditor : UserControl, INotifyPropertyChanged
    {
        public static IMultiValueConverter NumericActionNameConverter { get; } =
            new FuncMultiValueConverter<object?, string>((IReadOnlyList<object?> values) =>
                values.Count == 2 && values[0] is string format && values[1] is string field
                    ? string.Format(CultureInfo.CurrentCulture, format, field) : string.Empty);

        private readonly Config _config;
        private readonly Func<Config, Task>? _applySettings;
        private readonly Func<Task>? _forgetReadingPosition;
        private readonly bool _closeOnDiscard;
        private bool _rememberReadingPosition;
        private string _downloadPath = string.Empty;
        private decimal? _recheckTimer;
        private decimal? _maxParallelDownloads;
        private decimal? _bandwidthLimiterMB;
        private bool _saveUrlsOnExit;
        private bool _showBoardAdmissionWizard;
        private bool _minimizeToTray;
        private bool _closeToTray;
        private bool _isBusy;
        private bool _isClosed;
        private bool _errorRevealPending;
        private string? _errorMessage;
        private SettingsChoice? _selectedTheme;
        private SettingsChoice? _selectedWorkspaceMode;
        private LanguageInfo? _selectedLanguage;
        private bool _compactDensity;
        private bool _initialCompactDensity;
        private string _initialReadingFontSize = "Normal";
        private string _initialWorkspaceMode = "Barebones";
        private SettingsChoice? _selectedReadingFontSize;
        private bool _videoAutoplay;
        private bool _videoStartMuted;
        private int _videoVolume;
        private bool _showShortcutHelp;
        private bool _automaticUpdateChecks;
        private bool _includePrereleaseUpdates;
        private decimal? _minimumFreeSpaceGiB;
        private UpdateSession? _updates;
        internal UpdateSession? Updates
        {
            get => _updates;
            set
            {
                if (_updates != null) _updates.Changed -= OnUpdateStateChanged;
                _updates = value;
                if (_updates != null) _updates.Changed += OnUpdateStateChanged;
                OnUpdateStateChanged();
            }
        }

        private void OnUpdateStateChanged() => Dispatcher.UIThread.Post(() =>
        {
            if (_isClosed) return;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UpdatesDisabled)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UpdatesEnabled)));
        });
        private decimal? _productionWorkers;
        private decimal? _workerIdleMinutes;
        private decimal? _sourceHeapMiB;
        private decimal? _filterHeapMiB;


        public IReadOnlyList<SettingsChoice> ThemeChoices { get; private set; } = Array.Empty<SettingsChoice>();
        public IReadOnlyList<SettingsChoice> ReadingFontSizeChoices { get; private set; } = Array.Empty<SettingsChoice>();
        public IReadOnlyList<SettingsChoice> WorkspaceChoices { get; private set; } = Array.Empty<SettingsChoice>();
        public IReadOnlyList<LanguageInfo> LanguageChoices { get; private set; } = Array.Empty<LanguageInfo>();

        public new event PropertyChangedEventHandler? PropertyChanged;
        public event EventHandler? BusyChanged;
        public event EventHandler? Saved;
        public event EventHandler? Cancelled;
        private bool _isAvailable = true;
        private string? _statusMessage;

        public bool IsAvailable
        {
            get => _isAvailable;
            set
            {
                SetField(ref _isAvailable, value, nameof(IsAvailable));
                if (!_isClosed)
                {
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEditable)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanForgetReadingPosition)));
                }
            }
        }
        public bool IsEditable => IsAvailable && !IsBusy;
        public string? StatusMessage
        {
            get => _statusMessage;
            private set => SetField(ref _statusMessage, value, nameof(StatusMessage));
        }

        public SettingsEditor() : this(new Config())
        {
        }

        /// <param name="applySettings">Commits the validated candidate and applies it to the running session. The compatibility host can omit this to save an isolated profile directly.</param>
        public SettingsEditor(Config config, Func<Config, Task>? applySettings = null, Func<Task>? forgetReadingPosition = null, bool closeOnDiscard = false)
        {
            ArgumentNullException.ThrowIfNull(config);
            InitializeComponent();
            _config = config;
            _initialCompactDensity = config.CompactDensity;
            _initialReadingFontSize = config.ReadingFontSize;
            _initialWorkspaceMode = config.WorkspaceMode;
            _applySettings = applySettings;
            _forgetReadingPosition = forgetReadingPosition ?? (applySettings == null
                ? () => Task.Run(config.ForgetReadingPositionFile) : null);
            _closeOnDiscard = closeOnDiscard;
            DownloadPath = config.DownloadPath;
            RecheckTimer = config.RecheckTimer;
            MaxParallelDownloads = config.MaxParallelDownloads;
            SaveUrlsOnExit = config.SaveUrlsOnExit;
            ShowBoardAdmissionWizard = config.ShowBoardAdmissionWizard;
            MinimizeToTray = config.MinimizeToTray;
            CloseToTray = config.CloseToTray;
            BandwidthLimiterMB = config.BandwidthLimiter / (1024m * 1024m);
            CompactDensity = config.CompactDensity;
            VideoAutoplay = config.VideoAutoplay;
            VideoStartMuted = config.VideoStartMuted;
            VideoVolume = config.VideoVolume;
            ShowShortcutHelp = config.ShowShortcutHelp;
            RememberReadingPosition = config.RememberReadingPosition;
            AutomaticUpdateChecks = config.AutomaticUpdateChecks;
            IncludePrereleaseUpdates = config.IncludePrereleaseUpdates;
            MinimumFreeSpaceGiB = config.MinimumFreeSpaceGiB;
            ProductionWorkers = config.PluginMaximumProductionWorkers;
            WorkerIdleMinutes = config.PluginWorkerIdleMinutes;
            SourceHeapMiB = config.PluginSourceHeapMiB;
            FilterHeapMiB = config.PluginFilterHeapMiB;

            RefreshChoices(config.Theme, config.WorkspaceMode, config.Language);
            DataContext = this;
            ApplyCommandIcons();
            ResetNumber(RecheckTimerInput, RecheckTimer!.Value);
            ResetNumber(ParallelDownloadsInput, MaxParallelDownloads!.Value);
            ResetNumber(BandwidthInput, BandwidthLimiterMB!.Value);
            ResetNumber(ReserveInput, MinimumFreeSpaceGiB!.Value);
            ResetNumber(ProductionWorkersInput, ProductionWorkers!.Value);
            ResetNumber(WorkerIdleMinutesInput, WorkerIdleMinutes!.Value);
            ResetNumber(SourceHeapMiBInput, SourceHeapMiB!.Value);
            ResetNumber(FilterHeapMiBInput, FilterHeapMiB!.Value);

            App.ControlLanguageChanging += OnControlLanguageChanging;
            App.ControlLanguageChanged += OnControlLanguageChanged;
            Texts.Changed += OnLanguageChanged;
        }

        public string UpdatesTitle => Texts.T("updates.title");
        public string AutomaticUpdateChecksLabel => Texts.T("updates.automatic");
        public string IncludePrereleaseUpdatesLabel => Texts.T("updates.prereleases");
        public string UpdatePrivacyHint => Texts.T("updates.privacy");
        public string UpdateDisabledReason => Texts.T("updates.disabled");
        public bool UpdatesDisabled => Updates?.View.EnvironmentDisabled ?? Environment.GetEnvironmentVariable("MEOWCHAN_DISABLE_UPDATE_CHECK") == "1";
        public bool UpdatesEnabled => !UpdatesDisabled;
        public string ReserveLabel => Texts.T("budgets.reserve");
        public string ReserveHint => Texts.T("budgets.reserveHint");
        public string AdvancedBudgetsLabel => Texts.T("budgets.advanced");
        public string BudgetRestartHint => Texts.T("budgets.nextSession");
        public bool AutomaticUpdateChecks { get => _automaticUpdateChecks; set => SetField(ref _automaticUpdateChecks, value, nameof(AutomaticUpdateChecks)); }
        public bool IncludePrereleaseUpdates { get => _includePrereleaseUpdates; set => SetField(ref _includePrereleaseUpdates, value, nameof(IncludePrereleaseUpdates)); }
        public decimal? MinimumFreeSpaceGiB { get => _minimumFreeSpaceGiB; set => SetField(ref _minimumFreeSpaceGiB, value, nameof(MinimumFreeSpaceGiB)); }
        public string ProductionWorkersLabel => Texts.T("budgets.workers");
        public decimal? ProductionWorkers { get => _productionWorkers; set => SetField(ref _productionWorkers, value, nameof(ProductionWorkers)); }
        public string WorkerIdleMinutesLabel => Texts.T("budgets.idle");
        public decimal? WorkerIdleMinutes { get => _workerIdleMinutes; set => SetField(ref _workerIdleMinutes, value, nameof(WorkerIdleMinutes)); }
        public string SourceHeapMiBLabel => Texts.T("budgets.sourceHeap");
        public decimal? SourceHeapMiB { get => _sourceHeapMiB; set => SetField(ref _sourceHeapMiB, value, nameof(SourceHeapMiB)); }
        public string FilterHeapMiBLabel => Texts.T("budgets.filterHeap");
        public decimal? FilterHeapMiB { get => _filterHeapMiB; set => SetField(ref _filterHeapMiB, value, nameof(FilterHeapMiB)); }

        public string SettingsTitle => Texts.T("settings.title");
        public string SettingsSubtitle => Texts.T("settings.subtitle");
        public string DownloadsTitle => Texts.T("settings.downloads");
        public string InterfaceTitle => Texts.T("settings.interface");
        public string ReadingMediaTitle => Texts.T("r08d.settings.readingMedia");
        public string ReadingFontSizeLabel => Texts.T("r08d.settings.readingFontSize");
        public string VideoAutoplayLabel => Texts.T("r08d.settings.videoAutoplay");
        public string VideoStartMutedLabel => Texts.T("r08d.settings.videoStartMuted");
        public string VideoVolumeLabel => Texts.T("r08d.settings.videoVolume");
        public string ShowShortcutHelpLabel => Texts.T("r08d.settings.shortcutHelp");
        public string AboutLabel => Texts.T("r08d.settings.about");
        public string BehaviorTitle => Texts.T("settings.behavior");
        public string DownloadPathLabel => Texts.T("settings.downloadPath");
        public string BrowseLabel => Texts.T("settings.browse");
        public string PathHint => Texts.T("settings.pathHint");
        public string RecheckLabel => Texts.T("settings.recheckSeconds");
        public string BandwidthLabel => Texts.T("settings.bandwidth");
        public string ParallelLabel => Texts.T("settings.parallelDownloads");
        public string RestoreSessionLabel => Texts.T("settings.restoreSession");
        public string BoardAdmissionWizardLabel => Texts.T("settings.boardAdmissionWizard");
        public string CloseToTrayLabel => Texts.T("settings.closeToTray");
        public string MinimizeToTrayLabel => Texts.T("settings.minimizeToTray");
        public string ThemeLabel => Texts.T("settings.theme");
        public string LanguageLabel => Texts.T("settings.language");
        public string LanguagePacksLabel => Texts.T("settings.languagePacksTitle");
        public string LanguagePacksHint => Texts.T("settings.languagePacks");
        public string WorkspaceModeLabel => Texts.T("settings.viewMode");
        public string DensityLabel => Texts.T("settings.density");
        public string CompactLabel => Texts.T("density.compact");
        public string ModeHint => Texts.T("settings.modeHint");
        public string SaveLabel => Texts.T("settings.save");
        public string CancelLabel => Texts.T(_closeOnDiscard ? "settings.cancel" : "rules.discard");
        public string RememberReadingPositionLabel => Texts.T("resume.remember");
        public string ReadingPositionHint => Texts.T("resume.hint");
        public string ForgetReadingPositionLabel => Texts.T("resume.forget");
        public string SecondsHelp => Texts.T("settings.unitSeconds");
        public string BandwidthHelp => Texts.T("settings.unitBandwidth");
        public string VolumeHelp => Texts.T("settings.unitPercent");
        public string IncreaseNumericValueFormat => Texts.T("settings.increaseValue");
        public string DecreaseNumericValueFormat => Texts.T("settings.decreaseValue");
        public bool CanForgetReadingPosition => _forgetReadingPosition != null && IsEditable;
        public bool RememberReadingPosition { get => _rememberReadingPosition; set => SetField(ref _rememberReadingPosition, value, nameof(RememberReadingPosition)); }
        public string SupportedSources => Texts.T("settings.supportedSources");

        public SettingsChoice? SelectedTheme
        {
            get => _selectedTheme;
            set => SetField(ref _selectedTheme, value, nameof(SelectedTheme));
        }

        public SettingsChoice? SelectedReadingFontSize
        {
            get => _selectedReadingFontSize;
            set => SetField(ref _selectedReadingFontSize, value, nameof(SelectedReadingFontSize));
        }
        public bool VideoAutoplay { get => _videoAutoplay; set => SetField(ref _videoAutoplay, value, nameof(VideoAutoplay)); }
        public bool VideoStartMuted { get => _videoStartMuted; set => SetField(ref _videoStartMuted, value, nameof(VideoStartMuted)); }
        public int VideoVolume { get => _videoVolume; set => SetField(ref _videoVolume, value, nameof(VideoVolume)); }
        public bool ShowShortcutHelp { get => _showShortcutHelp; set => SetField(ref _showShortcutHelp, value, nameof(ShowShortcutHelp)); }

        public SettingsChoice? SelectedWorkspaceMode
        {
            get => _selectedWorkspaceMode;
            set => SetField(ref _selectedWorkspaceMode, value, nameof(SelectedWorkspaceMode));
        }

        public LanguageInfo? SelectedLanguage
        {
            get => _selectedLanguage;
            set => SetField(ref _selectedLanguage, value, nameof(SelectedLanguage));
        }

        public bool CompactDensity
        {
            get => _compactDensity;
            set => SetField(ref _compactDensity, value, nameof(CompactDensity));
        }

        public string DownloadPath
        {
            get => _downloadPath;
            set => SetField(ref _downloadPath, value, nameof(DownloadPath));
        }

        public decimal? RecheckTimer
        {
            get => _recheckTimer;
            set => SetField(ref _recheckTimer, value, nameof(RecheckTimer));
        }

        public decimal? MaxParallelDownloads
        {
            get => _maxParallelDownloads;
            set => SetField(ref _maxParallelDownloads, value, nameof(MaxParallelDownloads));
        }

        public decimal? BandwidthLimiterMB
        {
            get => _bandwidthLimiterMB;
            set => SetField(ref _bandwidthLimiterMB, value, nameof(BandwidthLimiterMB));
        }

        public bool SaveUrlsOnExit
        {
            get => _saveUrlsOnExit;
            set => SetField(ref _saveUrlsOnExit, value, nameof(SaveUrlsOnExit));
        }

        public bool ShowBoardAdmissionWizard
        {
            get => _showBoardAdmissionWizard;
            set => SetField(ref _showBoardAdmissionWizard, value, nameof(ShowBoardAdmissionWizard));
        }

        public bool CloseToTray
        {
            get => _closeToTray;
            set => SetField(ref _closeToTray, value, nameof(CloseToTray));
        }

        public bool MinimizeToTray
        {
            get => _minimizeToTray;
            set => SetField(ref _minimizeToTray, value, nameof(MinimizeToTray));
        }

        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (_isBusy == value) return;
                SetField(ref _isBusy, value, nameof(IsBusy));
                if (!_isClosed)
                {
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEditable)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanForgetReadingPosition)));
                    BusyChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public string? ErrorMessage
        {
            get => _errorMessage;
            private set
            {
                if (_isClosed)
                    return;
                if (value == null) ClearFieldErrors();
                SetField(ref _errorMessage, value, nameof(ErrorMessage));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasError)));
                RevealErrorAfterLayout();
            }
        }

        public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

        private void OnSettingsScrollChanged(object? sender, ScrollChangedEventArgs e)
        {
            // Native window limits can change the viewport after the initial layout.
            // Ignore offset-only changes so users can still scroll away from an error.
            if (ReferenceEquals(e.Source, SettingsScrollViewer) &&
                (e.ViewportDelta.X != 0 || e.ViewportDelta.Y != 0 || e.ExtentDelta.X != 0 || e.ExtentDelta.Y != 0))
            {
                RevealErrorAfterLayout();
            }
        }

        private void RevealErrorAfterLayout()
        {
            if (_isClosed || !IsVisible || !HasError || _errorRevealPending)
                return;

            _errorRevealPending = true;
            Dispatcher.UIThread.Post(() =>
            {
                _errorRevealPending = false;
                if (!_isClosed && IsVisible && HasError && SettingsError.IsAttachedToVisualTree() && SettingsError.Bounds.Height > 0)
                {
                    if (_firstInvalidInput != null)
                        (_firstInvalidInput.Parent as Control ?? _firstInvalidInput).BringIntoView();
                    else
                        SettingsError.BringIntoView();
                }
            }, DispatcherPriority.Loaded);
        }

        public void EndLifetime()
        {
            if (_isClosed) return;
            _isClosed = true;
            Texts.Changed -= OnLanguageChanged;
            App.ControlLanguageChanging -= OnControlLanguageChanging;
            App.ControlLanguageChanged -= OnControlLanguageChanged;
            _controlLanguageDrafts = null;
            if (_updates != null) _updates.Changed -= OnUpdateStateChanged;
            SettingsScrollViewer.ScrollChanged -= OnSettingsScrollChanged;
        }

        internal void RefreshConfirmedAppearance(bool theme, bool workspaceMode)
        {
            if (_isClosed) return;
            if (theme) SelectedTheme = ThemeChoices.First(item => item.Value == _config.Theme);
            if (workspaceMode) SelectedWorkspaceMode = WorkspaceChoices.First(item => item.Value == _config.WorkspaceMode);
        }

        internal void RevealCurrentError() => RevealErrorAfterLayout();

        internal void CancelChanges()
        {
            if (IsBusy || !IsAvailable || _isClosed) return;
            DownloadPath = _config.DownloadPath;
            RecheckTimer = _config.RecheckTimer;
            MaxParallelDownloads = _config.MaxParallelDownloads;
            SaveUrlsOnExit = _config.SaveUrlsOnExit;
            ShowBoardAdmissionWizard = _config.ShowBoardAdmissionWizard;
            MinimizeToTray = _config.MinimizeToTray;
            CloseToTray = _config.CloseToTray;
            BandwidthLimiterMB = _config.BandwidthLimiter / (1024m * 1024m);
            CompactDensity = _config.CompactDensity;
            VideoAutoplay = _config.VideoAutoplay;
            VideoStartMuted = _config.VideoStartMuted;
            VideoVolume = _config.VideoVolume;
            ShowShortcutHelp = _config.ShowShortcutHelp;
            RememberReadingPosition = _config.RememberReadingPosition;
            AutomaticUpdateChecks = _config.AutomaticUpdateChecks;
            IncludePrereleaseUpdates = _config.IncludePrereleaseUpdates;
            MinimumFreeSpaceGiB = _config.MinimumFreeSpaceGiB;
            ProductionWorkers = _config.PluginMaximumProductionWorkers;
            WorkerIdleMinutes = _config.PluginWorkerIdleMinutes;
            SourceHeapMiB = _config.PluginSourceHeapMiB;
            FilterHeapMiB = _config.PluginFilterHeapMiB;

            RefreshChoices(_config.Theme, _config.WorkspaceMode, _config.Language);
            ResetNumber(RecheckTimerInput, RecheckTimer!.Value);
            ResetNumber(ParallelDownloadsInput, MaxParallelDownloads!.Value);
            ResetNumber(BandwidthInput, BandwidthLimiterMB!.Value);
            ResetNumber(ReserveInput, MinimumFreeSpaceGiB!.Value);
            ResetNumber(ProductionWorkersInput, ProductionWorkers!.Value);
            ResetNumber(WorkerIdleMinutesInput, WorkerIdleMinutes!.Value);
            ResetNumber(SourceHeapMiBInput, SourceHeapMiB!.Value);
            ResetNumber(FilterHeapMiBInput, FilterHeapMiB!.Value);

            ErrorMessage = null;
            StatusMessage = null;
        }

        private static void ResetNumber(NumericUpDown input, decimal value)
        {
            input.Value = value;
            input.Text = value.ToString(CultureInfo.CurrentCulture);
        }

        private void OnEditorSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            double width = Math.Min(880, Math.Max(0, e.NewSize.Width - 60));
            SettingsContent.Width = width;
            SettingsHeader.Width = width;
            bool narrow = width < 700;
            foreach (Grid row in SettingsContent.GetVisualDescendants().OfType<Grid>().Where(row => row.Classes.Contains("settings-row")))
            {
                row.ColumnDefinitions[1].Width = new GridLength(narrow ? 0 : 280);
                row.RowSpacing = narrow ? 6 : 0;
                row.ColumnSpacing = narrow ? 0 : 32;
                Control editor = row.Children[1];
                Grid.SetColumn(editor, narrow ? 0 : 1);
                Grid.SetRow(editor, narrow ? 1 : 0);
                row.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            }
            Grid.SetColumn(SettingsFooterActions, narrow ? 0 : 1);
            Grid.SetRow(SettingsFooterActions, narrow ? 1 : 0);
            Grid.SetColumnSpan(SettingsFooterActions, narrow ? 2 : 1);
            Grid.SetColumnSpan(SettingsFooter.Children[0], narrow ? 2 : 1);
            RevealErrorAfterLayout();
        }

        private void OnLanguageChanged(object? sender, EventArgs e)
        {
            void Refresh()
            {
                if (_isClosed)
                    return;
                // Numeric controls keep malformed text separately from their last Value.
                // Refreshing labels must never replace that text with the last valid value.
                var numericDrafts = NumericFields().Select(field => (field.Input, field.Input.Text)).ToArray();
                bool hadFieldError = _firstInvalidInput != null;
                bool pathError = _firstInvalidInput == DownloadPathInput;
                string readingFontSize = SelectedReadingFontSize?.Value ?? _config.ReadingFontSize;
                RefreshChoices(SelectedTheme?.Value ?? _config.Theme,
                    SelectedWorkspaceMode?.Value ?? _config.WorkspaceMode,
                    SelectedLanguage?.Code ?? _config.Language);
                SelectedReadingFontSize = ReadingFontSizeChoices.First(item => item.Value == readingFontSize);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
                foreach (var draft in numericDrafts)
                    if (draft.Input.Text != draft.Text) draft.Input.Text = draft.Text;
                if (hadFieldError)
                {
                    ClearFieldErrors();
                    if (pathError)
                    {
                        DownloadPathInputError.Text = Texts.T("settings.pathInvalid");
                        DownloadPathInputError.IsVisible = true;
                        SetFieldErrorHelp(DownloadPathInput, DownloadPathInputError.Text!);
                        _firstInvalidInput = DownloadPathInput;
                        FinishFieldValidation();
                    }
                    else ValidateNumericDraft();
                }
                ApplyCommandIcons();
            }

            if (Dispatcher.UIThread.CheckAccess())
                Refresh();
            else
                Dispatcher.UIThread.Post(Refresh);
        }

        private void ApplyCommandIcons()
        {
            CommandBindings.ApplyLabeled(BrowseButton, "settings.browse-folder", BrowseLabel);
            CommandBindings.ApplyLabeled(AboutSettingsButton, "navigation.about", AboutLabel);
            CommandBindings.ApplyLabeled(CancelSettingsButton, "settings.discard", CancelLabel);
            CommandBindings.ApplyLabeled(SaveSettingsButton, "settings.save", SaveLabel);
        }

        private void RefreshChoices(string theme, string mode, string language)
        {
            ThemeChoices = new SettingsChoice[]
            {
                new SettingsChoice("Dark", Texts.T("theme.dark")),
                new SettingsChoice("Light", Texts.T("theme.light")),
                new SettingsChoice("System", Texts.T("r08d.settings.systemTheme"))
            };
            ReadingFontSizeChoices = new SettingsChoice[]
            {
                new SettingsChoice("Small", Texts.T("r08d.settings.fontSmall")),
                new SettingsChoice("Normal", Texts.T("r08d.settings.fontNormal")),
                new SettingsChoice("Large", Texts.T("r08d.settings.fontLarge"))
            };
            WorkspaceChoices = new SettingsChoice[]
            {
                new SettingsChoice("Barebones", Texts.T("mode.barebones")),
                new SettingsChoice("Simple", Texts.T("mode.simple")),
                new SettingsChoice("Advanced", Texts.T("mode.advanced"))
            };
            List<LanguageInfo> languages = new List<LanguageInfo>(Texts.Languages);
            if (!languages.Any(item => string.Equals(item.Code, language, StringComparison.OrdinalIgnoreCase)))
                languages.Add(new LanguageInfo(language, Texts.T("settings.unavailableLanguage", language)));
            LanguageChoices = languages;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThemeChoices)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ReadingFontSizeChoices)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WorkspaceChoices)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LanguageChoices)));
            SelectedTheme = ThemeChoices.First(item => item.Value == theme);
            SelectedReadingFontSize = ReadingFontSizeChoices.First(item => item.Value == _config.ReadingFontSize);
            SelectedWorkspaceMode = WorkspaceChoices.First(item => item.Value == mode);
            SelectedLanguage = LanguageChoices.First(item => string.Equals(item.Code, language, StringComparison.OrdinalIgnoreCase));
        }

        private void OnCancelButtonClick(object? sender, RoutedEventArgs e)
        {
            if (IsBusy || !IsAvailable || _isClosed) return;
            CancelChanges();
            Cancelled?.Invoke(this, EventArgs.Empty);
        }

        private async void OnForgetReadingPositionClick(object? sender, RoutedEventArgs e)
        {
            if (!CanForgetReadingPosition || _isClosed) return;
            IsBusy = true;
            ErrorMessage = null;
            StatusMessage = null;
            try
            {
                await _forgetReadingPosition!();
                StatusMessage = Texts.T("resume.forgotten");
            }
            catch (Exception exception) { ErrorMessage = Texts.T("resume.forgetFailed", exception.Message); }
            finally { IsBusy = false; }
        }

        private async void OnAboutButtonClick(object? sender, RoutedEventArgs e)
        {
            if (TopLevel.GetTopLevel(this) is Window owner)
                await new AboutWindow(Updates).ShowDialog(owner);
        }

        private async void OnBrowseButtonClick(object? sender, RoutedEventArgs e)
        {
            if (IsBusy || !IsAvailable || _isClosed)
            {
                return;
            }

            IsBusy = true;
            ErrorMessage = null;
            StatusMessage = null;
            try
            {
                IStorageProvider? storage = TopLevel.GetTopLevel(this)?.StorageProvider;
                if (storage == null || !storage.CanPickFolder)
                {
                    ErrorMessage = Texts.T("settings.pickerUnavailable");
                    return;
                }

                IReadOnlyList<IStorageFolder> folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = Texts.T("settings.pickerTitle"),
                    AllowMultiple = false
                });

                try
                {
                    if (!_isClosed && folders.Count > 0)
                    {
                        string? selectedPath = folders[0].TryGetLocalPath();
                        if (string.IsNullOrWhiteSpace(selectedPath))
                        {
                            ErrorMessage = Texts.T("settings.pickerLocalOnly");
                        }
                        else
                        {
                            DownloadPath = selectedPath;
                        }
                    }
                }
                finally
                {
                    foreach (IStorageFolder folder in folders)
                    {
                        folder.Dispose();
                    }
                }
            }
            catch (Exception exception)
            {
                ErrorMessage = Texts.T("settings.pickerFailed", exception.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async void OnSaveButtonClick(object? sender, RoutedEventArgs e)
        {
            if (IsBusy || !IsAvailable || _isClosed)
            {
                return;
            }

            IsBusy = true;
            ErrorMessage = null;
            StatusMessage = null;
            try
            {
                if (!ValidateNumericDraft())
                    return;

                Config candidate = _config.Clone();
                candidate.DownloadPath = DownloadPath;
                candidate.RecheckTimer = GetPositiveInteger(RecheckTimer, Texts.T("settings.recheckLabel"));
                candidate.MaxParallelDownloads = GetPositiveInteger(MaxParallelDownloads, Texts.T("settings.parallelLabel"));
                candidate.SaveUrlsOnExit = SaveUrlsOnExit;
                candidate.ShowBoardAdmissionWizard = ShowBoardAdmissionWizard;
                candidate.MinimizeToTray = MinimizeToTray;
                candidate.CloseToTray = CloseToTray;
                candidate.Theme = SelectedTheme?.Value ?? _config.Theme;
                candidate.Language = SelectedLanguage?.Code ?? _config.Language;
                candidate.WorkspaceMode = SelectedWorkspaceMode?.Value ?? _config.WorkspaceMode;
                if (CompactDensity != _initialCompactDensity)
                    candidate.CompactDensity = CompactDensity;
                candidate.SettingsBaselineCompactDensity = _initialCompactDensity;
                string selectedReadingFontSize = SelectedReadingFontSize?.Value ?? _config.ReadingFontSize;
                if (selectedReadingFontSize != _initialReadingFontSize)
                    candidate.ReadingFontSize = selectedReadingFontSize;
                candidate.SettingsBaselineReadingFontSize = _initialReadingFontSize;
                candidate.SettingsBaselineWorkspaceMode = _initialWorkspaceMode;
                candidate.VideoAutoplay = VideoAutoplay;
                candidate.VideoStartMuted = VideoStartMuted;
                candidate.VideoVolume = VideoVolume;
                candidate.ShowShortcutHelp = ShowShortcutHelp;
                candidate.RememberReadingPosition = RememberReadingPosition;
                candidate.AutomaticUpdateChecks = AutomaticUpdateChecks;
                candidate.IncludePrereleaseUpdates = IncludePrereleaseUpdates;
                candidate.MinimumFreeSpaceGiB = GetInteger(MinimumFreeSpaceGiB, ReserveLabel, 0, int.MaxValue);
                candidate.PluginMaximumProductionWorkers = GetInteger(ProductionWorkers, ProductionWorkersLabel, 1, 8);
                candidate.PluginWorkerIdleMinutes = GetInteger(WorkerIdleMinutes, WorkerIdleMinutesLabel, 1, 30);
                candidate.PluginSourceHeapMiB = GetInteger(SourceHeapMiB, SourceHeapMiBLabel, 128, 1024);
                candidate.PluginFilterHeapMiB = GetInteger(FilterHeapMiB, FilterHeapMiBLabel, 256, 2048);

                if (!BandwidthLimiterMB.HasValue || BandwidthLimiterMB.Value <= 0)
                {
                    throw new ArgumentException(Texts.T("settings.bandwidthPositive"));
                }

                candidate.BandwidthLimiter = checked((long)(BandwidthLimiterMB.Value * 1024m * 1024m));
                try { candidate.Validate(); }
                catch (ArgumentException exception) when (exception.ParamName == nameof(Config.DownloadPath))
                {
                    DownloadPathInputError.Text = Texts.T("settings.pathInvalid");
                    DownloadPathInputError.IsVisible = true;
                    SetFieldErrorHelp(DownloadPathInput, DownloadPathInputError.Text!);
                    _firstInvalidInput = DownloadPathInput;
                    FinishFieldValidation();
                    return;
                }
                if (_applySettings != null)
                {
                    await _applySettings(candidate);
                }
                else
                {
                    if (!candidate.RememberReadingPosition) await Task.Run(candidate.ForgetReadingPositionFile);
                    candidate.SaveConfig();
                }

                if (_applySettings == null)
                    _config.CopyFrom(candidate);
                else
                    _config.CopySettingsFieldsFrom(candidate);
                _initialCompactDensity = _config.CompactDensity;
                _initialReadingFontSize = _config.ReadingFontSize;
                _initialWorkspaceMode = _config.WorkspaceMode;
                if (!_isClosed)
                {
                    CancelChangesAfterSave();
                    StatusMessage = Texts.T("settings.saved");
                    IsBusy = false;
                    Saved?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (Exception exception)
            {
                ErrorDetails = exception.ToString();
                ErrorMessage = Texts.T("settings.saveProblem");
                StatusMessage = Texts.T("settings.unsaved");
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void CancelChangesAfterSave()
        {
            DownloadPath = _config.DownloadPath;
            RecheckTimer = _config.RecheckTimer;
            MaxParallelDownloads = _config.MaxParallelDownloads;
            BandwidthLimiterMB = _config.BandwidthLimiter / (1024m * 1024m);
            VideoAutoplay = _config.VideoAutoplay;
            VideoStartMuted = _config.VideoStartMuted;
            VideoVolume = _config.VideoVolume;
            ShowShortcutHelp = _config.ShowShortcutHelp;
            RememberReadingPosition = _config.RememberReadingPosition;
            AutomaticUpdateChecks = _config.AutomaticUpdateChecks;
            IncludePrereleaseUpdates = _config.IncludePrereleaseUpdates;
            MinimumFreeSpaceGiB = _config.MinimumFreeSpaceGiB;
            ProductionWorkers = _config.PluginMaximumProductionWorkers;
            WorkerIdleMinutes = _config.PluginWorkerIdleMinutes;
            SourceHeapMiB = _config.PluginSourceHeapMiB;
            FilterHeapMiB = _config.PluginFilterHeapMiB;

            RefreshChoices(_config.Theme, _config.WorkspaceMode, _config.Language);
            ResetNumber(RecheckTimerInput, RecheckTimer!.Value);
            ResetNumber(ParallelDownloadsInput, MaxParallelDownloads!.Value);
            ResetNumber(BandwidthInput, BandwidthLimiterMB!.Value);
            ResetNumber(ReserveInput, MinimumFreeSpaceGiB!.Value);
            ResetNumber(ProductionWorkersInput, ProductionWorkers!.Value);
            ResetNumber(WorkerIdleMinutesInput, WorkerIdleMinutes!.Value);
            ResetNumber(SourceHeapMiBInput, SourceHeapMiB!.Value);
            ResetNumber(FilterHeapMiBInput, FilterHeapMiB!.Value);

        }

        private static int GetInteger(decimal? value, string label, int minimum, int maximum)
        {
            if (!value.HasValue || value < minimum || value > maximum || decimal.Truncate(value.Value) != value.Value)
                throw new ArgumentException(Texts.T("budgets.range", label, minimum, maximum));
            return decimal.ToInt32(value.Value);
        }

        private static bool HasInvalidText(NumericUpDown input) => !decimal.TryParse(input.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out _);

        private static int GetPositiveInteger(decimal? value, string label)
        {
            if (!value.HasValue || value.Value <= 0 || value.Value > int.MaxValue || decimal.Truncate(value.Value) != value.Value)
            {
                throw new ArgumentException(Texts.T("settings.positiveInteger", label));
            }

            return decimal.ToInt32(value.Value);
        }

        private void SetField<T>(ref T field, T value, string propertyName)
        {
            if (!EqualityComparer<T>.Default.Equals(field, value))
            {
                field = value;
                if (!_isClosed)
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }
    }

    public sealed record SettingsChoice(string Value, string Label);
}
