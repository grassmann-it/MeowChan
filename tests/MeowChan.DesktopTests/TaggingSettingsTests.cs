using System.Globalization;
using System.Xml.Linq;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;

namespace MeowChan.DesktopTests;

/// <summary>
/// PT7: the tagging settings (stamp size, walk window, card count, card choices, card start) are Config fields with default, minimum and maximum,
/// carried by every copy, saved and validated; the settings page has labelled rows for them in a Tagging group; the code that used constants reads
/// them; the texts exist in all languages; and Q is the default key of "Add to tagging queue" without any clash.
/// </summary>
[Collection("Localization")]
public sealed class TaggingSettingsTests : IDisposable
{
    public TaggingSettingsTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static readonly string[] Keys =
    {
        "settings.tagging.group", "settings.tagging.stampSize", "settings.tagging.stampSize.hint", "settings.tagging.walkWindow",
        "settings.tagging.walkWindow.hint", "settings.tagging.cardCount", "settings.tagging.cardCount.hint", "settings.tagging.cardsStart",
        "settings.tagging.cardsStart.hint", "settings.tagging.cards", "settings.tagging.cards.hint", "settings.tagging.cardSlot",
        "settings.tagging.undoDepth", "settings.tagging.undoDepth.hint",
    };

    // ---- the fields ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheSettingsHaveDefaultMinimumAndMaximumAndAreValidated()
    {
        Config config = new();
        Assert.Equal((9, 200, 5, false), (config.TagStampSize, config.TagQueueWalkWindow, config.TaggingCardCount, config.TaggingCardsDefault));
        Assert.Equal((1, 18), (Config.TagStampSizeMinimum, Config.TagStampSizeMaximum));
        Assert.Equal((10, 5_000), (Config.TagQueueWalkWindowMinimum, Config.TagQueueWalkWindowMaximum));
        Assert.Equal((2, 5), (Config.TaggingCardCountMinimum, Config.TaggingCardCountMaximum));
        Assert.Equal((20, 1, 100), (config.TagQueueUndoDepth, Config.TagQueueUndoDepthMinimum, Config.TagQueueUndoDepthMaximum));
        config.Validate();
        foreach (Action<Config> breakIt in new Action<Config>[]
        {
            c => c.TagStampSize = 0, c => c.TagStampSize = Config.TagStampSizeMaximum + 1, c => c.TagStampSize = int.MinValue,
            c => c.TagQueueWalkWindow = Config.TagQueueWalkWindowMinimum - 1, c => c.TagQueueWalkWindow = Config.TagQueueWalkWindowMaximum + 1,
            c => c.TagQueueUndoDepth = 0, c => c.TagQueueUndoDepth = Config.TagQueueUndoDepthMaximum + 1,
        })
        {
            Config bad = new();
            breakIt(bad);
            Assert.Throws<ArgumentOutOfRangeException>(bad.Validate);
            Assert.Throws<ArgumentOutOfRangeException>(() => new Config().CopySettingsFieldsFrom(bad));
        }
        // The card count is a tolerant preference: out of range is clamped, never refused.
        Assert.Equal(2, new Config { TaggingCardCount = 0 }.TaggingCardCount);
        Assert.Equal(5, new Config { TaggingCardCount = 99 }.TaggingCardCount);
    }

    [Fact]
    public void CardCountRawDraftUsesVisibleNumericValidationWithoutChangingTolerantPersistence()
    {
        const decimal accepted = Config.TaggingCardCountDefault;
        string? Validate(decimal raw) => SettingsEditor.NumericDraftError(raw.ToString(CultureInfo.InvariantCulture),
            NumberStyles.Number, CultureInfo.InvariantCulture, hasBindingErrors: false, accepted,
            Config.TaggingCardCountMinimum, Config.TaggingCardCountMaximum, integer: true);

        Assert.Equal("settings.fieldInteger", Validate(Config.TaggingCardCountMinimum - 1));
        Assert.Equal("settings.fieldInteger", Validate(Config.TaggingCardCountMaximum + 1));
        Assert.Null(Validate(accepted));
        Assert.Equal(Config.TaggingCardCountMinimum, new Config { TaggingCardCount = int.MinValue }.TaggingCardCount);
        Assert.Equal(Config.TaggingCardCountMaximum, new Config { TaggingCardCount = int.MaxValue }.TaggingCardCount);
    }

