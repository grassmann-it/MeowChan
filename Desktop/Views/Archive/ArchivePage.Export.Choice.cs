using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using MeowChan.Desktop.Localization;

namespace MeowChan;

// AP7: one "Export…" dialog for threads. Its header chooses the format (HTML page or link list) and
// the scope (this thread, the selected threads or every match of the applied search); every entry
// point opens it preselected. The HTML work itself is unchanged; the link list keeps its own review
// window (consent text, create-only writer), reached from here.
public partial class ArchivePage
{
    internal enum ExportFormat { Html, LinkList }
    internal enum ExportScope { Selection, AllResults }

    private ExportFormat _exportFormat = ExportFormat.Html;
    // The threads the entry point froze. A scope switch returns to them; all results are resolved anew.
    private ResourceId[] _exportSessionMembers = Array.Empty<ResourceId>();
    private bool _exportOffersResults;
    // After a successful export Close becomes the primary action until the draft changes again.
    private bool _exportPrimaryClose;
    private ExportChoiceField? _exportFormatField;
    private ExportChoiceField? _exportScopeField;

    internal ExportFormat CurrentExportFormat => _exportFormat;
    internal ExportScope CurrentExportScope => _exportAllResults ? ExportScope.AllResults : ExportScope.Selection;
    internal bool ExportPrimaryIsClose => _exportPrimaryClose;
    // A link list names exactly one page, so it is offered only when the entry froze a single thread.
    private bool ExportSessionSingle => _exportSessionMembers.Length == 1;
    private bool ExportResultsOffered => _exportOffersResults && (_exportAllResults || _appliedSearch != null && _listTotal > 0);

    private void BeginExportSession(ResourceId[] members, bool offerResults, ExportFormat format)
    {
        _exportSessionMembers = members;
        _exportOffersResults = offerResults;
        _exportFormat = format == ExportFormat.LinkList && members.Length != 1 ? ExportFormat.Html : format;
    }

