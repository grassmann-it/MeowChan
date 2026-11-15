using System.Text.Json;
using Avalonia.Input;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Storage;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

// PT4 (PLAN-POWER-TAGGEN-2026-11-14 §1.5): the card mode of the tagging mode. K toggles it, five cards with keys 1 to 5 apply a decision
// (back, skip, done, favorite, stamp or a rating) through the walk's own decision log and undo, the cards are a setting with a default
// set and clamped count, the keys are catalog commands whose defaults yield, and every text exists in twelve languages. The cards
// themselves need a platform (the tagging-cards smoke covers them); the viewer's wiring is pinned through its source like PT3's.
[Collection("Localization")]
public sealed class CardModeTests : IDisposable
{
    public CardModeTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    public static IEnumerable<object[]> Platforms() => Enum.GetValues<ShortcutPlatform>().Select(platform => new object[] { platform });

    private static KeyEventArgs Press(Key key, KeyModifiers modifiers = KeyModifiers.None) => new() { Key = key, KeyModifiers = modifiers };

    // ---- keys ----------------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Platforms))]
    public void ShiftKTogglesTheCardModeAndTheCardKeysAre1To5OnEveryPlatformWithoutIssues(ShortcutPlatform platform)
    {
        var bindings = EffectiveShortcutBindings.Build(platform: platform);
        Assert.Empty(bindings.Issues);
        foreach (string id in TaggingCardCommands.All)
        {
            CommandDefinition command = CommandCatalog.Get(id);
            Assert.True(command.IsImplemented, id);
            Assert.Equal(CommandScope.Gallery, command.Scope);
            Assert.Equal(new[] { "gallery-tagging" }, command.ExecutionContexts);
            Assert.NotEmpty(bindings.For(id));
            foreach (ShortcutGesture gesture in bindings.For(id)) Assert.Null(EffectiveShortcutBindings.InvalidReason(gesture, platform));
        }
        Assert.Equal(platform == ShortcutPlatform.MacOS ? "⇧K" : "Shift+K", bindings.Display(TaggingCardCommands.Toggle)); // revision 13: the cards are a block, Shift plus a letter
        for (int number = 1; number <= TaggingCardCommands.CardKeys; number++)
            Assert.Equal(number.ToString(), bindings.Display(TaggingCardCommands.Card(number)));
    }

    [Fact]
    public void TheCardKeysAreThePlainDigitsAndNeverTheRatingOrSuggestionGestures()
    {
        var linux = EffectiveShortcutBindings.Build(platform: ShortcutPlatform.Linux);
        Assert.True(linux.Matches(Press(Key.K, KeyModifiers.Shift), TaggingCardCommands.Toggle));
        Assert.False(linux.Matches(Press(Key.K), TaggingCardCommands.Toggle));
        Key[] digits = { Key.D1, Key.D2, Key.D3, Key.D4, Key.D5 };
        for (int i = 0; i < digits.Length; i++)
        {
            string card = TaggingCardCommands.Card(i + 1);
            Assert.True(linux.Matches(Press(digits[i]), card));
            Assert.False(linux.Matches(Press(digits[i], KeyModifiers.Shift), card));          // Shift+n rates (PT6)
            Assert.False(linux.Matches(Press(digits[i], KeyModifiers.Alt), card));            // Alt+n toggles a suggestion (TP1)
            Assert.True(linux.Matches(Press(digits[i], KeyModifiers.Shift), "media.rating.set-" + (i + 1)));
            Assert.False(linux.Matches(Press(digits[i], KeyModifiers.Shift), "media.rating.clear"));
        }
        Assert.False(linux.Matches(Press(Key.D0), TaggingCardCommands.Card(1)));
        Assert.False(linux.Matches(Press(Key.D6), TaggingCardCommands.Card(5)));
    }

