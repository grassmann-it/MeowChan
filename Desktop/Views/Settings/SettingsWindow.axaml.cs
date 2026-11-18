using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan;

// Compatibility host for the shared editor; the main shell hosts the editor directly.
public partial class SettingsWindow : Window
{
    public SettingsEditor Editor { get; }
    public SettingsWindow() : this(new Config()) { }
    public SettingsWindow(Config config, Func<Config, Task>? applySettings = null)
    {
        InitializeComponent();
        WindowChrome.Apply(this);
        // Modals plan MD3: the XL step (size, centring, screen fit) from the dialog factory; a modeless window keeps minimize.
        DialogWindow.Configure(this, DialogSize.XL, modal: false);
        AppCaption.Attach(this);
        ThemeLogo.Follow(this);
        Editor = new SettingsEditor(config, applySettings, closeOnDiscard: true);
        EditorHost.Content = Editor;
        DataContext = Editor;
        Editor.Saved += OnFinished;
        Editor.Cancelled += OnFinished;
        // Escape never loses a draft silently: with unsaved changes it asks first (a local cancel), never while saving.
        DialogEscape.Attach(this, tryCancelLocal: AskBeforeDiscardingOnEscape, isBusy: () => Editor.IsBusy);
    }

    private bool _asking;

    private bool AskBeforeDiscardingOnEscape()
    {
        if (_asking || !Editor.HasUnsavedChanges) return false;
        _ = AskAsync();
        return true;
    }

    private async Task AskAsync()
    {
        _asking = true;
        try
        {
            bool discard = await DialogPrompt.ConfirmAsync(this, Title ?? string.Empty, Texts.T("settings.discardQuestion"),
                Texts.T("dialog.discardChanges"), Texts.T("dialog.keepEditing"));
            if (discard && !Editor.IsBusy) Close();
        }
        finally { _asking = false; }
    }

    private void OnFinished(object? sender, EventArgs e) => Close();
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (Editor.IsBusy && e.CloseReason == WindowCloseReason.WindowClosing) e.Cancel = true;
    }
    protected override void OnClosed(EventArgs e)
    {
        Editor.Saved -= OnFinished;
        Editor.Cancelled -= OnFinished;
        Editor.EndLifetime();
        base.OnClosed(e);
    }
}
