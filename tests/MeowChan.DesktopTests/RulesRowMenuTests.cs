using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Rules;

namespace MeowChan.DesktopTests;

/// <summary>The context menu of a Rules overview row: what it offers for a saved rule, a draft and a busy page, in what order, and what each entry reaches.</summary>
[Collection("Localization")]
public sealed class RulesRowMenuTests : IDisposable
{
    public RulesRowMenuTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static RuleOverviewRow Row(MediaRuleScope scope, bool saved = true, bool enabled = true, bool hasDraft = false) =>
        new(scope, scope.Kind == MediaRuleScopeKind.Global ? "Everything" : scope.Board ?? scope.Source ?? "scope", saved, enabled, hasDraft,
            [new RuleChip(".jpg, .png"), new RuleChip("≤ 20 MiB")]);

    private static ContextMenuModel Model(RuleOverviewRow row, bool draftRow = false, bool editable = true, Action? edit = null, Action? parent = null,
        Action? toggle = null, Action? copy = null, Action? remove = null)
    {
        MediaRuleScope? above = RulesPage.ParentOf(row.Scope);
        return RulesRowMenu.Compose(row, draftRow, above, above == null ? null : "4chan", editable, editable ? null : "Saving…",
            edit ?? (() => { }), parent ?? (() => { }), toggle ?? (() => { }), copy ?? (() => { }), remove ?? (() => { })).ToModel();
    }

    private static string[] Ids(ContextMenuModel model) => model.Entries.Select(entry => entry.CommandId!).ToArray();

    [Fact]
    public void ASavedBoardRuleOffersEditParentToggleCopyAndDeleteLastAsCatalogCommands()
    {
        ContextMenuModel model = Model(Row(MediaRuleScope.ForBoard("4chan", "g")));
        Assert.Equal(new[] { "rules.open-scope", "rules.open-parent", "rules.toggle-enabled", "rules.copy-summary", "rules.delete" }, Ids(model));
        Assert.Equal(new[] { "Edit", "Open parent scope · 4chan", "Disable", "Copy rule summary", "Delete rule…" }, model.Entries.Select(entry => entry.Label));
        Assert.All(model.Entries, entry => Assert.True(CommandCatalog.TryGet(entry.CommandId!, out _)));
        Assert.Equal(4, model.Sections.Count);
        Assert.Equal("rules.delete", model.Sections[^1].Single().CommandId);
        Assert.Empty(model.OversizedSections);
    }

    [Fact]
    public void ADisabledRuleOffersEnableAndTheGlobalScopeHasNoParent()
    {
        ContextMenuModel model = Model(Row(MediaRuleScope.Global, enabled: false));
        Assert.Equal(new[] { "rules.open-scope", "rules.toggle-enabled", "rules.copy-summary", "rules.delete" }, Ids(model));
        Assert.Equal("Enable", model.Entries.Single(entry => entry.CommandId == "rules.toggle-enabled").Label);
    }

    [Fact]
    public void ADraftIsContinuedAndDiscardedAndAnUnsavedOneCannotBeToggled()
    {
        ContextMenuModel fresh = Model(Row(MediaRuleScope.ForSource("4chan"), saved: false, hasDraft: true), draftRow: true);
        Assert.Equal(new[] { "rules.open-scope", "rules.open-parent", "rules.copy-summary", "rules.discard" }, Ids(fresh));
        Assert.Equal(new[] { "Continue", "Open parent scope · 4chan", "Copy rule summary", "Discard draft" }, fresh.Entries.Select(entry => entry.Label));

        // A saved rule with changes on top keeps its Delete; its toggle waits for the draft to be saved or discarded, like the row's buttons.
        ContextMenuModel changed = Model(Row(MediaRuleScope.ForBoard("4chan", "g"), hasDraft: true));
        Assert.DoesNotContain("rules.toggle-enabled", Ids(changed));
        Assert.Equal("Continue", changed.Entries.First().Label);
        Assert.Equal("rules.delete", changed.Entries.Last().CommandId);
    }

    [Fact]
    public void ABusyPageListsTheChangingEntriesAsUnavailableWithTheReasonAndStillCopies()
    {
        ContextMenuModel model = Model(Row(MediaRuleScope.ForBoard("4chan", "g")), editable: false);
        foreach (ContextMenuEntry entry in model.Entries)
        {
            if (entry.CommandId == "rules.copy-summary") { Assert.True(entry.Available); continue; }
            Assert.False(entry.Available, entry.CommandId);
            Assert.Equal("Saving…", entry.Reason);
        }
    }

    [Fact]
    public void EachEntryReachesItsOwnAction()
    {
        List<string> log = [];
        ContextMenuModel model = Model(Row(MediaRuleScope.ForBoard("4chan", "g")), edit: () => log.Add("edit"), parent: () => log.Add("parent"),
            toggle: () => log.Add("toggle"), copy: () => log.Add("copy"), remove: () => log.Add("remove"));
        foreach (ContextMenuEntry entry in model.Entries) entry.Invoke!();
        Assert.Equal(new[] { "edit", "parent", "toggle", "copy", "remove" }, log);
    }

    [Fact]
    public void TheSummaryIsTheScopeAndWhatTheRuleSets()
    {
        Assert.Equal("g: .jpg, .png, ≤ 20 MiB", RulesRowMenu.Summary(Row(MediaRuleScope.ForBoard("4chan", "g"))));
        Assert.Equal("Everything: .jpg, .png, ≤ 20 MiB", RulesRowMenu.Summary(Row(MediaRuleScope.Global)));
    }

    [Fact]
    public void TheParentIsOneStepUpAndTheGlobalScopeHasNone()
    {
        Assert.Equal(MediaRuleScope.ForBoard("4chan", "g"), RulesPage.ParentOf(MediaRuleScope.ForThread("4chan", "g", "9")));
        Assert.Equal(MediaRuleScope.ForSource("4chan"), RulesPage.ParentOf(MediaRuleScope.ForBoard("4chan", "g")));
        Assert.Equal(MediaRuleScope.Global, RulesPage.ParentOf(MediaRuleScope.ForSource("4chan")));
        Assert.Null(RulesPage.ParentOf(MediaRuleScope.Global));
    }
}
