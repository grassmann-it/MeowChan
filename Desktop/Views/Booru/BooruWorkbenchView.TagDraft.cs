using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Styling;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.PluginSdk;

namespace MeowChan.Desktop.Booru;

internal sealed partial class BooruWorkbenchView
{
    internal const string MenuTagRemove = "booru.menu.tag-remove";
    private const string DraftIncluded = "booru-draft-included";
    private const string DraftExcluded = "booru-draft-excluded";
    private readonly List<ContextMenuHost> _resultTagMenus = new();
    private readonly List<(Button Button, ChipOutline Outline, BooruTag Tag, string Category)> _resultTagChips = new();
    private (BooruInputMode Mode, string Input)? _shownTagDraft;

    internal ContextMenuBuilder? ComposeTagMenu(string tag, BooruResultTarget? target = null)
    {
        ResultLinkScope? scope = CaptureResultLink(target);
        object? tab = _tabs.SelectedItem;
        bool Available() => !_closed && IsSearchItem(tab) && ReferenceEquals(tab, _tabs.SelectedItem)
            && (scope is not { } bound || IsCurrentResultLink(bound));
        if (!Available()) return null;

        return new ContextMenuBuilder().Named(tag)
            .Item(MenuTagAdd, Texts.T("booru.ui.tags.menuAdd"), () => Edit(BooruTagDraftAction.Include), availableWhen: Available, icon: "plus")
            .Item(MenuTagExclude, Texts.T("booru.ui.tags.menuExclude"), () => Edit(BooruTagDraftAction.Exclude), availableWhen: Available, icon: "minus")
            .Item(MenuTagRemove, Texts.T("action.remove"), () => Edit(BooruTagDraftAction.Remove), availableWhen: Available, icon: "x")
            .Section()
            .Command("tags.copy-name", () =>
            {
                if (Available()) OpenActions.Start(() => CopyTagNameAsync(tag, scope), "booru.tags.copy");
            }, availableWhen: Available);

        void Edit(BooruTagDraftAction action)
        {
            if (Available()) EditTagDraft(tag, action, scope);
        }
    }

    private async Task CopyTagNameAsync(string tag, ResultLinkScope? scope)
    {
        if (!TryBeginLinkAction(out long revision)) return;
        if (await MenuClipboard.CopyAsync(this, tag, _ => SetLinkNote(("booru.ui.link.copyFailed", string.Empty, true), revision, scope)))
            SetLinkNote(("booru.ui.tags.copied", tag, false), revision, scope);
    }

    private void InstallTagDraftStyles()
    {
        Styles.Add(TagDraftStyle(DraftIncluded, "Workspace.ChipSelectedSurface", "Workspace.ChipSelectedBorder", dashed: false));
        Styles.Add(TagDraftStyle(DraftExcluded, "Workspace.ErrorSurface", "Workspace.Error", dashed: true));

        static Style TagDraftStyle(string state, string surface, string edge, bool dashed) =>
            new(selector => selector.OfType<ChipOutline>().Class(state))
            {
                Setters =
                {
                    new Setter(ChipOutline.BackgroundProperty, new DynamicResourceExtension(surface)),
                    new Setter(ChipOutline.StrokeProperty, new DynamicResourceExtension(edge)),
                    new Setter(ChipOutline.StrokeThicknessProperty, 2d),
                    new Setter(ChipOutline.IsDashedProperty, dashed)
                }
            };
    }

    private void RenderTagDraft(BooruSearchView view)
    {
        if (_shownTagDraft == (view.Mode, view.Input)) return;
        _shownTagDraft = (view.Mode, view.Input);
        var query = view.Mode == BooruInputMode.Tags ? BooruInput.ReadTagDraft(view.Input, out _) : null;
        foreach (var (button, outline, tag, category) in _resultTagChips)
        {
            BooruTagDraftState state = BooruTagDraft.State(query, tag.Name);
            outline.Classes.Set(DraftIncluded, state == BooruTagDraftState.Included);
            outline.Classes.Set(DraftExcluded, state is BooruTagDraftState.Excluded or BooruTagDraftState.Both);

            string tagAndCategory = Texts.T("media.breakdown.summary", category, tag.Name);
            string included = Texts.T("booru.ui.tags.alreadyInSearch", tagAndCategory);
            string excluded = Texts.T("media.filter.combo.excluded", tagAndCategory);
            string name = state switch
            {
                BooruTagDraftState.Included => included,
                BooruTagDraftState.Excluded => excluded,
                BooruTagDraftState.Both => included + " " + excluded,
                _ => Texts.T("booru.ui.tags.addFor", tag.Name, category)
            };
            AutomationProperties.SetName(button, name);
            ToolTip.SetTip(button, name);
        }
    }

    /// <summary>Edits the live search draft locally. The result binding is checked before the field can change.</summary>
    internal void AddTagToSearch(string tag, bool exclude = false) =>
        EditTagDraft(tag, exclude ? BooruTagDraftAction.Exclude : BooruTagDraftAction.Include);

    private void EditTagDraft(string tag, BooruTagDraftAction action, ResultLinkScope? scope = null)
    {
        if (_closed || (scope is { } bound && !IsCurrentResultLink(bound))) return;
        BooruSearchView view = _model.Search.View;
        BooruTagDraftEdit edit = BooruTagDraft.Edit(view.Mode, view.Input, tag, action);
        if (edit.Replacement is { } query)
        {
            _model.Search.SetMode(BooruInputMode.Tags);
            _input.Text = query;
            _model.Search.SetInput(query);
        }

        string term = action == BooruTagDraftAction.Exclude ? "-" + tag : tag;
        (string Key, string Argument, bool Problem)? note = edit switch
        {
            { Issue: BooruInputIssue.TooManyTags } => ("booru.ui.tags.searchFull",
                BooruSourceTagSearch.MaximumTags.ToString(Texts.Culture), true),
            { Issue: not BooruInputIssue.None } => (BooruText.Prefix + "issue." + edit.Issue, string.Empty, true),
            { UnusableTag: true } => ("booru.ui.tags.unusable", tag, true),
            _ when action == BooruTagDraftAction.Remove => null,
            { Replacement: null } => ("booru.ui.tags.alreadyInSearch", term, false),
            _ => ("booru.ui.tags.addedToSearch", term, false)
        };
        if (TryBeginLinkAction(out long revision)) SetLinkNote(note, revision, scope);
    }

    private void DisposeResultTagMenus()
    {
        foreach (ContextMenuHost menu in _resultTagMenus) menu.Dispose();
        _resultTagMenus.Clear();
        _resultTagChips.Clear();
        _shownTagDraft = null;
    }

    private sealed class DraftTagButton : Button
    {
        protected override Type StyleKeyOverride => typeof(Button);
        internal bool ExcludeOnClick { get; private set; }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            // Let Button decide whether this is a click, including pointer capture and release outside the chip.
            ExcludeOnClick = (e.KeyModifiers & (KeyModifiers.Alt | KeyModifiers.Shift)) != 0;
            try { base.OnPointerReleased(e); }
            finally { ExcludeOnClick = false; }
        }
    }
}
