using System;
using System.Threading.Tasks;
using Avalonia.Controls;

namespace MeowChan;

// Compatibility host for the shared editor; the main shell hosts the editor directly.
public partial class SettingsWindow : Window
{
    public SettingsEditor Editor { get; }
    public SettingsWindow() : this(new Config()) { }
    public SettingsWindow(Config config, Func<Config, Task>? applySettings = null)
    {
        InitializeComponent();
        Editor = new SettingsEditor(config, applySettings, closeOnDiscard: true);
        EditorHost.Content = Editor;
        DataContext = Editor;
        Editor.Saved += OnFinished;
        Editor.Cancelled += OnFinished;
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
