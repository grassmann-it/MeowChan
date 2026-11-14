using Avalonia.Controls;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>The common context menu builder: sections, the entry kinds, the rules it enforces, and the Avalonia menu made from it.</summary>
[Collection("Localization")]
public sealed class ContextMenuBuilderTests : IDisposable
{
    public ContextMenuBuilderTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    [Fact]
    public void SectionsKeepTheirOrderAndAnEmptySectionLeavesNoSeparator()
    {
        ContextMenuModel model = new ContextMenuBuilder()
            .Section().Section()                       // nothing yet: no empty first section
            .Item("a", "A", () => { }).Item("b", "B", () => { })
            .Section().Section()                       // two in a row make one
            .Item("c", "C", () => { })
            .Section()                                 // trailing, empty
            .ToModel();
        Assert.Equal(new[] { new[] { "a", "b" }, new[] { "c" } }, model.Sections.Select(section => section.Select(entry => entry.Id).ToArray()).ToArray());
        Assert.False(model.IsEmpty);
        Assert.True(new ContextMenuBuilder().ToModel().IsEmpty);
        Assert.Null(new ContextMenuBuilder().Build());
    }

    [Fact]
    public void ACatalogCommandCarriesItsLabelAndAnOverrideReplacesIt()
    {
        ContextMenuModel model = new ContextMenuBuilder()
            .Command("navigation.statistics", () => { })
            .Command("navigation.settings", () => { }, label: "Settings: Statistics…")
            .ToModel();
        ContextMenuEntry[] entries = model.Entries.ToArray();
        Assert.Equal(CommandCatalog.Label("navigation.statistics"), entries[0].Label);
        Assert.Equal("navigation.statistics", entries[0].CommandId);
        Assert.Equal("Settings: Statistics…", entries[1].Label);
        Assert.Equal("navigation.settings", entries[1].CommandId);
    }

    [Fact]
    public void EntryIdsAreUniqueAndOnlyOneRadioOfAGroupIsChecked()
    {
        Assert.Throws<ArgumentException>(() => new ContextMenuBuilder().Item("a", "A", () => { }).Item("a", "Again", () => { }));
        ContextMenuBuilder radios = new ContextMenuBuilder().Radio("g", "g.1", "One", true, () => { });
        Assert.Throws<ArgumentException>(() => radios.Radio("g", "g.2", "Two", true, () => { }));
        // Another group is its own choice; an unchecked entry is always fine.
        radios.Radio("g", "g.3", "Three", false, () => { }).Radio("h", "h.1", "Other", true, () => { });
        Assert.Equal(2, radios.ToModel().Entries.Count(entry => entry.Checked));
    }

    [Fact]
    public void ANoteIsADisabledSentenceAndTwoNotesDoNotCollide()
    {
        ContextMenuModel model = new ContextMenuBuilder().Note("First.").Item("a", "A", () => { }).Note("Second.").ToModel();
        ContextMenuEntry[] notes = model.Entries.Where(entry => entry.Kind == ContextMenuEntryKind.Note).ToArray();
        Assert.Equal(2, notes.Length);
        Assert.All(notes, note => { Assert.False(note.Available); Assert.Null(note.Invoke); });
        Assert.NotEqual(notes[0].Id, notes[1].Id);
    }

    [Fact]
    public void ASectionOverTheCeilingIsReported()
    {
        ContextMenuBuilder menu = new();
        for (int index = 0; index < ContextMenuModel.MaxEntriesPerSection; index++) menu.Item("n" + index, "N" + index, () => { });
        Assert.Empty(menu.ToModel().OversizedSections);
        menu.Item("tenth", "Tenth", () => { });
        Assert.Equal(new[] { 0 }, menu.ToModel().OversizedSections);
        // A new section starts fresh; only the long one is named.
        Assert.Equal(new[] { 0 }, menu.Section().Item("next", "Next", () => { }).ToModel().OversizedSections);
    }

    [Fact]
    public void TheAvaloniaMenuHasASeparatorBetweenSectionsAndCarriesKindStateAndTag()
    {
        int chosen = 0, switched = 0, ran = 0;
        ContextMenu menu = new ContextMenuBuilder().Named("Test menu")
            .Radio("g", "g.a", "A", false, () => chosen++)
            .Radio("g", "g.b", "B", true, () => chosen += 10)
            .Section()
            .Check("c", "Switch", true, () => switched++)
            .Item("x", "Gone", () => ran++, available: false, reason: "Nothing to do.")
            .Item("y", "Run", () => ran += 10)
            .Build()!;
        object[] items = menu.Items.Cast<object>().ToArray();
        Assert.Equal(6, items.Length);
        Assert.IsType<Separator>(items[2]);
        MenuItem a = (MenuItem)items[0], b = (MenuItem)items[1], check = (MenuItem)items[3], gone = (MenuItem)items[4], run = (MenuItem)items[5];
        Assert.Equal(MenuItemToggleType.Radio, a.ToggleType);
        Assert.Equal("g", a.GroupName);
        Assert.False(a.IsChecked);
        Assert.True(b.IsChecked);
        Assert.Equal("g.b", b.Tag);
        Assert.Equal(MenuItemToggleType.CheckBox, check.ToggleType);
        Assert.True(check.IsChecked);
        Assert.False(gone.IsEnabled);
        Assert.True(run.IsEnabled);
        a.Command!.Execute(null);
        check.Command!.Execute(null);
        run.Command!.Execute(null);
        Assert.False(gone.Command!.CanExecute(null));
        Assert.Equal((1, 1, 10), (chosen, switched, ran));
        Assert.Equal("Test menu", Avalonia.Automation.AutomationProperties.GetName(menu));
    }
}
