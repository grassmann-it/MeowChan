using Avalonia;
using Avalonia.Automation;
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
    private readonly Border _errorPanel;
    private readonly Button _exitButton;
    private bool _closed;

    public StartupErrorWindow(string message)
    {
        // Can open before MainWindow applies the saved theme; the variant in effect now decides.
        ThemeLogo.Follow(this);
        // Modals plan MD3: ownerless and modeless (the error must stay reachable from the taskbar), on the S step.
        DialogWindow.Configure(this, DialogSize.S, modal: false);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        this[!BackgroundProperty] = new DynamicResourceExtension("Workspace.Background");
        this[!ForegroundProperty] = new DynamicResourceExtension("Workspace.Text");

        SelectableTextBlock detail = new SelectableTextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap
        };
        detail.Classes.Add("workspace-error");
        // The failure is the window's whole subject, so it is the scrolling body rather than
        // a capped local strip; heading and exit stay fixed around it.
        _errorPanel = new Border
        {
            Margin = new Thickness(16, 0, 16, 16),
            Child = detail
        };
        _errorPanel.Classes.Add("workspace-section");
        _exitButton = new Button();
        _exitButton.Click += (_, _) => Close();
        // No owner: keeps minimize so the error stays reachable from the taskbar. The title stands in the title bar only.
        DialogWindow.Frame(this, DialogFrame.Build(header: null, _errorPanel, DialogFooter.Build(_exitButton)), modal: false);
        DialogEscape.Attach(this);

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
        _exitButton.Content = Texts.T("action.exit");
        // The title stands in the title bar only, so the region that carries the failure is named after it;
        // the failure text itself keeps the name of its own text.
        AutomationProperties.SetName(_errorPanel, Title);
        AutomationProperties.SetName(_exitButton, Texts.T("action.exit"));
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
