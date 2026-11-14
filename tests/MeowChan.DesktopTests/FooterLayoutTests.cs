using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;

namespace MeowChan.DesktopTests;

/// <summary>Reads and changes MainPageOrder.Numbering (process-global): runs alone, after the parallel tests.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FooterNumberingCollection { public const string Name = "Footer numbering"; }

/// <summary>F1 (footer setting): the layout value, its stored form, the profile setting and the digits that follow the visible footer buttons.</summary>
public sealed class FooterLayoutTests
{
    private static readonly string[] Pages = MainPageOrder.CommandIds.ToArray();

    [Fact]
    public void TheDefaultLayoutIsTodaysFooterWithBooruHiddenAndStoresNothing()
    {
        FooterLayout layout = FooterLayout.Default;
        Assert.Equal(Pages, layout.Order);
        Assert.Equal(Pages.Where(id => id != "navigation.booru"), layout.VisibleOrder);
        Assert.Equal(new[] { "navigation.booru" }, layout.HiddenOrder);
        Assert.False(layout.IsVisible("navigation.booru"));
        Assert.All(layout.VisibleOrder, id => Assert.True(layout.IsVisible(id)));
        Assert.True(layout.IsDefault);
        Assert.Empty(layout.ToStored());
        Assert.Equal(layout.VisibleOrder.Concat(layout.HiddenOrder), layout.Numbering);
        Assert.Equal(MainPageOrder.DefaultNumbering, layout.Numbering);
        Assert.Same(FooterLayout.Default, FooterLayout.Default);
        Assert.Equal(layout, FooterLayout.FromStored(null));
        Assert.Equal(layout, FooterLayout.FromStored(Array.Empty<string>()));
    }

    [Fact]
    public void StoredFormIsTheOrderWithAMinusForHiddenPagesAndRoundTrips()
    {
        FooterLayout changed = FooterLayout.Default.Move("navigation.status", -1).Move("navigation.status", -1)
            .WithVisible("navigation.booru", true).WithVisible("navigation.rules", false);
        string[] stored = changed.ToStored();
        Assert.Equal(Pages.Length, stored.Length);
        Assert.Contains("-rules", stored);
        Assert.Contains("booru", stored);
        Assert.DoesNotContain(stored, id => id.Contains("navigation.", StringComparison.Ordinal));
        FooterLayout again = FooterLayout.FromStored(stored);
        Assert.Equal(changed, again);
        Assert.Equal(changed.Order, again.Order);
        Assert.Equal(changed.VisibleOrder, again.VisibleOrder);
        // The command-id spelling is read too.
        Assert.Equal(changed, FooterLayout.FromStored(stored.Select(id => id.StartsWith('-') ? "-navigation." + id[1..] : "navigation." + id)));
        Assert.Equal(stored, again.ToStored());
    }

    [Fact]
    public void UnknownIdsRepeatsAndBlanksAreIgnoredAndMissingPagesTakeTheirDefaultPlace()
    {
        FooterLayout layout = FooterLayout.FromStored(["status", "bogus", "", null, "  ", "navigation.plugins", "status", "-unknown", "archive", "downloads", "-" + "x".PadRight(70, 'x')]);
        // The listed pages keep their order; the others come in behind the nearest default predecessor that is placed.
        Assert.Equal(Pages.Length, layout.Order.Count);
        Assert.Equal(Pages.Order(), layout.Order.Order());
        Assert.True(layout.IndexOf("navigation.status") < layout.IndexOf("navigation.archive"));
        Assert.True(layout.IndexOf("navigation.archive") < layout.IndexOf("navigation.downloads"));
        // Media follows Archive in the default order; Tagging follows Media; Rules follows Booru (hidden by default, so it is placed behind Tagging's place).
        Assert.Equal(layout.IndexOf("navigation.archive") + 1, layout.IndexOf("navigation.media"));
        Assert.Equal(layout.IndexOf("navigation.media") + 1, layout.IndexOf("navigation.tagging"));
        Assert.False(layout.IsVisible("navigation.booru"));
        Assert.True(layout.IsVisible("navigation.media"));
    }