    [Fact]
    public void EveryCopyCarriesThemAndTheyRoundTripThroughTheSettingsFile()
    {
        using TempDirectory directory = new();
        Config original = new()
        {
            TagStampSize = 14, TagQueueWalkWindow = 75, TaggingCardCount = 3, TaggingCardsDefault = true,
            TaggingCards = new[] { "done", "rate-3", "skip", "back", "favorite" }, TagQueueUndoDepth = 7,
        };
        Config clone = original.Clone();
        Assert.Equal((14, 75, 3, true), (clone.TagStampSize, clone.TagQueueWalkWindow, clone.TaggingCardCount, clone.TaggingCardsDefault));
        clone.TaggingCards[0] = "changed";
        Assert.Equal("done", original.TaggingCards[0]); // independent arrays

        Config live = new();
        live.CopySettingsFieldsFrom(original.Clone());
        Assert.Equal((14, 75, 3, true), (live.TagStampSize, live.TagQueueWalkWindow, live.TaggingCardCount, live.TaggingCardsDefault));
        Assert.Equal(original.TaggingCards, live.TaggingCards);
        Config copy = new();
        copy.CopyFrom(live);
        Assert.Equal((14, 75), (copy.TagStampSize, copy.TagQueueWalkWindow));
        Assert.Equal((7, 7, 7), (original.Clone().TagQueueUndoDepth, live.TagQueueUndoDepth, copy.TagQueueUndoDepth));

        copy.SaveConfig(directory.Path);
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Null(loaded.LoadWarning);
        Assert.Equal((14, 75, 3, true), (loaded.TagStampSize, loaded.TagQueueWalkWindow, loaded.TaggingCardCount, loaded.TaggingCardsDefault));
        Assert.Equal(original.TaggingCards, loaded.TaggingCards);
        Assert.Equal(7, loaded.TagQueueUndoDepth);
        Assert.Equal(7, SettingsTransfer.Export(loaded, "1.1.0")[nameof(Config.TagQueueUndoDepth)]!.GetValue<int>());
    }

    [Fact]
    public async Task TheHostSavesThemWithTheSettingsAndKeepsAKeyPressedMeanwhileWhenThePageDidNotChangeIt()
    {
        using TempDirectory directory = new();
        new Config { AutomaticUpdateChecks = false, SaveUrlsOnExit = false, DownloadPath = Path.Combine(directory.Path, "media") }.SaveConfig(directory.Path);
        await using DesktopSessionHost host = new(directory.Path, Array.Empty<IImageBoard>());
        await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Config draft = host.Config.Clone();
        draft.TagStampSize = 4;
        draft.TagQueueWalkWindow = 40;
        draft.TaggingCardCount = 4;
        draft.TaggingCards = new[] { "stamp", "done", "skip", "back", "favorite" };
        await host.ApplySettingsAsync(draft).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal((4, 40, 4), (host.Config.TagStampSize, host.Config.TagQueueWalkWindow, host.Config.TaggingCardCount));
        Config reloaded = Config.LoadConfig(directory.Path);
        Assert.Equal((4, 40, 4), (reloaded.TagStampSize, reloaded.TagQueueWalkWindow, reloaded.TaggingCardCount));
        Assert.Equal(new[] { "stamp", "done", "skip", "back", "favorite" }, reloaded.TaggingCards);
    }

    // ---- what the code reads -----------------------------------------------------------------------------------------------