    [Fact]
    public void TheCardsOwnContextKeepsTheGalleryZoomKeyAndTheRatingKeysAsTheyAre()
    {
        // 1 is the gallery's actual size outside the mode; in the mode's own context the card key shares that gesture without a conflict.
        var bindings = EffectiveShortcutBindings.Build(platform: ShortcutPlatform.Linux);
        Assert.StartsWith("1", bindings.Display("gallery.actual-size"));
        foreach (string id in TaggingCardCommands.All)
            foreach (string other in new[] { "gallery.actual-size", "gallery.fit", "media.rating.set-1", "favorites.toggle", "gallery.next", "tags.quick.suggestion-1" })
                Assert.False(EffectiveShortcutBindings.ContextsOverlap(id, other), id + " / " + other);
        // Inside the mode the card keys sit beside Enter, Space and Backspace and never collide with them.
        foreach (string mode in new[] { "tags.queue.done-next", "tags.queue.skip", "tags.queue.back", "tags.queue.play-pause" })
            foreach (var card in bindings.For(TaggingCardCommands.Card(1)))
                Assert.DoesNotContain(bindings.For(mode), gesture => gesture.Key == card.Key && gesture.Modifiers.SequenceEqual(card.Modifiers));
    }

    [Theory]
    [MemberData(nameof(Platforms))]
    public void TheNewDefaultsAreInTheModesRevisionYieldToASavedBindingAndGoWithTheSingleKeySwitch(ShortcutPlatform platform)
    {
        Assert.True(EffectiveShortcutBindings.DefaultsRevision >= 8);
        foreach (string id in TaggingCardCommands.All) Assert.Contains(id, EffectiveShortcutBindings.YieldingDefaultCommandIds);

        // A saved binding of another command of the mode wins over the new default; nothing is silenced and nothing is reported as a conflict.
        var saved = new ShortcutBindingsSection().Set("tags.queue.skip", [ShortcutGesture.Parse("Shift+K")]);
        var bindings = EffectiveShortcutBindings.Build(saved, platform);
        Assert.Empty(bindings.For(TaggingCardCommands.Toggle));
        Assert.Contains(bindings.For("tags.queue.skip"), gesture => gesture.Key == "K" && gesture.Modifiers.Contains("Shift"));
        Assert.DoesNotContain(bindings.Issues, issue => issue.BlocksSave);

        var off = EffectiveShortcutBindings.Build(new ShortcutBindingsSection().WithSingleKeyShortcuts(false), platform);
        Assert.Empty(off.Issues);
        foreach (string id in TaggingCardCommands.All) Assert.Empty(off.For(id)); // the cards stay buttons: a click still works
    }

    // ---- the card setting --------------------------------------------------------------------------------------------

    [Fact]
    public void TheDefaultSetIsFiveCardsWithTheStampAndNoCardsModeAtStart()
    {
        TaggingCardSettings settings = TaggingCardSettings.Default;
        Assert.False(settings.StartInCards);
        Assert.Equal(new[] { "back", "skip", "favorite", "stamp", "done" }, settings.Cards.Select(card => card.Id));
        Assert.Equal(TaggingCardCommands.CardKeys, settings.Cards.Count);
        Assert.Equal((5, 2, 5), (Config.TaggingCardCountDefault, Config.TaggingCardCountMinimum, Config.TaggingCardCountMaximum));
        Assert.Equal(TaggingCardActions.DefaultIds, TaggingCardSettings.From(true, Array.Empty<string>(), 5).Cards.Select(card => card.Id));
    }

    [Fact]
    public void ANamedSetKeepsItsOrderDropsUnknownAndRepeatedNamesAndIsFilledUpToTheCount()
    {
        TaggingCardSettings named = TaggingCardSettings.From(true, new[] { "rate-5", "RATE-3", "nonsense", null, "rate-5", " done ", "skip", "favorite" }, 5);
        Assert.True(named.StartInCards);
        Assert.Equal(new[] { "rate-5", "rate-3", "done", "skip", "favorite" }, named.Cards.Select(card => card.Id));
        Assert.Equal(new[] { 5, 3, 0, 0, 0 }, named.Cards.Select(card => card.Rating));

        // Too short: filled from the default set first (back, skip, favorite, stamp, done), not from the other actions.
        Assert.Equal(new[] { "rate-1", "back", "skip", "favorite", "stamp" },
            TaggingCardSettings.From(false, new[] { "rate-1" }, 5).Cards.Select(card => card.Id));
        // Fewer cards than the list: the first ones.
        Assert.Equal(new[] { "rate-4", "stamp" }, TaggingCardSettings.From(false, new[] { "rate-4", "stamp", "done" }, 2).Cards.Select(card => card.Id));
        // Never an empty or repeated set, whatever is saved.
        foreach (string[]? damaged in new string[]?[] { null, Array.Empty<string>(), new[] { "x", "y" }, new[] { "done", "done", "done" } })
        {
            IReadOnlyList<TaggingCardAction> cards = TaggingCardSettings.From(false, damaged, 5).Cards;
            Assert.Equal(5, cards.Count);
            Assert.Equal(cards.Count, cards.Select(card => card.Id).Distinct().Count());
        }
    }

