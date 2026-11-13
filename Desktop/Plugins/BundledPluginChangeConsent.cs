using System;

namespace MeowChan.Desktop.Plugins;

/// <summary>Row-local change consent; editing never carries a prior acknowledgement forward.</summary>
internal sealed class BundledPluginChangeConsent
{
    private readonly BundledPluginChangeDraft _draft;
    internal bool IsAcknowledged { get; private set; }
    internal event Action? Invalidated;

    internal BundledPluginChangeConsent(BundledPluginChangeDraft draft)
    {
        _draft = draft;
        draft.Plugin.Changed += Invalidate;
        foreach (PluginSettingsDraft source in draft.Sources.Values) source.Changed += Invalidate;
    }

    // Only the explicit change checkbox sets this, independently of permission consent and validation.
    internal void SetAcknowledged(bool acknowledged) => IsAcknowledged = acknowledged;

    internal void AcknowledgeRemovedValues()
    {
        // Also covers removed-source-only configurations, not just fields with Changed events.
        Invalidate();
        _draft.AcknowledgeRemovedValues();
    }

    private void Invalidate()
    {
        if (!IsAcknowledged) return;
        IsAcknowledged = false;
        Invalidated?.Invoke();
    }
}
