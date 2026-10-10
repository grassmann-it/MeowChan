using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using oChan.Desktop.Localization;
using Serilog;
using System;
using System.Diagnostics;
using System.Reflection;

namespace oChan;

public partial class AboutWindow : Window
{
    private readonly string? _version;
    private bool _closed;
    private string? _failedLink;
    private string? _linkFailure;

    public AboutWindow()
    {
        InitializeComponent();
        Assembly assembly = typeof(AboutWindow).Assembly;
        string? informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        _version = informationalVersion?.Split('+', 2)[0] ?? assembly.GetName().Version?.ToString();
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
        VersionText.Text = Texts.T("shell.version", _version ?? Texts.T("about.unknownVersion"));
        DescriptionText.Text = Texts.T("about.description");
        AuthorText.Text = Texts.T("about.author");
        ProjectLink.Content = Texts.T("about.project");
        AuthorLink.Content = Texts.T("about.authorLink");
        HistoryText.Text = Texts.T("about.history");
        IconsText.Text = Texts.T("about.icons");
        LicenseText.Text = Texts.T("about.license");
        CloseButton.Content = Texts.T("action.close");
        if (_failedLink != null)
            LinkErrorText.Text = Texts.T("about.linkFailed", _failedLink, _linkFailure);
    }

    private void OnTextsChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_closed)
                ApplyTexts();
        });
    }

    private void OnLinkClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string uri)
            return;

        try
        {
            using Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = uri,
                UseShellExecute = true
            });
            _failedLink = null;
            _linkFailure = null;
            LinkErrorPanel.IsVisible = false;
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Could not open About link {Uri}.", uri);
            _failedLink = uri;
            _linkFailure = exception.Message;
            LinkErrorText.Text = Texts.T("about.linkFailed", uri, exception.Message);
            LinkErrorPanel.IsVisible = true;
            LinkErrorPanel.BringIntoView();
        }
    }

    private void OnCloseButtonClick(object? sender, RoutedEventArgs e) => Close();
}
