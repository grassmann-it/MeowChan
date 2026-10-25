using Avalonia.Automation;
using Avalonia.Controls;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class ArchivePage
{
    private readonly TextBlock _archiveFilterHeader = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private void InitializeArchivePresentation()
    {
        SearchStateChanged += RefreshArchiveSearchContext;
        AppliedSearchChanged += RefreshArchiveSearchContext;
    }

    private void RefreshArchiveSearchContext()
    {
        // Use the existing side-effect-free draft comparison. Pending/invalid raw
        // fields remain drafts; this projection never validates or applies them.
        bool draft = _appliedSearch != null && HasUnappliedSearchDraft;
        Expander filters = Control<Expander>("ArchiveFilters");
        _archiveFilterHeader.Text = Texts.T("archive.filters")
            + (draft ? " · " + Texts.T("archive.unappliedShort") : string.Empty);
        filters.Header = _archiveFilterHeader;
        string? hint = draft ? Texts.T("archive.unappliedHint") : null;
        ToolTip.SetTip(filters, hint);
        AutomationProperties.SetHelpText(filters, hint ?? string.Empty);
    }

    private void RefreshArchiveSelectionContext()
    {
        TextBlock summary = Control<TextBlock>("ArchiveSelectionSummary");
        int count = FrozenArchiveSelection().Length;
        ArchiveThreadRow? lead = ArchiveLead;
        summary.IsVisible = count > 0;
        summary.Text = count > 0 ? Texts.T("archive.selectionPage", count, lead?.Summary.Id.Thread ?? "—") : string.Empty;
        string? detail = lead == null ? null : summary.Text + " · " + lead.TitleOrId + " · " + lead.Identity;
        ToolTip.SetTip(summary, detail);
        AutomationProperties.SetHelpText(summary, detail ?? string.Empty);
    }
}