    [Fact]
    public void ANewPageAppearsAtItsDefaultPositionInAnOlderList()
    {
        // A list from a version without Tagging and Booru.
        FooterLayout layout = FooterLayout.FromStored(["status", "downloads", "archive", "media", "rules", "sources"]);
        Assert.Equal(new[] { "navigation.status", "navigation.downloads", "navigation.archive", "navigation.media", "navigation.tagging", "navigation.booru",
            "navigation.rules", "navigation.sources" }, layout.Order);
        Assert.Equal(new[] { "navigation.booru" }, layout.HiddenOrder); // Booru stays off until switched on
        Assert.True(layout.IsVisible("navigation.tagging"));
        // A list that knows no page at all is the default.
        Assert.True(FooterLayout.FromStored(["nothing", "-more"]).IsDefault);
    }

    [Fact]
    public void DownloadsCanNeverBeHiddenNotEvenByAStoredMark()
    {
        Assert.False(MainPageOrder.CanHideFromFooter("navigation.downloads"));
        Assert.True(Pages.Where(id => id != "navigation.downloads").All(MainPageOrder.CanHideFromFooter));
        Assert.True(FooterLayout.FromStored(["-downloads"]).IsVisible("navigation.downloads"));
        Assert.Same(FooterLayout.Default, FooterLayout.Default.WithVisible("navigation.downloads", false));
        Assert.Equal(FooterLayout.Default, FooterLayout.FromStored(["-downloads"]));
    }

    [Fact]
    public void MoveAndVisibilityEditsAreImmutableAndStopAtTheEdges()
    {
        FooterLayout start = FooterLayout.Default;
        Assert.Same(start, start.Move("navigation.downloads", -1));
        Assert.Same(start, start.Move("navigation.status", 1));
        Assert.Same(start, start.Move("navigation.media", 0));
        Assert.Same(start, start.Move("navigation.unknown", 1));
        Assert.Same(start, start.WithVisible("navigation.unknown", true));
        FooterLayout moved = start.Move("navigation.status", -1);
        Assert.Equal(start.Order.Count, moved.Order.Count);
        Assert.Equal(start.IndexOf("navigation.status") - 1, moved.IndexOf("navigation.status"));
        Assert.Equal(Pages, start.Order); // the original did not change
        FooterLayout hidden = moved.WithVisible("navigation.rules", false);
        Assert.False(hidden.IsVisible("navigation.rules"));
        Assert.Equal(moved.IndexOf("navigation.rules"), hidden.IndexOf("navigation.rules")); // a hidden page keeps its place
        Assert.Equal(hidden.Order, hidden.WithVisible("navigation.rules", true).Order);
        Assert.Equal(moved, hidden.WithVisible("navigation.rules", true));
    }

    [Fact]
    public void SetPageVisibleIsThePureEntryForAHintThatOffersAPage()
    {
        string[] shown = FooterLayout.SetPageVisible(null, "booru", true);
        Assert.True(FooterLayout.FromStored(shown).IsVisible("navigation.booru"));
        Assert.Equal(FooterLayout.Default.Order, FooterLayout.FromStored(shown).Order); // it sits at its default position
        Assert.Equal(shown, FooterLayout.SetPageVisible(shown, "navigation.booru", true));
        string[] hiddenAgain = FooterLayout.SetPageVisible(shown, "booru", false);
        Assert.Empty(hiddenAgain); // back to the default: nothing stored
        Assert.Equal(Array.Empty<string>(), FooterLayout.SetPageVisible(null, "bogus", true));
        Assert.Empty(FooterLayout.SetPageVisible(null, "downloads", false));
        string[] custom = FooterLayout.Default.Move("navigation.media", -1).ToStored();
        Assert.Equal(custom, FooterLayout.SetPageVisible(custom, "bogus", false));
        Assert.Equal(FooterLayout.FromStored(custom).WithVisible("navigation.booru", true), FooterLayout.FromStored(FooterLayout.SetPageVisible(custom, "booru", true)));
    }