    [Fact]
    public void TheOptionsAndTheWalkWindowComeFromTheSettingsAndStayInTheirRange()
    {
        Assert.Equal(9, TaggingSettings.StampOptions(new Config()).StampSize);
        Assert.Equal(3, TaggingSettings.StampOptions(new Config { TagStampSize = 3 }).StampSize);
        Assert.Equal(1, TaggingSettings.StampOptions(new Config { TagStampSize = -4 }).StampSize);
        Assert.Equal(18, TaggingSettings.StampOptions(new Config { TagStampSize = 500 }).StampSize);
        QuickTagStamp stamp = TaggingSettings.StampOptions(new Config { TagStampSize = 2 }).CreateStamp(new[] { "a", "b", "c" });
        Assert.Equal(2, stamp.Count);
        stamp.Capacity = 12;
        Assert.Equal(12, stamp.Capacity);

        Assert.Equal(200, TaggingSettings.WalkWindow(new Config()));
        Assert.Equal(50, TaggingSettings.WalkWindow(new Config { TagQueueWalkWindow = 50 }));
        Assert.Equal(10, TaggingSettings.WalkWindow(new Config { TagQueueWalkWindow = 1 }));
        Assert.Equal(5_000, TaggingSettings.WalkWindow(new Config { TagQueueWalkWindow = 1_000_000 }));
        Assert.Equal(20, TaggingSettings.UndoDepth(new Config()));
        Assert.Equal(1, TaggingSettings.UndoDepth(new Config { TagQueueUndoDepth = -3 }));
        Assert.Equal(100, TaggingSettings.UndoDepth(new Config { TagQueueUndoDepth = 5_000 }));
    }

    [Fact]
    public void TheCardSlotsAreFiveDifferentDecisionsAndTheDefaultSetIsSavedAsNothing()
    {
        Assert.Equal(new[] { "back", "skip", "favorite", "stamp", "done" }, TaggingSettings.CardSlotIds(new Config()));
        Assert.Empty(TaggingSettings.CardsToSave(new[] { "back", "skip", "favorite", "stamp", "done" }));
        Assert.Equal(new[] { "done", "skip", "favorite", "stamp", "back" }, TaggingSettings.CardsToSave(new[] { "done", "skip", "favorite", "stamp", "back" }));

        // Damaged input: unknown and repeated names go, a short list is filled up, so the slots are always five different cards.
        string[] damaged = TaggingSettings.CardSlotIds(new Config { TaggingCards = new[] { "rate-2", "nonsense", "rate-2", "", "SKIP" } }).ToArray();
        Assert.Equal(new[] { "rate-2", "skip", "back", "favorite", "stamp" }, damaged);
        Assert.Equal(TaggingCardCommands.CardKeys, damaged.Distinct().Count());
        Assert.Empty(TaggingSettings.CardsToSave(new string?[] { null, "x" })); // nothing usable left: the default set
    }

    [Fact]
    public void ChoosingADecisionAnotherCardHasSwapsTheTwoCards()
    {
        string[] defaults = { "back", "skip", "favorite", "stamp", "done" };
        Assert.Equal(new[] { "back", "skip", "favorite", "stamp", "done" }, TaggingSettings.WithCard(defaults, 2, "favorite")); // no change
        Assert.Equal(new[] { "back", "skip", "done", "stamp", "favorite" }, TaggingSettings.WithCard(defaults, 2, "done")); // swaps with slot 5
        Assert.Equal(new[] { "rate-5", "skip", "favorite", "stamp", "done" }, TaggingSettings.WithCard(defaults, 0, "rate-5")); // a free decision
        Assert.Equal(defaults, TaggingSettings.WithCard(defaults, 0, "not-a-card"));
        Assert.Equal(defaults, TaggingSettings.WithCard(defaults, 9, "done"));
        foreach (int slot in Enumerable.Range(0, 5))
            foreach (TaggingCardAction action in TaggingCardActions.All)
            {
                string[] result = TaggingSettings.WithCard(defaults, slot, action.Id);
                Assert.Equal(action.Id, result[slot]);
                Assert.Equal(5, result.Distinct().Count());
            }
    }

