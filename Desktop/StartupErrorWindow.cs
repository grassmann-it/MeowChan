using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using System;

namespace MeowChan.Desktop;

/// <summary>Keeps profile and initialization failures visible even when MainWindow cannot be created.</summary>
public sealed class StartupErrorWindow : Window
{
    private readonly TextBlock _heading;
    private readonly Button _exitButton;
    private bool _closed;

    public StartupErrorWindow(string message)
    {
        // Can open before MainWindow applies the saved theme; the variant in effect now decides.
        ThemeLogo.Follow(this);
        Width = 560;
        Height = 360;
        MinWidth = 340;
        MinHeight = 240;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        this[!BackgroundProperty] = new DynamicResourceExtension("Workspace.Background");
        this[!ForegroundProperty] = new DynamicResourceExtension("Workspace.Text");

        _heading = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap
        };
        _heading.Classes.Add("page-title");
        SelectableTextBlock detail = new SelectableTextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap
        };
        detail.Classes.Add("workspace-error");
        // The failure is the window's whole subject, so it is the scrolling body rather than
        // a capped local strip; heading and exit stay fixed around it.
        Border errorPanel = new Border
        {
            Margin = new Thickness(16, 4, 16, 16),
            Child = detail
        };
        errorPanel.Classes.Add("workspace-section");
        _exitButton = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            MinWidth = 96,
            IsCancel = true
        };
        _exitButton.Click += (_, _) => Close();
        Border footer = new() { Child = _exitButton };
        footer.Classes.Add("workspace-dialog-footer");
        // No owner: keeps minimize so the error stays reachable from the taskbar.
        Content = WindowChrome.Frame(this, DialogFrame.Build(_heading, errorPanel, footer));

        // Texts has built-in English before Initialize, including for profile startup failures.
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
        Title = Texts.T("startup.title");
        _heading.Text = Title;
        _exitButton.Content = Texts.T("action.exit");
    }

    private void OnTextsChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_closed)
                ApplyTexts();
        });
    }
}