    private Control BuildExportChoices()
    {
        _exportFormatField = RegisterExportChoice(new ExportChoiceField("ArchiveExportFormat"));
        _exportScopeField = RegisterExportChoice(new ExportChoiceField("ArchiveExportScope"));
        _exportFormatField.Chosen += value => OnExportFormatChosen((ExportFormat)value);
        _exportScopeField.Chosen += async value => await OnExportScopeChosenAsync((ExportScope)value);
        WrapPanel row = ExportName(new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 16, LineSpacing = 6 }, "ArchiveExportChoices");
        row.Children.Add(_exportFormatField);
        row.Children.Add(_exportScopeField);
        return row;
    }

    private ExportChoiceField RegisterExportChoice(ExportChoiceField field)
    {
        _exportNames.Register(field.Name!, field);
        _exportNames.Register(field.Box.Name!, field.Box);
        _exportNames.Register(field.Fixed.Name!, field.Fixed);
        _exportNames.Register(field.Label.Name!, field.Label);
        return field;
    }

    private void UpdateExportChoices()
    {
        if (_exportFormatField == null || _exportScopeField == null) return;
        bool enabled = !_exportRunning && !_exportPicking;
        List<ExportChoiceField.Option> formats = new() { new(ExportFormat.Html, Texts.T("export.format.html")) };
        if (ExportSessionSingle) formats.Add(new(ExportFormat.LinkList, Texts.T("export.format.linkList")));
        _exportFormatField.Set(Texts.T("export.format"), formats, _exportFormat, enabled);
        List<ExportChoiceField.Option> scopes = new();
        if (_exportSessionMembers.Length == 1) scopes.Add(new(ExportScope.Selection, Texts.T("export.scope.thread")));
        else if (_exportSessionMembers.Length > 1) scopes.Add(new(ExportScope.Selection, Texts.Count("export.scope.selection", _exportSessionMembers.Length)));
        if (_exportFormat == ExportFormat.Html && ExportResultsOffered) scopes.Add(new(ExportScope.AllResults, Texts.T("export.scope.results")));
        _exportScopeField.Set(Texts.T("export.scope"), scopes, CurrentExportScope, enabled);
    }

    private void OnExportFormatChosen(ExportFormat format)
    {
        if (format == _exportFormat || !ArchiveExportDialog.IsVisible || !ExportRequestIsCurrent(_exportRequest)) return;
        if (_exportRunning || _exportPicking) { UpdateExportActions(); return; }
        _exportFormat = format;
        _exportPrimaryClose = false;
        ExportStatus(string.Empty);
        ExportError(string.Empty);
        // A link list names one page: leaving "all matches" returns to the thread the entry froze.
        if (format == ExportFormat.LinkList && _exportAllResults) { _ = SwitchExportScopeAsync(ExportScope.Selection); return; }
        UpdateExportActions();
    }

    private async Task OnExportScopeChosenAsync(ExportScope scope)
    {
        if (scope == CurrentExportScope || !ArchiveExportDialog.IsVisible) return;
        if (_exportRunning || _exportPicking) { UpdateExportActions(); return; }
        await SwitchExportScopeAsync(scope);
    }

    // Reuses the entry paths: a switch is a new request in the open dialog, with the same origin and return.
    private Task SwitchExportScopeAsync(ExportScope scope)
    {
        _exportPrimaryClose = false;
        if (scope == ExportScope.AllResults) return OpenExportForAllResultsAsync(_exportOrigin, switching: true);
        ResourceId[] members = _exportSessionMembers;
        if (members.Length == 0) return Task.CompletedTask;
        return OpenExportCoreAsync(members[0], _exportOrigin, members.Length > 1 ? members : null, _exportClosed, switching: true);
    }

    private static readonly string[] HtmlOnlyExportControls =
    {
        "ArchiveExportTargetLabel", "ArchiveExportDestination", "ArchiveExportBrowse", "ArchiveExportMedia", "ArchiveExportOpen"
    };
    private static readonly string[] ConditionalHtmlExportControls =
    {
        "ArchiveExportHistoryLabel", "ArchiveExportHistory", "ArchiveExportNew", "ArchiveExportUpdate", "ArchiveExportPrevious",
        "ArchiveExportAssociate", "ArchiveExportLegacySection", "ArchiveExportBackup", "ArchiveExportBackupHint"
    };

    // Runs at the end of UpdateExportActions: the link list shows only its explanation and the review action.
    private void ApplyExportFormat()
    {
        bool html = _exportFormat == ExportFormat.Html;
        foreach (string name in HtmlOnlyExportControls) Control<Control>(name).IsVisible = html;
        if (!html) foreach (string name in ConditionalHtmlExportControls) Control<Control>(name).IsVisible = false;
        // The extras section is on the dialog only while one of its parts is (a link list or a collection can leave none).
        Expander options = Control<Expander>("ArchiveExportMoreOptions");
        options.IsVisible = html && ((Panel)options.Content!).Children.Any(child => child.IsVisible);
        OpenExportOptionsForUpdate();
        Button start = Control<Button>("ArchiveExportStart");
        Button close = Control<Button>("ArchiveExportClose");
        bool closePrimary = _exportPrimaryClose && !_exportRunning;
        start.Classes.Set("primary", !closePrimary);
        close.Classes.Set("primary", closePrimary);
        close.Classes.Set("subtle", !closePrimary);
        UpdateExportChoices();
    }

    private void FocusExportEntry()
    {
        if (_exportFormat == ExportFormat.LinkList) Control<Button>("ArchiveExportStart").Focus();
        else Control<TextBox>("ArchiveExportDestination").Focus();
    }

    // Called right after a published result: Close takes over as the primary action and the focus
    // moves there when it was in the dialog (or nowhere), never away from somewhere else.
    private void MarkExportSucceeded()
    {
        _exportPrimaryClose = true;
        if (_exportCard == null) return;
        object? focused = TopLevel.GetTopLevel(_exportCard)?.FocusManager?.GetFocusedElement();
        if (focused == null || focused is Control control && IsExportChild(control))
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (_exportPrimaryClose && ArchiveExportDialog.IsVisible) Control<Button>("ArchiveExportClose").Focus();
            });
    }

    // Typing another destination after a success makes "Export" the primary action again.
    private void OnExportDestinationEdited(TextBox destination)
    {
        if (!_exportPrimaryClose || !destination.IsFocused || _exportRunning) return;
        _exportPrimaryClose = false;
        UpdateExportActions();
    }

    // The link list's review window holds the consent text and the create-only writer. A page shows one
    // export surface at a time, so the dialog closes first (restoring its origin) and hands over the frozen thread.
    private async Task StartLinkListReviewAsync()
    {
        if (_exportThread is not ResourceId id || !ExportSessionSingle) return;
        Control? origin = _exportOrigin;
        OnExportClose(this, new RoutedEventArgs());
        if (ArchiveExportDialog.IsVisible) return;
        await OpenUrlTextExportForThreadAsync(id, origin);
    }
}