    [Theory]
    [InlineData(-3, 2)]
    [InlineData(0, 2)]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(5, 5)]
    [InlineData(6, 5)]
    [InlineData(int.MaxValue, 5)]
    public void TheCountIsClampedToItsRange(int count, int shown) =>
        Assert.Equal(shown, TaggingCardSettings.From(false, null, count).Cards.Count);

    [Fact]
    public void EveryActionHasAnIdAKindAndLabelsAndDescriptionsInTheLanguages()
    {
        Assert.Equal(10, TaggingCardActions.All.Count);
        Assert.Equal(TaggingCardActions.All.Count, TaggingCardActions.All.Select(action => action.Id).Distinct().Count());
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, TaggingCardActions.All.Where(action => action.Kind == TaggingCardKind.Rate).Select(action => action.Rating));
        Assert.Equal("Rate 4 & next", TaggingCardActions.Find("rate-4")!.Label());
        Assert.Equal("Rates the medium 4 of 5, marks it as done and goes on.", TaggingCardActions.Find("rate-4")!.Description());
        Assert.Equal("Favorite & next", TaggingCardActions.Find("favorite")!.Label());
        Assert.Null(TaggingCardActions.Find("nonsense"));
        Assert.Null(TaggingCardActions.Find(" "));
        Texts.SetLanguage("de");
        Assert.Equal("Bewertung 4 & weiter", TaggingCardActions.Find("rate-4")!.Label());
        Assert.Equal("Stempel & weiter", TaggingCardActions.Find("stamp")!.Label());
        Assert.Equal("Fertig & weiter, Taste 3", TaggingCardsText.Name(TaggingCardActions.Find("done")!, 3));
        foreach (TaggingCardAction action in TaggingCardActions.All)
            Assert.False(string.IsNullOrWhiteSpace(action.Label()) || string.IsNullOrWhiteSpace(action.Description()), action.Id);
    }

    // ---- the setting in settings.json -------------------------------------------------------------------------------

    private sealed class Profile : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("meowchan-cards-").FullName;
        public void Dispose() => Directory.Delete(Path, true);
    }

    [Fact]
    public void TheCardSettingsRoundTripThroughTheSettingsFileAndCloneAndCopyAreIndependent()
    {
        using Profile profile = new();
        Config config = Config.LoadConfig(profile.Path);
        Assert.False(config.TaggingCardsDefault); // an older profile has none of them
        Assert.Empty(config.TaggingCards);
        Assert.Equal(5, config.TaggingCardCount);
        config.TaggingCardsDefault = true;
        config.TaggingCards = new[] { "skip", "rate-5", "done" };
        config.TaggingCardCount = 3;

        Config clone = config.Clone();
        clone.TaggingCards[0] = "changed";
        Assert.Equal("skip", config.TaggingCards[0]);
        Config copy = new();
        copy.CopyFrom(config);
        Assert.True(copy.TaggingCardsDefault);
        Assert.Equal(3, copy.TaggingCardCount);
        copy.TaggingCards[1] = "changed";
        Assert.Equal("rate-5", config.TaggingCards[1]);

        config.SaveConfig(profile.Path);
        Config loaded = Config.LoadConfig(profile.Path);
        Assert.True(loaded.TaggingCardsDefault);
        Assert.Equal(new[] { "skip", "rate-5", "done" }, loaded.TaggingCards);
        Assert.Equal(3, loaded.TaggingCardCount);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(profile.Path, "settings.json")));
        Assert.Equal(JsonValueKind.Array, document.RootElement.GetProperty("TaggingCards").ValueKind);
        Assert.Equal(new[] { "skip", "rate-5", "done" }, TaggingCardSettings.FromConfig(loaded).Cards.Take(3).Select(card => card.Id).ToArray());
    }

    [Theory]
    [InlineData("0", 2)]
    [InlineData("1", 2)]
    [InlineData("2", 2)]
    [InlineData("4", 4)]
    [InlineData("9999", 5)]
    [InlineData("-7", 2)]
    [InlineData("\"many\"", 5)]
    [InlineData("null", 5)]
    [InlineData("3.5", 5)]
    public void TheSavedCountIsClampedToItsRangeAndADamagedValueNeverDiscardsTheOtherSettings(string value, int expected)
    {
        using Profile profile = new();
        File.WriteAllText(Path.Combine(profile.Path, "settings.json"), "{\"VideoVolume\":29,\"TaggingCardCount\":" + value + "}");
        Config config = Config.LoadConfig(profile.Path);
        Assert.Null(config.LoadWarning);
        Assert.Equal(29, config.VideoVolume);
        Assert.Equal(expected, config.TaggingCardCount);
        // The in-memory setter clamps the same way.
        config.TaggingCardCount = 99;
        Assert.Equal(Config.TaggingCardCountMaximum, config.TaggingCardCount);
        config.TaggingCardCount = -1;
        Assert.Equal(Config.TaggingCardCountMinimum, config.TaggingCardCount);
    }

    [Theory]
    [InlineData("\"yes\"", false)]
    [InlineData("1", false)]
    [InlineData("null", false)]
    [InlineData("true", true)]
    public void ADamagedStartChoiceReadsAsOffAndADamagedCardListAsTheDefaultSet(string start, bool expected)
    {
        using Profile profile = new();
        File.WriteAllText(Path.Combine(profile.Path, "settings.json"),
            "{\"VideoVolume\":31,\"TaggingCardsDefault\":" + start + ",\"TaggingCards\":{\"a\":1}}");
        Config config = Config.LoadConfig(profile.Path);
        Assert.Null(config.LoadWarning);
        Assert.Equal(31, config.VideoVolume);
        Assert.Equal(expected, config.TaggingCardsDefault);
        Assert.Empty(config.TaggingCards);
        Assert.Equal(TaggingCardActions.DefaultIds, TaggingCardSettings.FromConfig(config).Cards.Select(card => card.Id));
    }

    // ---- a card applies its decision through the walk's own log and undo ------------------------------------------------

    private static readonly ResourceId Thread = new("source", "board", "42");
    private static readonly DateTimeOffset T0 = new(2026, 11, 14, 8, 0, 0, TimeSpan.Zero);
    private const string Folder = "/media/42";

    private static TagQueueItem Item(int n) => new(Thread, Folder, "asset-" + n, new string((char)('a' + n % 6), 64), 100 + n, "media-" + n,
        "original", null, null, null, null, false, null, "file-" + n + ".png", SavedMediaKind.Image, TagQueueState.Open, T0.AddSeconds(n));

    private static GalleryEntry Entry(int n)
    {
        MediaAssetReference asset = new("asset-" + n, "media-" + n, "original", "file-" + n + ".png", Path.Combine(Folder, "file-" + n + ".png"), 100 + n,
            new string((char)('a' + n % 6), 64), "https://offline.invalid/" + n, "file-" + n + ".png", true);
        MediaAssetSnapshot snapshot = new(Folder, MediaManifestStatus.Available, 3, "fixture", [asset]);
        return new GalleryEntry(new GalleryAssetKey(Thread, Folder, asset.AssetId), snapshot, asset, null, null, false, false);
    }

    private static async Task<(DesktopSessionHost Host, TaggingWalk Walk)> WalkAsync(string directory, int count)
    {
        DesktopSessionHost host = new(directory, Array.Empty<IImageBoard>());
        await host.StartAsync();
        TagQueueItem[] items = Enumerable.Range(1, count).Select(Item).ToArray();
        await host.EnqueueTagQueueAsync(items);
        TagQueueSnapshot queue = await host.LoadTagQueueAsync();
        (IReadOnlyList<TaggingWalkItem> paired, int unavailable) = TaggingWalk.Pair(queue.Items, items.Select((_, i) => Entry(i + 1)).ToArray());
        return (host, new TaggingWalk(paired, unavailable, queue, new HostTaggingQueueWriter(host)));
    }

    private sealed class DecisionOwner : IGalleryDecisionOwner
    {
        private readonly Dictionary<GalleryEntryKey, GalleryDecisionState> _states = new();
        public GalleryDecisionState? Read(GalleryEntry entry) => _states.GetValueOrDefault(entry.EntryKey);
        public Task SetFavoriteAsync(GalleryEntry entry, bool selected, CancellationToken token)
        { _states[entry.EntryKey] = _states.GetValueOrDefault(entry.EntryKey) with { Favorite = selected }; return Task.CompletedTask; }
        public Task SetRatingAsync(GalleryEntry entry, int? value, CancellationToken token)
        { _states[entry.EntryKey] = _states.GetValueOrDefault(entry.EntryKey) with { Rating = value }; return Task.CompletedTask; }
    }

    [Fact]
    public async Task AFavoriteOrRatingCardIsWrittenFirstThenFinishesTheMediumAndTheOneUndoKeyTakesTheDoneBackBeforeTheFavorite()
    {
        using TempDirectory temp = new();
        (DesktopSessionHost host, TaggingWalk walk) = await WalkAsync(temp.Path, 3);
        await using (host)
        using (walk)
        using (GalleryDecisionSession decisions = new(new DecisionOwner()))
        {
            List<TaggingUndoResult> returned = new();
            using TaggingUndo undo = new(walk, decisions, () => 0, returned.Add);

            // The card, as the viewer applies it: the favorite is saved (a step of the decision session), then the medium is finished.
            GalleryDecisionStatus favorite = await decisions.SetFavoriteAsync(new[] { Entry(1) }, true);
            Assert.Equal(GalleryDecisionStatusKind.FavoriteSaved, favorite.Kind);
            TaggingDecisionResult done = walk.Decide(0, TagQueueState.Done);
            Assert.Equal(1, done.Next);
            Assert.Equal("Saved as a favorite. Done: “file-1.png”. Next: “file-2.png”.", favorite.Text() + " " + TaggingModeText.Decided(walk, done, TagQueueState.Done));
            Assert.Equal(TaggingWriteKind.Saved, (await done.Write).Kind);
            GalleryDecisionStatus rating = await decisions.SetRatingAsync(new[] { Entry(2) }, 5);
            Assert.Equal(GalleryDecisionStatusKind.RatingSaved, rating.Kind);
            Assert.Equal("Rating 5 of 5 saved.", rating.Text());
            Assert.Equal(TaggingWriteKind.Saved, (await walk.Decide(1, TagQueueState.Done).Write).Kind);
            Assert.Equal(4, undo.Steps); // two queue decisions, two decision steps: one log
            Assert.True(undo.QueueIsNewest);

            // Newest first: medium 2 done, its rating, medium 1 done, its favorite.
            Assert.Equal("Undone: “file-2.png” is open again.", await undo.UndoAsync());
            Assert.Equal(1, returned[^1].Index);
            Assert.False(undo.QueueIsNewest);
            Assert.Equal("Undone: rating change.", await undo.UndoAsync());
            Assert.Equal("Undone: “file-1.png” is open again.", await undo.UndoAsync());
            Assert.Equal(0, returned[^1].Index);
            Assert.Equal(TagQueueState.Open, walk.StateOf(0));
            Assert.Equal("Undone: favorite change.", await undo.UndoAsync());
            Assert.Equal(0, undo.Steps);
        }
    }

    [Fact]
    public async Task ASkipAndADoneCardAreTheStripsOwnDecisionsInOneLogAndTheBackCardTakesTheNewestBack()
    {
        using TempDirectory temp = new();
        (DesktopSessionHost host, TaggingWalk walk) = await WalkAsync(temp.Path, 3);
        await using (host)
        using (walk)
        {
            await walk.Decide(0, TagQueueState.Skipped).Write; // the Skip card
            await walk.Decide(1, TagQueueState.Done).Write;    // the Done card
            Assert.Equal((1, 1, 1), walk.Progress());
            Assert.Equal(2, walk.Steps);
            TaggingUndoResult? back = await walk.UndoAsync();   // the Back card
            Assert.Equal((1, TagQueueState.Open), (back!.Index, back.State));
            Assert.Equal("Undone: “file-2.png” is open again.", TaggingModeText.Undone(back));
            Assert.Equal(1, walk.Steps);
        }
    }

    // ---- texts ----------------------------------------------------------------------------------------------------------

    private static IEnumerable<string> Keys() => TaggingCardsCatalog.Entries.Select(entry => entry.Key);

    [Fact]
    public void EveryTextIsInEnglishAndGermanWithTheSamePlaceholdersAndTheCommandLabelsMatchTheCatalog()
    {
        Assert.Equal(23, TaggingCardsCatalog.Entries.Length);
        foreach (string key in Keys())
        {
            Assert.True(BuiltInCatalog.English.TryGetValue(key, out string? english), key);
            Assert.True(BuiltInCatalog.German.TryGetValue(key, out string? german), key);
            Assert.NotEqual(english, german);
            foreach (string placeholder in new[] { "{0}", "{1}", "{2}" })
                Assert.Equal(english!.Contains(placeholder), german!.Contains(placeholder));
        }
        foreach (string id in TaggingCardCommands.All)
        {
            Assert.Equal(CommandCatalog.Get(id).LabelEn, BuiltInCatalog.English["command." + id]);
            Assert.Equal(CommandCatalog.Get(id).LabelDe, BuiltInCatalog.German["command." + id]);
        }
    }

    [Fact]
    public void ThePackKeysAreInEveryBundledLanguageAsRealTranslationsWithTheirPlaceholders()
    {
        string[] codes = { "es", "fr", "ru", "zh-Hans", "ja", "ko", "pt-BR", "cs", "it", "pl" };
        string packs = Path.Combine(RepoRoot(), "Desktop", "Localization", "Packs");
        foreach (string code in codes)
        {
            using JsonDocument pack = JsonDocument.Parse(File.ReadAllText(Path.Combine(packs, code + ".json")));
            JsonElement texts = pack.RootElement.GetProperty("strings");
            foreach (string key in Keys())
            {
                Assert.True(texts.TryGetProperty(key, out JsonElement value), code + " lacks " + key);
                string text = value.GetString()!;
                Assert.False(string.IsNullOrWhiteSpace(text), code + " " + key);
                foreach (string placeholder in new[] { "{0}", "{1}", "{2}" })
                    Assert.Equal(BuiltInCatalog.English[key].Contains(placeholder), text.Contains(placeholder));
                Assert.NotEqual(BuiltInCatalog.English[key], text);
            }
        }
    }

    [Fact]
    public void TheSentencesNameTheEffectiveKeysInEnglishAndGerman()
    {
        // The toggle's key comes from the bindings (Shift+K since revision 13), in the language and on the platform of the window.
        string key = CommandCatalog.ShortcutDisplay(TaggingCardCommands.Toggle)!;
        Assert.Equal("Card mode on: the keys 1–5 choose a card, " + key + " leaves the card mode.", TaggingCardsText.On(5));
        Assert.Equal("Card mode on: the keys 1–3 choose a card, " + key + " leaves the card mode.", TaggingCardsText.On(3));
        Assert.Equal("Card mode off.", TaggingCardsText.Off());
        Assert.StartsWith(key + " turns the card mode on or off. In the card mode the keys 1 to 5 choose the cards", TaggingCardsText.HelpNote());
        Assert.Equal("Skip, key 2", TaggingCardsText.Name(TaggingCardActions.Find("skip")!, 2));
        Texts.SetLanguage("de");
        key = CommandCatalog.ShortcutDisplay(TaggingCardCommands.Toggle)!;
        Assert.Equal("Kartenmodus an: Die Tasten 1–5 wählen eine Karte, " + key + " verlässt den Kartenmodus.", TaggingCardsText.On(5));
        Assert.StartsWith(key + " schaltet den Kartenmodus ein oder aus. Im Kartenmodus wählen die Tasten 1 bis 5", TaggingCardsText.HelpNote());
    }

    // ---- the viewer's and the window's hooks ------------------------------------------------------------------------------

    private static string RepoRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory != null && !File.Exists(Path.Combine(directory, "MeowChan.csproj"))) directory = Path.GetDirectoryName(directory);
        return directory ?? throw new InvalidOperationException("The repository root was not found.");
    }

    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray()));

    [Fact]
    public void TheModeRoutesKAndTheCardKeysAfterItsOwnDecisionKeysAndNeverFromInsideTheTagField()
    {
        string mode = Source("Desktop", "GalleryViewer.TaggingMode.cs");
        int field = mode.IndexOf("if (surface?.IsKeyboardFocusWithin == true)", StringComparison.Ordinal);
        int playback = mode.IndexOf("\"tags.queue.play-pause\")) return Handled(e, ToggleTaggingPlayback);", StringComparison.Ordinal);
        int cards = mode.IndexOf("return RouteCardKey(e);", StringComparison.Ordinal);
        Assert.True(field > 0 && field < playback && playback < cards); // a key typed in the tag field returns before it ever reaches the cards
        Assert.Contains("AddCardsRow(content);", mode);
        Assert.Contains("StartCards();", mode);
        Assert.Contains("EndCards();", mode);
        Assert.Contains("RefreshCardTexts();", mode);
        Assert.Contains("TaggingCardCommands.Toggle })", mode);          // K is listed in F1
        Assert.Contains("TaggingCardsText.HelpNote()", mode);
        Assert.Contains("string? prefix = null", mode);                  // the one sentence for favorite/rating and the decision

        string cardsSource = Source("Desktop", "GalleryViewer.Cards.cs");
        Assert.Contains("CommandBindings.Matches(e, TaggingCardCommands.Toggle)", cardsSource);
        Assert.Contains("CommandBindings.Matches(e, TaggingCardCommands.Card(i + 1))", cardsSource);
        // One decision log: the card writes through the panel's own decision session and then the walk's own Decide; no second stack.
        Assert.Contains("controller.DecisionTargets()", cardsSource);
        Assert.Contains("decisions.SetFavoriteAsync(targets, true)", cardsSource);
        Assert.Contains("decisions.SetRatingAsync(targets, action.Rating)", cardsSource);
        Assert.Contains("TaggingDecide(TagQueueState.Done, status.Text());", cardsSource);
        Assert.Contains("await TaggingBackAsync();", cardsSource);
        Assert.Contains("await TaggingStampAsync();", cardsSource);
        Assert.DoesNotContain("new GalleryDecisionSession", cardsSource);
        Assert.DoesNotContain("List<GalleryDecisionStep>", cardsSource);
        // A second key while a card is applied is ignored, and a refused write leaves the medium open.
        Assert.Contains("_cardBusy", cardsSource);
        Assert.Contains("if (!saved || !ReferenceEquals(_entry, entry))", cardsSource);
    }

    [Fact]
    public void TheCardsAreButtonsWithNamesHelpTextAndTheirKeyAndTheStripsStatusLineSpeaks()
    {
        string cardsSource = Source("Desktop", "GalleryViewer.Cards.cs");
        Assert.Contains("Classes = { \"card-action\" }", cardsSource);
        Assert.Contains("AutomationProperties.SetName(button, name);", cardsSource);
        Assert.Contains("AutomationProperties.SetHelpText(button, action.Description());", cardsSource);
        Assert.Contains("AutomationProperties.SetAcceleratorKey(button,", cardsSource);
        Assert.Contains("ToolTip.SetTip(button,", cardsSource);
        Assert.Contains("TaggingCardsText.Name(action, i + 1)", cardsSource);
        Assert.Contains("SayTagging(on ? TaggingCardsText.On(_cards.Count) : TaggingCardsText.Off());", cardsSource);
        // The decision is announced by the strip's polite status line (or the panel's, while it is open): see PT3's SayTagging.
        Assert.Contains("AutomationLiveSetting.Polite", Source("Desktop", "GalleryViewer.TaggingMode.cs"));
        // The cards follow the work views like the strip does: they sit inside the controls (never hidden by auto-hide, the back control stays).
        Assert.Contains("content.Children.Add(_cardsRow);", cardsSource);
        string styles = Source("Desktop", "WorkspaceStyles.axaml");
        Assert.Contains("Selector=\"Button.card-action\"", styles);
        Assert.Contains("Workspace.Size.Card\">48<", styles);
        Assert.Contains("Workspace.Size.CardWidth\">120<", styles);
        // Colours stay the shared button tokens: nothing new for Light and Dark.
        int start = styles.IndexOf("Selector=\"Button.card-action\"", StringComparison.Ordinal);
        string style = styles.Substring(start, styles.IndexOf("</Style>", start, StringComparison.Ordinal) - start);
        Assert.DoesNotContain("#", style);
        Assert.DoesNotContain("Background", style);
    }

    [Fact]
    public void TheWindowKeepsTheCardSettingAndSavesTheChoiceAsAViewPreference()
    {
        string glue = Source("Desktop", "Shell", "MainWindow.GalleryTags.cs");
        Assert.Contains("IGalleryDecisionOwner, ITaggingCardsOwner", glue);
        Assert.Contains("TaggingCardSettings.FromConfig(window._config)", glue);
        Assert.Contains("config.TaggingCardsDefault = active", glue);
        Assert.Contains("_config.TaggingCardsDefault = active;", glue);
        // PT3's internals stay: the walk, the decision log and the owner interface are untouched by the cards.
        Assert.DoesNotContain("TaggingCard", Source("Desktop", "TaggingDecisionLog.cs"));
        Assert.DoesNotContain("TaggingCard", Source("Desktop", "GalleryTagPanelModel.cs"));
    }
}
