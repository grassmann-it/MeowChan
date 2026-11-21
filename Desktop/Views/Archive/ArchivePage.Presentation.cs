using Avalonia.Automation;
using Avalonia.Controls;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class ArchivePage
{
    private void InitializeArchivePresentation()
    {
        SearchStateChanged += RefreshArchiveSearchContext;
        AppliedSearchChanged += RefreshArchiveSearchContext;
    }

    private void RefreshArchiveSearchContext() => UpdateArchiveFilterToggle();

    // S2: the toggle's text, its count badge (one per applied filter chip, as on Media) and the unapplied-draft marker.
    // Use the existing side-effect-free draft comparison. Pending/invalid raw fields remain drafts; this projection never
    // validates or applies them.
    private void UpdateArchiveFilterToggle()
    {
        bool draft = _appliedSearch != null && HasUnappliedSearchDraft;
        int count = _appliedFilterCount;
        string label = Texts.T("archive.filters") + (draft ? " · " + Texts.T("archive.unappliedShort") : string.Empty);
        string name = count > 0 ? Texts.Count("archive.filters.countName", count) : Texts.T("archive.filters");
        ArchiveFilterDraftNote.IsVisible = draft;
        ArchiveFilterDraftNote.Text = draft ? Texts.T("archive.filterDraftNote") : string.Empty;
        ArchiveFilters.Show(label, count, draft ? name + ", " + Texts.T("archive.unappliedShort") : name, draft ? Texts.T("archive.unappliedHint") : null);
    }

    private void RefreshArchiveSelectionContext()
    {
        TextBlock summary = ArchiveSelectionSummary;
        int count = FrozenArchiveSelection().Length;
        ArchiveThreadRow? lead = ArchiveLead;
        summary.IsVisible = count > 0;
        summary.Text = count > 0 ? Texts.T("archive.selectionPage", count, lead?.Summary.Id.Thread ?? "—") : string.Empty;
        string? detail = lead == null ? null : summary.Text + " · " + lead.TitleOrId + " · " + lead.Identity;
        ToolTip.SetTip(summary, detail);
        AutomationProperties.SetHelpText(summary, detail ?? string.Empty);
    }
}
