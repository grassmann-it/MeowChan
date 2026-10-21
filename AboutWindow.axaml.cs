using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MeowChan.Desktop.Legal;
using MeowChan.Desktop.Localization;
using Serilog;
using System;
using System.Diagnostics;
using System.Reflection;

namespace MeowChan;

public partial class AboutWindow : Window
{
    private readonly string _version;
    private readonly OfflineLegalCatalog _legal;
    private bool _closed;
    private string? _failedLink;
    private string? _linkFailure;

    public AboutWindow()
    {
        InitializeComponent();
        Assembly assembly = typeof(AboutWindow).Assembly;
        string? informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        _version = informationalVersion?.Split('+', 2)[0] ?? assembly.GetName().Version?.ToString() ?? Texts.T("about.unknownVersion");
        _legal = OfflineLegalCatalog.Load(AppContext.BaseDirectory);
        LicenseList.ItemsSource = _legal.Entries;
        LicenseList.SelectedIndex = _legal.Entries.Count > 0 ? 0 : -1;
        ApplyTexts();
        Texts.Changed += OnTextsChanged;
        Closed += (_, _) =>
        {
            _closed = true;
            Texts.Changed -= OnTextsChanged;
        };
    }

    private void ApplyTexts()
    {
        Title = Texts.T("about.title");
        AboutTab.Header = Texts.T("about.tab.about");
        CreditsTab.Header = Texts.T("about.tab.credits");
        LicensesTab.Header = Texts.T("about.tab.licenses");
        AutomationProperties.SetName(CatLogo, Texts.T("about.logoDescription"));
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
        LicenseText.Text = Texts.T("about.license");
        LicenseSearchBox.PlaceholderText = Texts.T("about.licenseSearch");
        LicenseHintText.Text = _legal.Error is null ? Texts.T("about.licenseHint") : Texts.T("about.licenseReadFailed", _legal.Error);
        CloseButton.Content = Texts.T("action.close");
        if (_failedLink != null)
            LinkErrorText.Text = Texts.T("about.linkFailed", _failedLink, _linkFailure);
        UpdateSelection();
    }

    private void OnTextsChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (!_closed)
            ApplyTexts();
    });

    protected override void OnKeyDown(KeyEventArgs e)
    {
        KeyModifiers searchModifier = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        if (e.Key == Key.F && e.KeyModifiers == searchModifier)
        {
            AboutTabs.SelectedIndex = 2;
            LicenseSearchBox.Focus();
            e.Handled = true;
            return;
        }
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
        try
        {
            if (Clipboard != null)
                await Clipboard.SetTextAsync(_version);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Could not copy application version.");
            LinkErrorText.Text = Texts.T("about.copyFailed", exception.Message);
        }
    }

    private void OnLinkClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string uri)
            return;
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo { FileName = uri, UseShellExecute = true });
            _failedLink = null;
            _linkFailure = null;
            LinkErrorText.Text = "";
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Could not open About link {Uri}.", uri);
            _failedLink = uri;
            _linkFailure = exception.Message;
            LinkErrorText.Text = Texts.T("about.linkFailed", uri, exception.Message);
        }
    }

    private void OnCloseButtonClick(object? sender, RoutedEventArgs e) => Close();
}
