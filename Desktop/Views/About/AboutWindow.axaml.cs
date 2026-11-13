using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Legal;
using MeowChan.Desktop.Updates;
using System.Threading.Tasks;
using MeowChan.Desktop.Localization;
using Serilog;
using System;
using System.Reflection;

namespace MeowChan;

public partial class AboutWindow : Window
{
    private readonly UpdateSession? _updates;
    private readonly Func<string, Task> _openBrowser;
    private bool _updateActionFailed;
    private readonly string _version;
    private readonly OfflineLegalCatalog _legal;
    private bool _closed;
    private AboutActionError? _actionError;
    // The photographer's and the business name are proper names: never translated.
    internal const string PhotoCreditName = "Alexander Huxel · Webentwicklung Huxel";

    public AboutWindow() : this(null) { }

    internal AboutWindow(UpdateSession? updates, Func<string, Task>? openBrowser = null)
    {
        _updates = updates;
        _openBrowser = openBrowser ?? ExternalLauncher.OpenAsync;
        InitializeComponent();
        WindowChrome.Apply(this);
        DialogFit.OnOpen(this);
        AppCaption.Attach(this);
        ThemeLogo.Follow(this);
        // Both cats side by side in either theme; only the window icon follows the theme.
        ThemeLogo.ShowPair(CatLogo, CatLogoDark);
        Opened += (_, _) => { MeowChan.Desktop.Commands.LegacyCommandAdapters.Register(this); MeowChan.Desktop.Commands.CommandBindings.BindExisting(CloseButton, "about.close"); };
        Assembly assembly = typeof(AboutWindow).Assembly;
        string? informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        _version = informationalVersion?.Split('+', 2)[0] ?? assembly.GetName().Version?.ToString() ?? Texts.T("about.unknownVersion");
        _legal = OfflineLegalCatalog.Load(AppContext.BaseDirectory);
        MeowChan.Desktop.Commands.CommandBindings.BindExisting(LicenseList, "about.open-license");
        LicenseList.ItemsSource = _legal.Entries;
        LicenseList.SelectedIndex = _legal.Entries.Count > 0 ? 0 : -1;
        ApplyTexts();
        Texts.Changed += OnTextsChanged;
        if (_updates != null) _updates.Changed += OnUpdatesChanged;
        Closed += (_, _) =>
        {
            _closed = true;
            Texts.Changed -= OnTextsChanged;
            if (_updates != null) _updates.Changed -= OnUpdatesChanged;
        };
    }

    private void ApplyTexts()
    {
        Title = Texts.T("about.title");
        AboutTab.Header = Texts.T("about.tab.about");
        CreditsTab.Header = Texts.T("about.tab.credits");
        LicensesTab.Header = Texts.T("about.tab.licenses");
        AutomationProperties.SetName(CatLogoPair, Texts.T("about.logoDescription"));
        // Each cat is named by tooltip (and a name for tools); the images stay out of the accessibility tree.
        AutomationProperties.SetName(CatLogo, Texts.T("about.logoSimba"));
        ToolTip.SetTip(CatLogo, Texts.T("about.logoSimba"));
        AutomationProperties.SetName(CatLogoDark, Texts.T("about.logoMila"));
        ToolTip.SetTip(CatLogoDark, Texts.T("about.logoMila"));
        VersionText.Text = Texts.T("shell.version", _version);
        CopyVersionButton.Content = Texts.T("about.copyVersion");
        DescriptionText.Text = Texts.T("about.description");
        AuthorText.Text = Texts.T("about.author");
        ProjectLink.Content = Texts.T("about.project");
        AuthorLink.Content = Texts.T("about.authorLink");
        SupportTitle.Text = Texts.T("about.supportTitle");
        SupportDescription.Text = Texts.T("about.supportDescription");
        DonationLink.Content = Texts.T("about.donate");
        HistoryText.Text = Texts.T("about.history");
        VideoCreditsText.Text = Texts.T("about.videoCredits");
        UICreditsText.Text = Texts.T("about.uiCredits");
        IconsText.Text = Texts.T("about.featherCredits");
        PhotoCreditText.Text = Texts.T("about.photoCredit", PhotoCreditName);
        PhotoCreditLink.Content = PhotoCreditName;
        AutomationProperties.SetName(PhotoCreditFlickrLink, Texts.T("about.photoCreditFlickr", PhotoCreditName));
        LicenseText.Text = Texts.T("about.license");
        LicenseSearchBox.PlaceholderText = Texts.T("about.licenseSearch");
        LicenseMetadataExpander.Header = Texts.T("about.sources");
        LicenseHintText.Text = _legal.Error is null ? Texts.T("about.licenseHint") : Texts.T("about.licenseReadFailed", _legal.Error);
        CloseButton.Content = Texts.T("action.close");
        LinkErrorText.Text = _actionError?.Render() ?? string.Empty;
        UpdateSelection();
        RefreshUpdates();
    }