    [Theory]
    [InlineData(WorkView.Barebones)]
    [InlineData(WorkView.Simple)]
    [InlineData(WorkView.Advanced)]
    public void TheWorkViewIsTheUpperBoundTheSettingHidesButNeverForceShows(WorkView view)
    {
        FooterLayout everything = FooterLayout.Default.WithVisible("navigation.booru", true);
        foreach (string id in Pages)
        {
            bool allowed = WorkViewPolicy.FooterDirect(id, view);
            Assert.Equal(allowed, everything.ShowsDirectly(id, view));
            // Hiding is always honoured, showing never beats the work view.
            Assert.False(everything.WithVisible(id, false).ShowsDirectly(id, view) && id != "navigation.downloads");
        }
        Assert.Equal(everything.VisibleOrder.Where(id => WorkViewPolicy.FooterDirect(id, view)), everything.DirectOrder(view));
        FooterLayout none = Pages.Aggregate(FooterLayout.Default, (current, id) => current.WithVisible(id, false));
        Assert.Equal(new[] { "navigation.downloads" }, none.VisibleOrder);
        Assert.Equal(new[] { "navigation.downloads" }.Where(id => WorkViewPolicy.FooterDirect(id, view)), none.DirectOrder(view));
        // Barebones moves pages under More that the layout would show.
        if (view == WorkView.Barebones)
        {
            Assert.Contains(Pages, id => everything.IsVisible(id) && !everything.ShowsDirectly(id, view));
            Assert.True(everything.ShowsDirectly("navigation.downloads", view));
        }
    }

    [Fact]
    public void TheOverflowOrderOfTheMoreMenuStaysTheStaticBaseOrder()
    {
        Assert.Equal(Pages.Where(id => id != "navigation.downloads").Order(), MainPageOrder.OverflowOrder.Order());
        FooterLayout reordered = FooterLayout.Default.Move("navigation.status", -3);
        Assert.NotEqual(Pages, reordered.Order);
        Assert.Equal(MainPageOrder.OverflowOrder, MainPageOrder.OverflowOrder); // More and the ⋯ menu list the groups of the base order, not the footer order
    }

    // ---- profile setting -------------------------------------------------------------------------------------------------------------

    [Fact]
    public void AMissingSettingIsTheDefaultFooter()
    {
        Assert.Empty(new Config().FooterPages);
        using TempDirectory directory = new();
        File.WriteAllText(Path.Combine(directory.Path, "settings.json"), "{\"RecheckTimer\":120}");
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Empty(loaded.FooterPages);
        Assert.True(loaded.FooterLayout.IsDefault);
        Assert.Equal(120, loaded.RecheckTimer);
        Assert.Null(loaded.LoadWarning);
    }

    [Fact]
    public void EveryCopyAndPersistencePathKeepsTheLayout()
    {
        using TempDirectory directory = new();
        FooterLayout chosen = FooterLayout.Default.Move("navigation.sources", -2).WithVisible("navigation.booru", true).WithVisible("navigation.archive", false);
        Config source = new() { FooterPages = chosen.ToStored(), DownloadPath = Path.Combine(directory.Path, "media") };
        Assert.Equal(chosen, source.FooterLayout);
        Config clone = source.Clone();
        Config copy = new(); copy.CopyFrom(source);
        Config settings = new(); settings.CopySettingsFieldsFrom(source);
        foreach (Config config in new[] { clone, copy, settings })
        {
            Assert.Equal(source.FooterPages, config.FooterPages);
            Assert.Equal(chosen, config.FooterLayout);
        }
        // A copy owns its array: changing the source later does not reach it.
        source.FooterPages[0] = "-media";
        Assert.NotEqual(source.FooterPages, clone.FooterPages);
        Assert.Equal(chosen, clone.FooterLayout);
        source.FooterPages = chosen.ToStored();
        Assert.Equal(source.FooterPages, SettingsTransfer.CreatePortableSnapshot(source).FooterPages);
        var exported = SettingsTransfer.Export(source)[nameof(Config.FooterPages)]!.AsArray();
        Assert.Equal(chosen.ToStored(), exported.Select(node => node!.GetValue<string>()));
        copy.SaveConfig(directory.Path);
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Equal(chosen, loaded.FooterLayout);
        Assert.Equal(chosen.ToStored(), loaded.FooterPages);
        Assert.Null(loaded.LoadWarning);
    }

