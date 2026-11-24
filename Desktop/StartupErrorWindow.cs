using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
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
    private readonly SelectableTextBlock _detail;
    private readonly Button _copyButton;
    private readonly Button _exitButton;
    private readonly TextBlock _copyStatus;
    private readonly Func<IClipboard?> _getClipboard;
    private string? _copyStatusKey;
    private bool _copyInProgress;
    private bool _closed;

    public StartupErrorWindow(string message) : this(message, null) { }

    internal StartupErrorWindow(string message, Func<IClipboard?>? getClipboard)
    {
        _getClipboard = getClipboard ?? (() => Clipboard);
        // Can open before MainWindow applies the saved theme; the variant in effect now decides.
        ThemeLogo.Follow(this);
        // Modals plan MD3: ownerless and modeless (the error must stay reachable from the taskbar), on the S step.
        DialogWindow.Configure(this, DialogSize.S, modal: false);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        this[!BackgroundProperty] = new DynamicResourceExtension("Workspace.Background");
        this[!ForegroundProperty] = new DynamicResourceExtension("Workspace.Text");

        _detail = new SelectableTextBlock
        {
            Name = "StartupErrorMessage",
            Text = message,
            TextWrapping = TextWrapping.Wrap
        };
        _detail.Classes.Add("workspace-error");
        // The failure is the window's whole subject, so it is the scrolling body rather than
        // a capped local strip; heading and exit stay fixed around it.
        _errorPanel = new Border
        {
            Margin = new Thickness(16, 0, 16, 16),
            Child = _detail
        };
        _errorPanel.Classes.Add("workspace-section");
        _copyButton = new Button { Name = "StartupErrorCopy" };
        _copyButton.Click += CopyMessage;
        _exitButton = new Button { Name = "StartupErrorExit" };
        _exitButton.Click += (_, _) => Close();
        _copyStatus = new TextBlock { Name = "StartupErrorCopyStatus", TextWrapping = TextWrapping.Wrap, IsVisible = false };
        AutomationProperties.SetLiveSetting(_copyStatus, AutomationLiveSetting.Polite);
        // No owner: keeps minimize so the error stays reachable from the taskbar. The title stands in the title bar only.
        DialogWindow.Frame(this, DialogFrame.Build(header: null, _errorPanel,
            DialogFooter.Build(_exitButton, [_copyButton], _copyStatus)), modal: false);
        DialogEscape.Attach(this);

        // Texts has built-in English before Initialize, including for profile startup failures.
        ApplyTexts();
        Opened += OnOpened;
        Closed += (_, _) =>
        {
            _closed = true;
            Texts.Changed -= OnTextsChanged;
        };
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        Opened -= OnOpened;
        Texts.Changed += OnTextsChanged;
        ApplyTexts();
    }

    private void ApplyTexts()
    {
        Title = Texts.T("startup.title");
        _copyButton.Content = Texts.T("command.feedback.copy-text");
        _exitButton.Content = Texts.T("action.exit");
        // The title stands in the title bar only, so the region that carries the failure is named after it;
        // the failure text itself keeps the name of its own text.
        AutomationProperties.SetName(_errorPanel, Title);
        AutomationProperties.SetName(_copyButton, Texts.T("command.feedback.copy-text"));
        AutomationProperties.SetName(_exitButton, Texts.T("action.exit"));
        ApplyCopyStatus();
    }

    private async void CopyMessage(object? sender, RoutedEventArgs e)
    {
        if (_copyInProgress || _closed) return;
        _copyInProgress = true;
        _copyStatusKey = null;
        ApplyCopyStatus();

        string result = "ctxmenu.copyFailed";
        try
        {
            if (_getClipboard() is { } clipboard)
            {
                await clipboard.SetTextAsync(_detail.Text);
                result = "startup.messageCopied";
            }
        }
        catch (Exception)
        {
            // Clipboard access can fail before or during the write. Keep the dialog usable for a retry.
        }
        finally
        {
            _copyInProgress = false;
            if (!_closed)
            {
                _copyStatusKey = result;
                ApplyCopyStatus();
            }
        }
    }

    private void ApplyCopyStatus()
    {
        _copyStatus.Classes.Set("workspace-error", _copyStatusKey == "ctxmenu.copyFailed");
        _copyStatus.IsVisible = _copyStatusKey != null;
        _copyStatus.Text = _copyStatusKey is { } key ? Texts.T(key) : null;
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
