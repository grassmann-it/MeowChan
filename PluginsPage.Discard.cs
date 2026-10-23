using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.VisualTree;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Plugins;
namespace MeowChan;
public partial class PluginsPage
{
    private DraftDiscardConfirmation? _discardConfirmation;
    internal bool IsDiscardConfirmationOpen => _discardConfirmation?.IsOpen == true;
    internal void CancelDiscardConfirmation() => _discardConfirmation?.Close();
    internal void RequestDiscardSelectedDraft()
    {
        if (_ended || _busy || _selectedKey is not string key || !_drafts.TryGetValue(key, out PluginConfigurationDraft? draft)) return;
        if (!draft.HasDiscardableChanges) { _ = DiscardUnchangedDraftAsync(key, draft); return; }
        _discardConfirmation ??= new(C<Avalonia.Controls.Button>("PluginDiscard"));
        long generation = draft.EditGeneration;
        long? completedOperation = null; bool confirmationStarted = false; long navigationGeneration = _operationGeneration;
        _discardConfirmation.OpenAsync(Selected?.Name ?? key, async () =>
        {
            confirmationStarted = true;
            if (!_ended && !_busy && _selectedKey == key && _drafts.TryGetValue(key, out var current) && ReferenceEquals(draft, current) && draft.EditGeneration == generation && draft.HasDiscardableChanges)
                completedOperation = await DiscardSelectedDraftAsync(key, draft);
        }, context: () => !_ended && !_busy && IsEffectivelyVisible && _selectedKey == key && (confirmationStarted ? completedOperation == _operationGeneration : navigationGeneration == _operationGeneration),
            completedFocus: PluginDiscardFocusTarget);
    }
    private Control? PluginDiscardFocusTarget()
    {
        Button discard = C<Button>("PluginDiscard");
        if (discard.IsAttachedToVisualTree() && discard.IsEnabled && discard.IsEffectivelyVisible) return discard;
        return _editors.Values.SelectMany(editor => editor.GetVisualDescendants().OfType<Control>())
            .FirstOrDefault(c => c.Focusable && c.IsAttachedToVisualTree() && c.IsEnabled && c.IsEffectivelyVisible) ?? C<Button>("PluginsRefresh");
    }
    private async System.Threading.Tasks.Task DiscardUnchangedDraftAsync(string key, PluginConfigurationDraft draft)
    {
        long? completed = await DiscardSelectedDraftAsync(key, draft);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (!_ended && !_busy && IsEffectivelyVisible && completed == _operationGeneration && _selectedKey == key
                && PluginDiscardFocusTarget() is { IsEnabled: true, IsEffectivelyVisible: true } target)
            { target.BringIntoView(); target.Focus(); }
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }
    private async System.Threading.Tasks.Task<long?> DiscardSelectedDraftAsync(string key, PluginConfigurationDraft draft)
    {
        if (_ended || _busy || _services == null || _selectedKey != key || !_drafts.TryGetValue(key, out var current) || !ReferenceEquals(current, draft)) return null;
        (long generation, System.Threading.CancellationToken token) = BeginOperation();
        try
        {
            var catalog = await _services.ReadCatalogAsync(token);
            if (!Current(generation) || _selectedKey != key || !_drafts.TryGetValue(key, out current) || !ReferenceEquals(current, draft)) return null;
            _catalog = catalog; _drafts.Remove(key); _renderedKey = null; Error(null); RenderList(); RenderDetails();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (Current(generation)) Error(exception.Message); }
        finally { FinishOperation(generation); }
        return !_ended && generation == _operationGeneration && _selectedKey == key ? generation : null;
    }
}
