using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using MeowChan.Desktop.Plugins;

namespace MeowChan;

public partial class PluginsPage
{
    private void FocusFirstSettingError()
    {
        PluginConfigurationDraft? draft = CurrentDraft;
        long generation = _operationGeneration;
        // Worker validation runs with the settings disabled. Wait for FinishOperation to enable them.
        Dispatcher.UIThread.Post(() =>
        {
            if (_ended || _busy || generation != _operationGeneration || !ReferenceEquals(CurrentDraft, draft)
                || TopLevel.GetTopLevel(this) == null)
            {
                Serilog.Log.Debug("Plugin save error focus skipped: ended {Ended}, busy {Busy}, generationMatches {GenerationMatches}, draftMatches {DraftMatches}, attached {Attached}",
                    _ended, _busy, generation == _operationGeneration, ReferenceEquals(CurrentDraft, draft), TopLevel.GetTopLevel(this) != null);
                return;
            }
            foreach (PluginSettingsEditor editor in _editors.Values)
                if (editor.TryFocusFirstError()) return;
            TextBlock summary = C<TextBlock>("PluginPageError");
            summary.Focusable = true;
            AutomationProperties.SetName(summary, summary.Text);
            summary.BringIntoView();
            summary.Focus();
        }, DispatcherPriority.Background);
    }
}