    private void OnTextsChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (!_closed)
            ApplyTexts();
    });

    protected override void OnKeyDown(KeyEventArgs e)
    {
        KeyModifiers searchModifier = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        if (MeowChan.Desktop.Commands.CommandBindings.Matches(e, "input.focus-search"))
        {
            AboutTabs.SelectedIndex = 2;
            LicenseSearchBox.Focus();
            e.Handled = true;
            return;
        }
        if (!e.Handled && MeowChan.Desktop.Commands.CommandBindings.Matches(e, "about.open-license"))
        {
            e.Handled = true;
            if (LicenseList.SelectedItem is OfflineLegalCatalog.Entry)
            { AboutTabs.SelectedIndex = 2; UpdateSelection(); LicenseFullText.Focus(); }
            return;
        }
        MeowChan.Desktop.Commands.CommandBindings.RouteVisibleActions(this, e);
        base.OnKeyDown(e);
    }

    private void OnLicenseSearchChanged(object? sender, TextChangedEventArgs e)
    {
        if (_legal == null)
            return;
        LicenseList.ItemsSource = _legal.Search(LicenseSearchBox.Text);
        LicenseList.SelectedIndex = LicenseList.ItemCount > 0 ? 0 : -1;
        UpdateSelection();
    }

    private void OnLicenseSelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateSelection();

    private void UpdateSelection()
    {
        if (LicenseList.SelectedItem is not OfflineLegalCatalog.Entry entry)
        {
            LicenseMetadataText.Text = "";
            LicenseFullText.Text = "";
            return;
        }
        LicenseMetadataText.Text = $"{entry.Name} · {entry.Version}\n{entry.License}\n{entry.Copyright}\n{Texts.T("about.sources")}: {entry.Source}";
        LicenseFullText.Text = entry.FullText;
        LicenseFullText.CaretIndex = 0;
    }

    private async void OnCopyVersionClick(object? sender, RoutedEventArgs e)
    {
        if (_closed || Clipboard is not { } clipboard) return;
        try
        {
            await clipboard.SetTextAsync(_version);
            if (_closed) return;
            _actionError = null;
            LinkErrorText.Text = string.Empty;
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Could not copy application version.");
            if (_closed) return;
            _actionError = new(null, exception.Message);
            LinkErrorText.Text = _actionError.Render();
        }
    }

    private async void OnLinkClick(object? sender, RoutedEventArgs e)
    {
        if (_closed || sender is not Button button || button.Tag is not string uri)
            return;
        try
        {
            await ExternalLauncher.OpenAsync(uri);
            if (_closed) return;
            _actionError = null;
            LinkErrorText.Text = "";
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Could not open About link {Uri}.", uri);
            if (_closed) return;
            _actionError = new(uri, exception.Message);
            LinkErrorText.Text = _actionError.Render();
        }
    }

    private void OnUpdatesChanged() => Dispatcher.UIThread.Post(() => { if (!_closed) RefreshUpdates(); });

    private void RefreshUpdates()
    {
        UpdateSection.IsVisible = _updates != null;
        if (_updates == null) return;
        UpdateView view = _updates.View;
        UpdatesTitle.Text = Texts.T("updates.title");
        CheckUpdatesButton.Content = Texts.T(view.IsChecking ? "updates.checking" : "updates.check");
        CheckUpdatesButton.IsEnabled = !view.IsChecking && !view.EnvironmentDisabled;
        UpdateStatusText.Text = UpdatePresentation.Status(view);
        UpdateStatusText.IsVisible = !string.IsNullOrEmpty(UpdateStatusText.Text);
        LastUpdateCheckText.Text = view.LastCheckedUtc is { } checkedAt
            ? Texts.T("updates.lastChecked", checkedAt.ToLocalTime().ToString("g", Texts.Culture)) : string.Empty;
        UpdateActions.IsVisible = view.Update != null && !view.EnvironmentDisabled;
        AvailableUpdateText.IsVisible = UpdateActions.IsVisible;
        AvailableUpdateText.Text = view.Update is { } available ? Texts.T("updates.available", available.Version) : string.Empty;
        UpdateActions.IsEnabled = !view.IsChecking;
        UpdateReleaseButton.Content = Texts.T("updates.releaseNotes");
        UpdateDownloadButton.Content = Texts.T("updates.download");
        SkipUpdateButton.Content = Texts.T("updates.skip");
        UpdateBrowserHint.Text = Texts.T("updates.browserOnly");
        UpdatePrivacyText.Text = Texts.T("updates.privacy");
        UpdateActionErrorText.Text = _updateActionFailed ? Texts.T("updates.error.skip") : string.Empty;
    }

    internal async Task CheckUpdatesAsync()
    {
        if (_closed || _updates == null || _updates.View.EnvironmentDisabled || _updates.View.IsChecking) return;
        _updateActionFailed = false;
        try { await _updates.CheckAsync(); }
        catch (OperationCanceledException) { }
        if (!_closed) RefreshUpdates();
    }

    private async void OnCheckUpdatesClick(object? sender, RoutedEventArgs e) => await CheckUpdatesAsync();
    private async void OnUpdateReleaseClick(object? sender, RoutedEventArgs e) => await OpenUpdateAsync(download: false);
    private async void OnUpdateDownloadClick(object? sender, RoutedEventArgs e) => await OpenUpdateAsync(download: true);

    private async Task OpenUpdateAsync(bool download)
    {
        if (_closed || _updates?.View.Update is not { } update) return;
        string uri = (download ? update.DownloadUri : update.ReleaseUri).AbsoluteUri;
        try
        {
            await _openBrowser(uri);
            if (_closed) return;
            _actionError = null;
            LinkErrorText.Text = string.Empty;
        }
        catch (Exception error)
        {
            Log.Warning(error, "Could not open update page.");
            if (_closed) return;
            _actionError = new(uri, error.Message);
            LinkErrorText.Text = _actionError.Render();
        }
    }

    private async void OnSkipUpdateClick(object? sender, RoutedEventArgs e)
    {
        if (_closed || _updates == null) return;
        try { await _updates.SkipAsync(); _updateActionFailed = false; }
        catch (OperationCanceledException) { }
        catch (Exception error) { Log.Warning(error, "Could not skip update."); _updateActionFailed = true; }
        if (!_closed) RefreshUpdates();
    }

    private void OnCloseButtonClick(object? sender, RoutedEventArgs e) => Close();
}