    [Fact]
    public void TheDefaultLayoutWritesAnEmptyListAndStaysTheDefaultAfterARoundTrip()
    {
        using TempDirectory directory = new();
        Config config = new() { FooterPages = FooterLayout.Default.ToStored() };
        config.SaveConfig(directory.Path);
        using JsonDocument written = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.Path, "settings.json")));
        Assert.Equal(0, written.RootElement.GetProperty(nameof(Config.FooterPages)).GetArrayLength());
        Assert.True(Config.LoadConfig(directory.Path).FooterLayout.IsDefault);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("0")]
    [InlineData("\"media\"")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("[1,2,{},null,[\"x\"],false]")]
    [InlineData("[\"nonsense\",\"-nonsense\"]")]
    public void ACorruptSettingFallsBackToTheDefaultWithoutRejectingTheOtherSettings(string token)
    {
        using TempDirectory directory = new();
        File.WriteAllText(Path.Combine(directory.Path, "settings.json"), "{\"FooterPages\":" + token + ",\"RecheckTimer\":120}");
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.True(loaded.FooterLayout.IsDefault);
        Assert.Empty(loaded.FooterPages);
        Assert.Equal(120, loaded.RecheckTimer);
        Assert.Null(loaded.LoadWarning);
    }

    [Fact]
    public void AHalfCorruptListKeepsWhatItCanReadAndRepairsTheRest()
    {
        using TempDirectory directory = new();
        File.WriteAllText(Path.Combine(directory.Path, "settings.json"),
            "{\"FooterPages\":[\"status\",7,\"downloads\",null,\"-rules\",\"downloads\",\"???\"],\"RecheckTimer\":120}");
        Config loaded = Config.LoadConfig(directory.Path);
        FooterLayout layout = loaded.FooterLayout;
        Assert.Equal(Pages.Length, layout.Order.Count);
        Assert.Equal("navigation.status", layout.Order[0]);
        Assert.Equal("navigation.downloads", layout.Order[1]);
        Assert.False(layout.IsVisible("navigation.rules"));
        Assert.Equal(layout.ToStored(), loaded.FooterPages); // the property normalises what it reads
        Assert.Equal(120, loaded.RecheckTimer);
        Assert.Null(loaded.LoadWarning);
    }

    [Fact]
    public void AnOverlongListIsBoundedAndStillValid()
    {
        using TempDirectory directory = new();
        string items = string.Join(",", Enumerable.Range(0, 500).Select(index => "\"p" + index + "\"").Concat(["\"-media\""]));
        File.WriteAllText(Path.Combine(directory.Path, "settings.json"), "{\"FooterPages\":[" + items + "]}");
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.True(loaded.FooterLayout.IsDefault); // 64 unknown entries were read, the rest dropped
        Assert.Null(loaded.LoadWarning);
    }

    // ---- digits follow the footer ---------------------------------------------------------------------------------------------------

    [Collection(FooterNumberingCollection.Name)]
    public sealed class Numbering : IDisposable
    {
        private readonly IReadOnlyList<string> _saved = MainPageOrder.Numbering;
        public void Dispose() => MainPageOrder.SetNumbering(_saved);

        [Fact]
        public void CtrlDigitsFollowTheVisibleFooterButtonsFromLeftToRightThenTheHiddenPages()
        {
            FooterLayout layout = FooterLayout.Default.Move("navigation.status", -1).Move("navigation.status", -1).WithVisible("navigation.archive", false);
            MainPageOrder.SetNumbering(layout.Numbering);
            string[] expected = layout.VisibleOrder.Concat(layout.HiddenOrder).ToArray();
            for (int index = 0; index < expected.Length; index++) Assert.Equal(index + 1, MainPageOrder.Digit(expected[index]));
            EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(platform: ShortcutPlatform.Linux);
            for (int index = 0; index < expected.Length; index++)
                Assert.Equal("Ctrl+" + (index + 1), bindings.Display(expected[index]));
            Assert.Empty(bindings.Issues);
            Assert.Equal(expected, MainPageOrder.NumberedCommandIds);
            Assert.Equal("Primary+" + MainPageOrder.Digit("navigation.status"), MainPageOrder.DefaultShortcut("navigation.status"));
            // The hidden Archive is numbered after the visible buttons, so it stays reachable by a digit.
            Assert.True(MainPageOrder.Digit("navigation.archive") > MainPageOrder.Digit(layout.VisibleOrder[^1]));
            // The Mac menu and the display on macOS read the same numbering.
            Assert.Equal("⌘" + MainPageOrder.Digit("navigation.status"), EffectiveShortcutBindings.Build(platform: ShortcutPlatform.MacOS).Display("navigation.status"));
        }

        [Fact]
        public void ChangingTheNumberingReachesTheBuiltBindingsAndTheDefaultComesBack()
        {
            Assert.Equal(1, MainPageOrder.Digit("navigation.downloads"));
            EffectiveShortcutBindings before = EffectiveShortcutBindings.Build(platform: ShortcutPlatform.Linux);
            Assert.Equal("Ctrl+7", before.Display("navigation.status"));
            Assert.True(MainPageOrder.SetNumbering(FooterLayout.Default.WithVisible("navigation.booru", true).Numbering));
            EffectiveShortcutBindings shown = EffectiveShortcutBindings.Build(platform: ShortcutPlatform.Linux);
            Assert.Equal("Ctrl+5", shown.Display("navigation.booru"));
            Assert.Equal("Ctrl+8", shown.Display("navigation.status"));
            Assert.False(MainPageOrder.SetNumbering(FooterLayout.Default.WithVisible("navigation.booru", true).Numbering)); // unchanged
            Assert.True(MainPageOrder.SetNumbering(MainPageOrder.DefaultNumbering));
            Assert.Equal("Ctrl+7", EffectiveShortcutBindings.Build(platform: ShortcutPlatform.Linux).Display("navigation.status"));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        public void ANumberingThatIsNoPermutationOfTheMainPagesIsIgnored(int kind)
        {
            IReadOnlyList<string> before = MainPageOrder.Numbering;
            IReadOnlyList<string>? bad = kind switch
            {
                0 => null,
                1 => Pages.Take(Pages.Length - 1).ToArray(),
                _ => Pages.Take(Pages.Length - 1).Append(Pages[0]).ToArray(),
            };
            Assert.False(MainPageOrder.SetNumbering(bad));
            Assert.Equal(before, MainPageOrder.Numbering);
            Assert.False(MainPageOrder.SetNumbering(Pages.Take(Pages.Length - 1).Append("navigation.settings").ToArray()));
            Assert.Equal(before, MainPageOrder.Numbering);
        }

        [Fact]
        public void ASavedBindingOfAnotherCommandKeepsItsKeyWhenTheFooterMovesAPageOntoIt()
        {
            // Rules is the fifth page in the default layout; the person saved Ctrl+4 for "Pause" long ago. Moving Rules in front of Tagging
            // gives Rules Ctrl+4 as its default - the default yields, the saved binding wins, nothing blocks saving.
            MainPageOrder.SetNumbering(FooterLayout.Default.Move("navigation.rules", -1).Move("navigation.rules", -1).Numbering);
            Assert.Equal(4, MainPageOrder.Digit("navigation.rules"));
            var section = new ShortcutBindingsSection().Set("session.pause", [ShortcutGesture.Parse("Primary+4")]);
            EffectiveShortcutBindings saved = EffectiveShortcutBindings.Build(section, ShortcutPlatform.Linux);
            Assert.Equal("Ctrl+4", saved.Display("session.pause"));
            Assert.Empty(saved.For("navigation.rules"));
            Assert.DoesNotContain(saved.Issues, issue => issue.BlocksSave);
        }

        [Fact]
        public void EveryMainPageKeepsADigitInAnyLayoutBecauseEightPagesFitInNine()
        {
            Assert.True(Pages.Length <= MainPageOrder.MaximumDigit);
            foreach (FooterLayout layout in new[]
            {
                FooterLayout.Default,
                Pages.Aggregate(FooterLayout.Default, (current, id) => current.WithVisible(id, false)),
                Pages.Aggregate(FooterLayout.Default, (current, id) => current.WithVisible(id, true)).Move("navigation.status", -7),
            })
            {
                MainPageOrder.SetNumbering(layout.Numbering);
                Assert.Equal(Enumerable.Range(1, Pages.Length), Pages.Select(id => MainPageOrder.Digit(id)!.Value).Order());
                EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(platform: ShortcutPlatform.Windows);
                Assert.All(Pages, id => Assert.Single(bindings.For(id)));
                Assert.Empty(bindings.Issues);
            }
        }
    }

    // ---- the list in Settings --------------------------------------------------------------------------------------------------------

    [Collection("Localization")]
    public sealed class Editor
    {
        public Editor() { Texts.Initialize(); Texts.SetLanguage("en"); }

        [Fact]
        public void EveryPageHasAToggleSwitchAndTwoMoveButtonsWithAutomationNamesAndDownloadsIsPinned()
        {
            OwnedUiDispatcher.Run(() =>
            {
                var editor = new FooterLayoutEditor();
                Assert.Equal(Pages.Length, editor.Rows.Children.Count);
                foreach (string id in Pages)
                {
                    ToggleSwitch check = Assert.IsType<ToggleSwitch>(editor.SwitchOf(id));
                    Assert.False(string.IsNullOrWhiteSpace(Avalonia.Automation.AutomationProperties.GetName(check)));
                    Assert.Equal(id != "navigation.downloads", check.IsEnabled);
                    Assert.Equal(FooterLayout.Default.IsVisible(id), check.IsChecked);
                    // SP1: the page name is the label of the row (the switch is the control at its end), not the content of the switch.
                    Assert.Null(check.OnContent);
                    Assert.Null(check.OffContent);
                    Assert.Equal(CommandCatalog.Label(id), editor.Rows.Children.OfType<Grid>().First(row => row.Tag as string == id).Children.OfType<TextBlock>().First(t => t.Name?.StartsWith("FooterPageLabel") == true).Text);
                    Assert.NotNull(editor.MoveButtonOf(id, up: true));
                    Assert.NotNull(editor.MoveButtonOf(id, up: false));
                    Assert.False(string.IsNullOrWhiteSpace(Avalonia.Automation.AutomationProperties.GetName(editor.MoveButtonOf(id, true)!)));
                    Assert.True(editor.SwitchOf(id)!.Focusable);
                }
                Assert.False(editor.MoveButtonOf(Pages[0], up: true)!.IsEnabled);
                Assert.False(editor.MoveButtonOf(Pages[^1], up: false)!.IsEnabled);
                Assert.True(editor.MoveButtonOf(Pages[1], up: true)!.IsEnabled);
                Assert.False(string.IsNullOrWhiteSpace(Avalonia.Automation.AutomationProperties.GetName(editor.ResetButton)));
            });
        }

        private static string? ShortcutTextOf(FooterLayoutEditor editor, string id) =>
            editor.Rows.Children.OfType<Grid>().First(row => row.Tag as string == id).Children.OfType<TextBlock>().First(t => t.Name?.StartsWith("FooterShortcut") == true).Text;

        // M6 (review wave 6): a toggle rebuilds the rows (the digits move), so the check box of the page must take the focus again.
        [Fact]
        public void TheFocusStaysOnTheToggledCheckBoxAndOnTheMovedButtonAfterTheRowsAreRebuilt()
        {
            OwnedUiDispatcher.Run(() =>
            {
                var editor = new FooterLayoutEditor();
                Assert.Null(editor.FocusTarget);
                CheckBox before = editor.CheckBoxOf("navigation.rules")!;
                before.IsChecked = false;
                Assert.False(editor.Layout.IsVisible("navigation.rules"));
                CheckBox after = editor.CheckBoxOf("navigation.rules")!;
                Assert.NotSame(before, after);
                Assert.Same(after, editor.FocusTarget);
                // Checking it again keeps the focus on the check box of that page, not on a neighbour.
                after.IsChecked = true;
                Assert.Same(editor.CheckBoxOf("navigation.rules"), editor.FocusTarget);
                // A move keeps it on the same kind of button of the page that moved, also at the edge (where it is disabled now).
                string page = "navigation.status";
                for (int step = 0; step < Pages.Length; step++)
                {
                    Button up = editor.MoveButtonOf(page, up: true)!;
                    if (!up.IsEnabled) break;
                    up.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Button upNow = editor.MoveButtonOf(page, up: true)!;
                    Assert.Same(upNow.IsEnabled ? upNow : editor.MoveButtonOf(page, up: false), editor.FocusTarget);
                }
                Assert.False(editor.MoveButtonOf(page, up: true)!.IsEnabled);
                Assert.Same(editor.MoveButtonOf(page, up: false), editor.FocusTarget);
                editor.MoveButtonOf(page, up: false)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Same(editor.MoveButtonOf(page, up: false), editor.FocusTarget);
                // Reset and Load rebuild without a focus request.
                editor.ResetButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Null(editor.FocusTarget);
            });
        }

        [Fact]
        public void ChecksAndMovesChangeTheDraftRaiseTheEventAndResetGoesBackToTheDefault()
        {
            OwnedUiDispatcher.Run(() =>
            {
                var editor = new FooterLayoutEditor();
                int raised = 0;
                editor.LayoutChanged += (_, _) => raised++;
                editor.SwitchOf("navigation.rules")!.IsChecked = false;
                Assert.Equal(1, raised);
                Assert.False(editor.Layout.IsVisible("navigation.rules"));
                editor.MoveButtonOf("navigation.status", up: true)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(2, raised);
                Assert.Equal(FooterLayout.Default.IndexOf("navigation.status") - 1, editor.Layout.IndexOf("navigation.status"));
                Assert.Contains(CommandCatalog.Label("navigation.status"), editor.StatusText.Text, StringComparison.Ordinal);
                editor.SwitchOf("navigation.booru")!.IsChecked = true;
                Assert.True(editor.Layout.IsVisible("navigation.booru"));
                // The digit of the draft is shown next to the page and moves with it.
                Assert.Contains("5", ShortcutTextOf(editor, "navigation.booru"));
                editor.ResetButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(editor.Layout.IsDefault);
                Assert.Equal(4, raised);
                // Load replaces the draft silently.
                editor.Load(FooterLayout.Default.WithVisible("navigation.media", false));
                Assert.Equal(4, raised);
                Assert.False(editor.SwitchOf("navigation.media")!.IsChecked);
            });
        }
    }

    // ---- texts -----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void EveryFooterTextExistsInAllTwelveLanguagesWithTheSamePlaceholders()
    {
        string[] keys = FooterSettingsCatalog.Entries.Select(entry => entry.Key).ToArray();
        Assert.Equal(12, keys.Length);
        Assert.All(keys, key => Assert.StartsWith("settings.footer.", key, StringComparison.Ordinal));
        Assert.Equal(keys.Length, keys.Distinct().Count());
        foreach (var entry in FooterSettingsCatalog.Entries)
        {
            Assert.Equal(entry.English, BuiltInCatalog.English[entry.Key]);
            Assert.Equal(entry.German, BuiltInCatalog.German[entry.Key]);
            Assert.NotEqual(entry.English, entry.German);
        }
        string[] codes = Texts.BundledPackCodes().ToArray();
        Assert.Equal(10, codes.Length);
        foreach (string code in codes)
        {
            using Stream stream = typeof(Texts).Assembly.GetManifestResourceStream(Texts.BundledPackResourcePrefix + code + ".json")!;
            using JsonDocument pack = JsonDocument.Parse(stream);
            JsonElement strings = pack.RootElement.GetProperty("strings");
            foreach (var entry in FooterSettingsCatalog.Entries)
            {
                Assert.True(strings.TryGetProperty(entry.Key, out JsonElement value), code + " lacks " + entry.Key);
                string text = value.GetString() ?? "";
                Assert.False(string.IsNullOrWhiteSpace(text), code + " " + entry.Key);
                Assert.NotEqual(entry.English, text); // a real translation, not the English text
                foreach (string placeholder in new[] { "{0}", "{1}", "{2}" })
                    Assert.Equal(entry.English.Contains(placeholder, StringComparison.Ordinal), text.Contains(placeholder, StringComparison.Ordinal));
            }
        }
    }
}
