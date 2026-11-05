using System.Text.Json;
using Avalonia.Controls;
using MeowChan;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Persistence;
using GroupRow = MeowChan.ReaderGroupsDialog.GroupRow;
using MemberRow = MeowChan.ReaderGroupsDialog.MemberRow;

namespace MeowChan.DesktopTests;

// Actual detached controls + production RefreshRows, not a constructed dialog.
// The fixture's Texts.Changed bridge is test-owned and synchronous: it does NOT
// exercise ReaderGroupsDialog.LanguageChanged, Localize, dispatcher/layout, the
// constructor's edit handlers, native focus, or the dialog's private mode flags.
[Collection("Localization")]
public sealed class ReaderGroupsPresentationStateRegressionTests : IDisposable
{
    private readonly string _previousLanguage = Texts.Language;

    public ReaderGroupsPresentationStateRegressionTests()
    {
        Texts.Initialize();
        Texts.SetLanguage("en");
    }

    public void Dispose()
    {
        Texts.Initialize();
        Texts.SetLanguage(_previousLanguage);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SwitchingLanguagePreservesControlSelectionEditingStateAndCleanOrDirtyDraft(bool dirty, bool editable)
    {
        using var fixture = new PresentationFixture(dirty, editable);
        var before = fixture.Capture();
        fixture.AssertNonDefaultSetup(dirty, editable);

        foreach (string language in new[] { "de", "fr", "en" })
        {
            int refreshes = fixture.Refreshes;
            string previousDisplay = fixture.LocalMember.Display;
            Texts.SetLanguage(language);

            Assert.Equal(refreshes + 1, fixture.Refreshes);
            fixture.AssertUnchanged(before);
            // Observe the actual production row value directly. Binding delivery
            // belongs to the native smoke helper, not this detached-state fixture.
            Assert.NotEqual(previousDisplay, fixture.LocalMember.Display);
            Assert.EndsWith(Texts.T("readerGroups.localPosts"), fixture.LocalMember.Display);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SameCodePackReplacementPreservesStateAndReallyRefreshesTheExistingProductionRow(bool dirty, bool editable)
    {
        using TempDirectory directory = new();
        WritePack(directory.Path, "PACK-ONE");
        Texts.Initialize(directory.Path);
        Texts.SetLanguage("fr");
        using var fixture = new PresentationFixture(dirty, editable);
        var before = fixture.Capture();
        fixture.AssertNonDefaultSetup(dirty, editable);
        Assert.EndsWith(" · PACK-ONE", fixture.LocalMember.Display);

        // SetLanguage with the active code is deliberately not a refresh.
        Texts.SetLanguage("fr");
        Assert.Equal(0, fixture.Refreshes);
        fixture.AssertUnchanged(before);

        foreach (string marker in new[] { "PACK-TWO", "PACK-THREE" })
        {
            int refreshes = fixture.Refreshes;
            var languages = Texts.Languages;
            string previousDisplay = fixture.LocalMember.Display;
            WritePack(directory.Path, marker);
            Texts.Initialize(directory.Path);

            Assert.Equal("fr", Texts.Language);
            Assert.NotSame(languages, Texts.Languages);
            Assert.Empty(Texts.Warnings);
            Assert.Equal(refreshes + 1, fixture.Refreshes);
            Assert.NotEqual(previousDisplay, fixture.LocalMember.Display);
            Assert.EndsWith(" · " + marker, fixture.LocalMember.Display);
            fixture.AssertUnchanged(before);
        }
    }

    [Fact]
    public void InvalidRawNameAndMembershipOrderRemainDiscardableRatherThanBecomingAnAcceptedBaseline()
    {
        using TempDirectory directory = new();
        WritePack(directory.Path, "PACK-ONE");
        Texts.Initialize(directory.Path);
        Texts.SetLanguage("fr");
        using var fixture = new PresentationFixture(dirty: false, editable: true);
        // Stage an intentionally invalid name and a distinct reading order. These
        // are explicit fixture edits, not claims about the constructor's handlers.
        fixture.StageName("   ");
        fixture.Draft.Move(fixture.GroupId, 0, 1);
        var before = fixture.Capture();
        Assert.True(fixture.Draft.IsDirty);
        Assert.False(ReaderGroupsValidation.SameGroups(fixture.Saved.Groups, before.Snapshot.Groups));

        Texts.SetLanguage("de");
        fixture.AssertUnchanged(before);
        Texts.SetLanguage("fr");
        fixture.AssertUnchanged(before);
        WritePack(directory.Path, "PACK-TWO");
        Texts.Initialize(directory.Path);
        fixture.AssertUnchanged(before);

        // A language refresh must never Accept the dirty state or replace the
        // saved baseline: explicit Discard still restores its exact old order.
        fixture.Draft.Discard();
        Assert.False(fixture.Draft.IsDirty);
        Assert.Equal(fixture.Saved.Revision, fixture.Draft.Revision);
        Assert.True(ReaderGroupsValidation.SameGroups(fixture.Saved.Groups, fixture.Draft.Groups));
    }

    [Fact]
    public void GenuineLaterDraftChangesAndFullReversionStillCompareAgainstTheOriginalSavedState()
    {
        using var fixture = new PresentationFixture(dirty: true, editable: true);
        var before = fixture.Capture();
        Texts.SetLanguage("de");
        fixture.AssertUnchanged(before);

        // Continue staging into the same real TextBox and production draft.
        // No Save, Accept, host operation, or two-way event wiring is simulated.
        fixture.StageName("  later draft {0} / 日本語  ");
        Assert.Equal(fixture.GroupName.Text, fixture.Draft.Groups[1].Name);
        Assert.True(fixture.Draft.IsDirty);
        var later = fixture.Capture();
        Texts.SetLanguage("fr");
        fixture.AssertUnchanged(later);

        fixture.StageName(fixture.Saved.Groups[1].Name);
        Assert.False(fixture.Draft.IsDirty);
        var reverted = fixture.Capture();
        Texts.SetLanguage("en");
        fixture.AssertUnchanged(reverted);
        Assert.True(ReaderGroupsValidation.SameGroups(fixture.Saved.Groups, fixture.Draft.Groups));
    }

    private static void WritePack(string directory, string localPosts) => File.WriteAllText(
        Path.Combine(directory, "fr.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            code = "fr",
            nativeName = "Français state fixture",
            strings = new Dictionary<string, string> { ["readerGroups.localPosts"] = localPosts }
        }));

    private sealed class PresentationFixture : IDisposable
    {
        private readonly GroupRow[] _groups;
        private readonly MemberRow[] _members;
        private readonly MemberRow[] _candidates;
        private int _nameChanges, _searchChanges, _selectionChanges;

        internal Guid GroupId { get; } = Guid.Parse("73caacbc-37ed-4d14-adbc-fab82e172dd2");
        internal ReaderGroupsSnapshot Saved { get; }
        internal ReaderGroupsDraft Draft { get; }
        internal StackPanel Root { get; }
        internal ListBox Groups { get; }
        internal ListBox Members { get; }
        internal ListBox Candidates { get; }
        internal TextBox GroupName { get; } = new();
        internal TextBox Search { get; } = new() { MaxLength = 512 };
        internal MemberRow LocalMember => _members[0];
        internal int Refreshes { get; private set; }

        internal PresentationFixture(bool dirty, bool editable)
        {
            ResourceId a = new("fixture", "b", "1"), b = new("fixture", "b", "2"), c = new("fixture", "b", "3");
            Saved = new(1, 41,
            [
                new(Guid.Parse("4be7fd46-b6e1-4e87-9ad7-556d15268f4a"), "Other saved group", [c]),
                new(GroupId, "Chosen saved group", [a, b, c])
            ]);
            Draft = new(Saved);
            // Build the group row BEFORE renaming, as the dialog's draft record
            // replacement does not replace the existing row/control instance.
            _groups = Draft.Groups.Select(group => new GroupRow(group)).ToArray();
            _members = [new(new(a, "raw member {0}", true)), new(new(b, "watched", false, true)), new(new(c, "missing", false))];
            _candidates = [new(new(c, "third", false)), new(new(a, "first", true)), new(new(b, "second", false, true))];
            Groups = new() { ItemsSource = _groups, SelectedItem = _groups[1] };
            Members = new() { ItemsSource = _members, SelectedItem = _members[1] };
            Candidates = new() { ItemsSource = _candidates, SelectionMode = SelectionMode.Multiple, IsVisible = false, IsEnabled = editable };
            Candidates.SelectedItems!.Add(_candidates[2]);
            Candidates.SelectedItems.Add(_candidates[0]); // reverse row order, retained even while hidden
            StageName(dirty ? "  unsaved {0} / 日本語  " : Saved.Groups[1].Name);
            GroupName.IsEnabled = Search.IsEnabled = editable;
            GroupName.IsReadOnly = Search.IsReadOnly = !editable;
            Search.Text = "fixture/b/3 {0}";
            GroupName.CaretIndex = 9;
            GroupName.SelectionStart = 2;
            GroupName.SelectionEnd = 9;
            Search.CaretIndex = 12;
            Search.SelectionStart = 3;
            Search.SelectionEnd = 12;
            Root = new() { Children = { Groups, GroupName, Members, Search, Candidates } };
            RefreshRows();

            // Observe real control events only; none of these handlers mutate
            // state or reproduce the production dialog's private edit handlers.
            GroupName.TextChanging += (_, _) => _nameChanges++;
            Search.TextChanging += (_, _) => _searchChanges++;
            foreach (ListBox list in new[] { Groups, Members, Candidates })
                list.SelectionChanged += (_, _) => _selectionChanges++;
            Texts.Changed += CatalogChanged;
        }

        private void CatalogChanged(object? sender, EventArgs args)
        {
            Refreshes++;
            RefreshRows();
        }

        private void RefreshRows() => ReaderGroupsDialog.RefreshRows(
            Groups.Items.OfType<GroupRow>(),
            new[] { Members, Candidates }.SelectMany(list => list.Items.OfType<MemberRow>()), Draft);

        internal void StageName(string raw)
        {
            GroupName.Text = raw;
            Draft.Rename(GroupId, raw);
        }

        internal void AssertNonDefaultSetup(bool dirty, bool editable)
        {
            Assert.Equal(dirty, Draft.IsDirty);
            Assert.Equal(editable, GroupName.IsEnabled);
            Assert.Equal(!editable, GroupName.IsReadOnly);
            Assert.NotEqual(GroupName.SelectionStart, GroupName.SelectionEnd);
            Assert.NotEqual(Search.SelectionStart, Search.SelectionEnd);
            Assert.Same(_groups[1], Groups.SelectedItem);
            Assert.Same(_members[1], Members.SelectedItem);
            Assert.Equal(new object[] { _candidates[2], _candidates[0] }, Candidates.SelectedItems!.Cast<object>().ToArray());
            Assert.False(Candidates.IsVisible);
            Assert.Contains(Draft.Groups[1].Name, _groups[1].Display);
        }

        internal State Capture() => new(
            Draft.Groups, new(1, Draft.Revision, Draft.Groups.Select(group => group with { Members = group.Members.ToArray() }).ToArray()),
            Draft.IsDirty, Root.Children.ToArray(),
            new[] { Groups, Members, Candidates }.Select(list => new ListState(list.ItemsSource!, list.Items.Cast<object>().ToArray(),
                list.SelectedItems!.Cast<object>().ToArray(), list.IsVisible, list.IsEnabled)).ToArray(),
            InputState.Capture(GroupName), InputState.Capture(Search), (_nameChanges, _searchChanges, _selectionChanges));

        internal void AssertUnchanged(State expected)
        {
            Assert.Same(expected.DraftGroups, Draft.Groups);
            Assert.Equal(expected.Snapshot.Revision, Draft.Revision);
            Assert.True(ReaderGroupsValidation.SameGroups(expected.Snapshot.Groups, Draft.Groups));
            Assert.Equal(expected.Dirty, Draft.IsDirty);
            Assert.Equal(expected.Controls.Length, Root.Children.Count);
            for (int i = 0; i < expected.Controls.Length; i++) Assert.Same(expected.Controls[i], Root.Children[i]);
            int index = 0;
            foreach (ListBox list in new[] { Groups, Members, Candidates })
            {
                ListState saved = expected.Lists[index++];
                Assert.Same(saved.Source, list.ItemsSource);
                Assert.Equal(saved.Items.Length, list.ItemCount);
                for (int i = 0; i < saved.Items.Length; i++) Assert.Same(saved.Items[i], list.Items[i]);
                Assert.Equal(saved.Selected.Length, list.SelectedItems!.Count);
                for (int i = 0; i < saved.Selected.Length; i++) Assert.Same(saved.Selected[i], list.SelectedItems[i]);
                Assert.Equal(saved.Visible, list.IsVisible);
                Assert.Equal(saved.Enabled, list.IsEnabled);
            }
            Assert.Equal(expected.Name, InputState.Capture(GroupName));
            Assert.Equal(expected.Search, InputState.Capture(Search));
            Assert.Equal(expected.Events, (_nameChanges, _searchChanges, _selectionChanges));
        }

        public void Dispose() => Texts.Changed -= CatalogChanged;

        internal sealed record InputState(string? Text, int Caret, int Start, int End, bool Enabled, bool ReadOnly)
        {
            internal static InputState Capture(TextBox control) => new(control.Text, control.CaretIndex,
                control.SelectionStart, control.SelectionEnd, control.IsEnabled, control.IsReadOnly);
        }
        internal sealed record ListState(object Source, object[] Items, object[] Selected, bool Visible, bool Enabled);
        internal sealed record State(List<ReaderGroup> DraftGroups, ReaderGroupsSnapshot Snapshot, bool Dirty,
            Control[] Controls, ListState[] Lists, InputState Name, InputState Search, (int Name, int Search, int Selection) Events);
    }
}