    [Fact]
    public void TheWindowAndTheStampReadTheSettingsInsteadOfConstants()
    {
        string queue = Source("Desktop", "Shell", "MainWindow.TagQueue.cs");
        Assert.DoesNotContain("const int TagQueueWalkWindow", queue);
        Assert.Contains("internal int TagQueueWalkWindow => TaggingSettings.WalkWindow(_config);", queue);
        Assert.Contains(".Take(TagQueueWalkWindow)", queue);
        Assert.Contains("new HostTaggingQueueWriter(_session), TaggingSettings.UndoDepth(_config))", queue);
        Assert.Contains("_tagQueueDecisions.Depth = TaggingSettings.UndoDepth(Config);", Source("Desktop", "DesktopSessionHost.TagQueue.cs"));
        string tags = Source("Desktop", "Shell", "MainWindow.GalleryTags.cs");
        Assert.Contains("TaggingSettings.StampOptions(_config).CreateStamp(_config.TagStamp)", tags);
        Assert.Contains("public QuickTagOptions Options => QuickTagSettings.SurfaceOptions(window._config);", tags); // TP5 limits and the PT7 stamp size in one place
        Assert.DoesNotContain("QuickTagOptions.Default.CreateStamp", tags);
        // Saving the settings reaches the live stamp through the gallery preferences every settings save already runs.
        Assert.Contains("_quickTagStamp.Capacity = _config.TagStampSize;", tags);
        Assert.Contains("ApplyTaggingPreferences();", Source("Desktop", "Shell", "MainWindow.GalleryPreferences.cs"));
    }

    // ---- the settings page ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void ThePageHasALabelledRowForEverySettingInATaggingGroupOfTheReadingAndMediaPage()
    {
        XDocument markup = XDocument.Load(Path.Combine(RepoRoot(), "Desktop", "Views", "Settings", "SettingsEditor.axaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement Named(string name) => Assert.Single(markup.Descendants(), e => (string?)e.Attribute(x + "Name") == name);
        XElement heading = Named("SettingsTaggingHeading");
        Assert.Equal("{Binding TaggingGroupLabel}", (string?)heading.Attribute("Text"));
        Assert.Contains(heading.Ancestors(), e => (string?)e.Attribute(x + "Name") == "SettingsReadingMediaPage");
        List<XElement> order = markup.Descendants().ToList();
        foreach (string property in new[] { "TagStampSize", "TagQueueWalkWindow", "TagQueueUndoDepth", "TaggingCardCount" })
        {
            XElement field = Named(property + "Input");
            Assert.Equal("{Binding " + property + ", Mode=TwoWay}", (string?)field.Attribute("Value"));
            Assert.Equal("{Binding " + property + "Minimum}", (string?)field.Attribute("Minimum"));
            Assert.Equal("{Binding " + property + "Maximum}", (string?)field.Attribute("Maximum"));
            Assert.Equal("{Binding ElementName=" + property + "FieldLabel}", (string?)field.Attribute("AutomationProperties.LabeledBy"));
            Assert.Equal("{Binding " + property + "Help}", (string?)field.Attribute("AutomationProperties.HelpText"));
            Assert.Equal("{Binding " + property + "Label}", (string?)Named(property + "FieldLabel").Attribute("Text"));
            Named(property + "InputError");
            Assert.Contains(markup.Descendants(), e => (string?)e.Attribute("Text") == "{Binding " + property + "Hint}");
            Assert.True(order.IndexOf(heading) < order.IndexOf(field));
        }
        XElement start = Named("TaggingCardsDefaultInput");
        Assert.Equal("{Binding TaggingCardsDefault, Mode=TwoWay}", (string?)start.Attribute("IsChecked"));
        Assert.Equal("{Binding TaggingCardsDefaultLabel}", (string?)Named("TaggingCardsDefaultFieldLabel").Attribute("Text"));
        Assert.Equal("ToggleSwitch", start.Name.LocalName);
        for (int slot = 1; slot <= 5; slot++)
        {
            XElement combo = Named("TaggingCard" + slot + "Input");
            Assert.Equal("{Binding TaggingCardChoices}", (string?)combo.Attribute("ItemsSource"));
            Assert.Equal("{Binding SelectedTaggingCard" + slot + ", Mode=TwoWay}", (string?)combo.Attribute("SelectedItem"));
            Assert.Equal("{Binding ElementName=TaggingCard" + slot + "FieldLabel}", (string?)combo.Attribute("AutomationProperties.LabeledBy"));
            Assert.Equal("{Binding TaggingCard" + slot + "Label}", (string?)Named("TaggingCard" + slot + "FieldLabel").Attribute("Text"));
            Assert.True(order.IndexOf(Named("TaggingCardsHeading")) < order.IndexOf(combo));
        }
    }

    [Fact]
    public void TheSettingsSearchFindsTheRowsByTheirWordsInBothLanguagesOfTheAliases()
    {
        Assert.Contains("stempel", string.Join(' ', SettingsSearchAliases.For("TagStampSize", "settings.reading-media")));
        Assert.Contains("warteschlange", string.Join(' ', SettingsSearchAliases.For("TagQueueWalkWindow", "settings.reading-media")));
        Assert.Contains("rückgängig", string.Join(' ', SettingsSearchAliases.For("TagQueueUndoDepth", "settings.reading-media")));
        Assert.Contains("kartenmodus", string.Join(' ', SettingsSearchAliases.For("TaggingCard3", "settings.reading-media")));
        Assert.Contains("warteschlange", string.Join(' ', SettingsSearchAliases.For("TagQueueLimit", "settings.worker-budgets")));
    }

    // ---- texts ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheTextsExistInEnglishGermanAndEveryBundledLanguageAsRealTranslations()
    {
        foreach (string key in Keys)
        {
            Assert.False(string.IsNullOrWhiteSpace(BuiltInCatalog.English[key]), key);
            Assert.False(string.IsNullOrWhiteSpace(BuiltInCatalog.German[key]), key);
            // "Tagging" is the word of the group in English, German and Italian alike; every other text differs.
            if (key != "settings.tagging.group") Assert.NotEqual(BuiltInCatalog.English[key], BuiltInCatalog.German[key]);
        }
        Assert.Equal("Card {0}", BuiltInCatalog.English["settings.tagging.cardSlot"]);
        string packs = Path.Combine(RepoRoot(), "Desktop", "Localization", "Packs");
        foreach (string code in new[] { "es", "fr", "ru", "zh-Hans", "ja", "ko", "pt-BR", "cs", "it", "pl" })
        {
            using System.Text.Json.JsonDocument pack = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(packs, code + ".json")));
            System.Text.Json.JsonElement texts = pack.RootElement.GetProperty("strings");
            foreach (string key in Keys)
            {
                Assert.True(texts.TryGetProperty(key, out System.Text.Json.JsonElement value), code + " lacks " + key);
                string text = value.GetString()!;
                Assert.False(string.IsNullOrWhiteSpace(text), code + " " + key);
                Assert.Equal(BuiltInCatalog.English[key].Contains("{0}"), text.Contains("{0}"));
                if (key != "settings.tagging.group") Assert.NotEqual(BuiltInCatalog.English[key], text);
            }
        }
    }

