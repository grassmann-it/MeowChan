using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Plugins;

namespace MeowChan;

public partial class PluginsPage
{
    private void OnApproveSdkTransitionClick(object? sender, RoutedEventArgs e)
    {
        if (_ended || _busy || _services is null || _catalog is null || Selected is not { } entry ||
            _selectedKey is not { } key || !Included(entry)) return;
        PluginEntryPresentation presentation = PluginEntryPresentation.Create(entry);
        if (!presentation.HasAutomaticSdkTransition || presentation.AutomaticSdkTransition is not { } transition ||
            presentation.PluginId is not { } pluginId) return;

        _sdkTransitionConfirmation ??= new DraftDiscardConfirmation(C<Button>("PluginApproveSdkTransition"));
        long capturedRevision = _catalog.Profile.Revision;
        string capturedExecution = transition.ExecutionRevision;
        string capturedPluginId = pluginId;
        long navigationGeneration = _operationGeneration;
        _sdkTransitionConfirmation.OpenAsync(
            entry.Name,
            async () =>
            {
                if (!_ended && !_busy && _selectedKey == key && _catalog?.Profile.Revision == capturedRevision &&
                    Selected is { } current && current.PluginId == capturedPluginId &&
                    PluginEntryPresentation.Create(current).AutomaticSdkTransition is { } currentTransition &&
                    currentTransition.ExecutionRevision == capturedExecution)
                    await CommandAsync((services, revision, token) => services.ProfileStore
                        .ExplicitlyApproveSdkTransitionAsync(capturedPluginId, capturedExecution, revision, token));
            },
            actionLabel: Texts.T("plugins.sdkTransition.confirm"),
            context: () => !_ended && !_busy && IsEffectivelyVisible && _selectedKey == key &&
                navigationGeneration == _operationGeneration,
            completedFocus: SdkTransitionApprovalFocusTarget,
            title: Texts.T("plugins.sdkTransition.title", entry.Name),
            body: Texts.T("plugins.sdkTransition.body", transition.FromSdkVersion, transition.ToSdkVersion, transition.ExecutionRevision),
            cancelLabel: Texts.T("plugins.sdkTransition.cancel"));
    }

    private Control? SdkTransitionApprovalFocusTarget()
    {
        Button button = C<Button>("PluginApproveSdkTransition");
        return button.IsAttachedToVisualTree() && button.IsEffectivelyVisible && button.IsEnabled ? button :
            C<Button>("PluginMoreActions");
    }
}
