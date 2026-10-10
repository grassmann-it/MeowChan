using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using oChan.Desktop.Localization;

namespace oChan
{
    public partial class SettingsWindow : Window, INotifyPropertyChanged
    {
        private readonly Config _config;
        private readonly Func<Config, Task>? _applySettings;
        private string _downloadPath = string.Empty;
        private decimal? _recheckTimer;
        private decimal? _maxParallelDownloads;
        private decimal? _bandwidthLimiterMB;
        private bool _saveUrlsOnExit;
        private bool _minimizeToTray;
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

        public SettingsWindow() : this(new Config())
        {
        }

        /// <param name="applySettings">Commits the validated candidate and applies it to the running session. When omitted, the dialog saves the candidate itself.</param>
        public SettingsWindow(Config config, Func<Config, Task>? applySettings = null)
        {
            ArgumentNullException.ThrowIfNull(config);
            InitializeComponent();
            _config = config;
            _applySettings = applySettings;
            DownloadPath = config.DownloadPath;
            RecheckTimer = config.RecheckTimer;
            MaxParallelDownloads = config.MaxParallelDownloads;
            SaveUrlsOnExit = config.SaveUrlsOnExit;
            MinimizeToTray = config.MinimizeToTray;
            BandwidthLimiterMB = config.BandwidthLimiter / (1024m * 1024m);
            CompactDensity = config.CompactDensity;
            RefreshChoices(config.Theme, config.WorkspaceMode, config.Language);
            DataContext = this;
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
        public string MinimizeToTrayLabel => Texts.T("settings.minimizeToTray");
        public string ThemeLabel => Texts.T("settings.theme");
        public string LanguageLabel => Texts.T("settings.language");
        public string LanguagePacksHint => Texts.T("settings.languagePacks");
        public string WorkspaceModeLabel => Texts.T("settings.viewMode");
        public string DensityLabel => Texts.T("settings.density");
        public string CompactLabel => Texts.T("density.compact");
        public string ModeHint => Texts.T("settings.modeHint");
        public string SaveLabel => Texts.T("settings.saveClose");
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

        public bool MinimizeToTray
        {
            get => _minimizeToTray;
            set => SetField(ref _minimizeToTray, value, nameof(MinimizeToTray));
        }

        public bool IsBusy
        {
            get => _isBusy;
            private set => SetField(ref _isBusy, value, nameof(IsBusy));
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

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            base.OnClosing(e);
            if (IsBusy && e.CloseReason == WindowCloseReason.WindowClosing)
            {
                e.Cancel = true;
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _isClosed = true;
            Texts.Changed -= OnLanguageChanged;
            SettingsScrollViewer.ScrollChanged -= OnSettingsScrollChanged;
            base.OnClosed(e);
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
                new SettingsChoice("Simple", Texts.T("mode.simple")),
                new SettingsChoice("Advanced", Texts.T("mode.advanced")),
                new SettingsChoice("Barebones", Texts.T("mode.barebones"))
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
            if (!IsBusy)
                Close();
        }

        private async void OnBrowseButtonClick(object? sender, RoutedEventArgs e)
        {
            if (IsBusy)
            {
                return;
            }

            IsBusy = true;
            ErrorMessage = null;
            try
            {
                if (!StorageProvider.CanPickFolder)
                {
                    ErrorMessage = Texts.T("settings.pickerUnavailable");
                    return;
                }

                IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
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

        private async void OnCloseButtonClick(object? sender, RoutedEventArgs e)
        {
            if (IsBusy)
            {
                return;
            }

            IsBusy = true;
            ErrorMessage = null;
            try
            {
                if (DataValidationErrors.GetHasErrors(RecheckTimerInput) ||
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
                candidate.MinimizeToTray = MinimizeToTray;
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
                IsBusy = false;
                if (!_isClosed)
                    Close();
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
