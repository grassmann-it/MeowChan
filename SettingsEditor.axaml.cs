using Avalonia;
using Avalonia.Controls;
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

namespace MeowChan
{
    public partial class SettingsEditor : UserControl, INotifyPropertyChanged
    {
        private readonly Config _config;
        private readonly Func<Config, Task>? _applySettings;
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

        public IReadOnlyList<SettingsChoice> ThemeChoices { get; private set; } = Array.Empty<SettingsChoice>();
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
                if (!_isClosed) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEditable)));
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
        public SettingsEditor(Config config, Func<Config, Task>? applySettings = null)
        {
            ArgumentNullException.ThrowIfNull(config);
            InitializeComponent();
            _config = config;
            _applySettings = applySettings;
            DownloadPath = config.DownloadPath;
            RecheckTimer = config.RecheckTimer;
            MaxParallelDownloads = config.MaxParallelDownloads;
            SaveUrlsOnExit = config.SaveUrlsOnExit;
            ShowBoardAdmissionWizard = config.ShowBoardAdmissionWizard;
            MinimizeToTray = config.MinimizeToTray;
            CloseToTray = config.CloseToTray;
            BandwidthLimiterMB = config.BandwidthLimiter / (1024m * 1024m);
            CompactDensity = config.CompactDensity;
            RefreshChoices(config.Theme, config.WorkspaceMode, config.Language);
            DataContext = this;
            ResetNumber(RecheckTimerInput, RecheckTimer!.Value);
            ResetNumber(ParallelDownloadsInput, MaxParallelDownloads!.Value);
            ResetNumber(BandwidthInput, BandwidthLimiterMB!.Value);
            Texts.Changed += OnLanguageChanged;
        }

        public string SettingsTitle => Texts.T("settings.title");
        public string SettingsSubtitle => Texts.T("settings.subtitle");
        public string DownloadsTitle => Texts.T("settings.downloads");
        public string InterfaceTitle => Texts.T("settings.interface");
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
        public string CancelLabel => Texts.T("settings.cancel");
        public string SupportedSources => Texts.T("settings.supportedSources");

        public SettingsChoice? SelectedTheme
        {
            get => _selectedTheme;
            set => SetField(ref _selectedTheme, value, nameof(SelectedTheme));
        }

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
                    SettingsError.BringIntoView();
                }
            }, DispatcherPriority.Loaded);
        }

        public void EndLifetime()
        {
            if (_isClosed) return;
            _isClosed = true;
            Texts.Changed -= OnLanguageChanged;
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
            RefreshChoices(_config.Theme, _config.WorkspaceMode, _config.Language);
            ResetNumber(RecheckTimerInput, RecheckTimer!.Value);
            ResetNumber(ParallelDownloadsInput, MaxParallelDownloads!.Value);
            ResetNumber(BandwidthInput, BandwidthLimiterMB!.Value);
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
            RevealErrorAfterLayout();
        }

        private void OnLanguageChanged(object? sender, EventArgs e)
        {
            void Refresh()
            {
                if (_isClosed)
                    return;
                RefreshChoices(SelectedTheme?.Value ?? _config.Theme,
                    SelectedWorkspaceMode?.Value ?? _config.WorkspaceMode,
                    SelectedLanguage?.Code ?? _config.Language);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
            }

            if (Dispatcher.UIThread.CheckAccess())
                Refresh();
            else
                Dispatcher.UIThread.Post(Refresh);
        }

        private void RefreshChoices(string theme, string mode, string language)
        {
            ThemeChoices = new SettingsChoice[]
            {
                new SettingsChoice("Dark", Texts.T("theme.dark")),
                new SettingsChoice("Light", Texts.T("theme.light"))
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
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WorkspaceChoices)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LanguageChoices)));
            SelectedTheme = ThemeChoices.First(item => item.Value == theme);
            SelectedWorkspaceMode = WorkspaceChoices.First(item => item.Value == mode);
            SelectedLanguage = LanguageChoices.First(item => string.Equals(item.Code, language, StringComparison.OrdinalIgnoreCase));
        }

        private void OnCancelButtonClick(object? sender, RoutedEventArgs e)
        {
            if (IsBusy || !IsAvailable || _isClosed) return;
            CancelChanges();
            Cancelled?.Invoke(this, EventArgs.Empty);
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
                if (HasInvalidText(RecheckTimerInput) || HasInvalidText(BandwidthInput) || HasInvalidText(ParallelDownloadsInput) ||
                    DataValidationErrors.GetHasErrors(RecheckTimerInput) ||
                    DataValidationErrors.GetHasErrors(BandwidthInput) ||
                    DataValidationErrors.GetHasErrors(ParallelDownloadsInput))
                {
                    throw new ArgumentException(Texts.T("settings.invalidNumbers"));
                }

                Config candidate = _config.Clone();
                candidate.DownloadPath = DownloadPath;
                candidate.RecheckTimer = GetPositiveInteger(RecheckTimer, Texts.T("settings.recheckLabel"));
                candidate.MaxParallelDownloads = GetPositiveInteger(MaxParallelDownloads, Texts.T("settings.parallelLabel"));
                candidate.SaveUrlsOnExit = SaveUrlsOnExit;
                candidate.ShowBoardAdmissionWizard = ShowBoardAdmissionWizard;
                candidate.MinimizeToTray = MinimizeToTray;
                candidate.CloseToTray = CloseToTray;
                candidate.Theme = SelectedTheme?.Value ?? _config.Theme;
                candidate.WorkspaceMode = SelectedWorkspaceMode?.Value ?? _config.WorkspaceMode;
                candidate.Language = SelectedLanguage?.Code ?? _config.Language;
                candidate.CompactDensity = CompactDensity;
                if (!BandwidthLimiterMB.HasValue || BandwidthLimiterMB.Value <= 0)
                {
                    throw new ArgumentException(Texts.T("settings.bandwidthPositive"));
                }

                candidate.BandwidthLimiter = checked((long)(BandwidthLimiterMB.Value * 1024m * 1024m));
                candidate.Validate();
                if (_applySettings != null)
                {
                    await _applySettings(candidate);
                }
                else
                {
                    candidate.SaveConfig();
                }

                _config.CopyFrom(candidate);
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
                ErrorMessage = Texts.T("settings.saveFailed", exception.Message);
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
            RefreshChoices(_config.Theme, _config.WorkspaceMode, _config.Language);
            ResetNumber(RecheckTimerInput, RecheckTimer!.Value);
            ResetNumber(ParallelDownloadsInput, MaxParallelDownloads!.Value);
            ResetNumber(BandwidthInput, BandwidthLimiterMB!.Value);
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