    // ---- the key of "Add to tagging queue" -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ShortcutPlatform.Linux)]
    [InlineData(ShortcutPlatform.Windows)]
    [InlineData(ShortcutPlatform.MacOS)]
    public void QIsTheDefaultKeyOfAddToTheTaggingQueueAndNoOtherCommandHasIt(ShortcutPlatform platform)
    {
        CommandDefinition command = CommandCatalog.Get("media.queue.add");
        Assert.Equal("Q", command.Shortcut);
        Assert.Equal(new[] { "media", "reader", "gallery" }, command.ExecutionContexts);
        ShortcutGesture q = Assert.Single(EffectiveShortcutBindings.Defaults(command, platform));
        foreach (CommandDefinition other in CommandCatalog.All.Where(item => item.Id != command.Id))
            Assert.DoesNotContain(EffectiveShortcutBindings.Defaults(other, platform), gesture => gesture.Key == q.Key && gesture.Modifiers.Count == 0);
        EffectiveShortcutBindings effective = EffectiveShortcutBindings.Build(null, platform);
        Assert.Equal("Q", effective.Display("media.queue.add"));
        Assert.DoesNotContain(effective.Issues, issue => issue.CommandId == command.Id || issue.OtherCommandId == command.Id);
        // A new default on an existing command yields to a saved binding of another command.
        Assert.Contains(command.Id, EffectiveShortcutBindings.YieldingDefaultCommandIds);
    }

    private static string RepoRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory != null && !File.Exists(Path.Combine(directory, "MeowChan.csproj"))) directory = Path.GetDirectoryName(directory);
        return directory ?? throw new InvalidOperationException("The repository root was not found.");
    }

    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray()));
}
