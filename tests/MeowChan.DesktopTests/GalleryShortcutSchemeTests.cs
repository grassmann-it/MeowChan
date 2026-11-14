using System.Text.RegularExpressions;
using System.Text.Json;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>
/// The key scheme of the gallery (defaults revision 13, <see cref="GalleryShortcutScheme"/>): Shift plus a letter shows or hides a panel or
/// block, plain letters act on the playback, Ctrl (⌘) stays for the application. The whole table, its collisions on every platform, the
/// rule "every show / hide switch is Shift plus a letter or unbound", the yielding of saved bindings and the texts that name keys.
/// The key routing itself needs a window: its proof is the gallery-keyboard / gallery-video smokes.
/// </summary>
[Collection("Localization")]
public sealed class GalleryShortcutSchemeTests : IDisposable
{
    public GalleryShortcutSchemeTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    public static IEnumerable<object[]> Platforms() => Enum.GetValues<ShortcutPlatform>().Select(platform => new object[] { platform });

    /// <summary>The scheme's defaults (identical on every platform) of the commands it touched or added; null means no default.</summary>
    private static readonly (string Id, string? Gesture)[] Table =
    {
        // Shift plus a letter: panels and blocks.
        ("gallery.filmstrip-toggle", "Shift+L"), ("gallery.context-toggle", "Shift+P"), ("gallery.mode-toggle", "Shift+T"),
        ("gallery.tools-toggle", "Shift+H"), ("gallery.overlay-toggle", "Shift+O"), ("tags.queue.cards", "Shift+K"),
        ("gallery.overlay-autohide-toggle", null), ("gallery.post-overlay-toggle", null),
        // Plain letters: playback and the older actions.
        ("video.loop", "L"), ("video.mute", "M"), ("video.snapshot", "S"), ("video.ab-loop", "A"), ("gallery.slideshow-toggle", "D"),
        ("favorites.toggle", "F"), ("favorites.add", "B"), ("favorites.remove", "U"), ("media.details", "I"), ("media.queue.add", "Q"),
        ("media.rating.edit", "R"), ("gallery.show-in-thread", "T"),
    };

    private static IEnumerable<CommandDefinition> InGallery(IEnumerable<string>? contexts = null) =>
        CommandCatalog.All.Where(command => command.ExecutionContexts.Any(context => (contexts ?? new[] { "gallery", "gallery-tagging" }).Contains(context)));

    private static string Identity(ShortcutGesture gesture) =>
        string.Join("+", gesture.Modifiers.OrderBy(modifier => modifier, StringComparer.Ordinal).Append(gesture.Key));

    // ---- the table ----

    [Theory, MemberData(nameof(Platforms))]
    public void TheTableOfTheSchemeHoldsOnEveryPlatformWithoutAnyIssue(ShortcutPlatform platform)
    {
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(null, platform);
        Assert.Empty(bindings.Issues);
        foreach ((string id, string? gesture) in Table)
        {
            if (gesture == null) Assert.Empty(bindings.For(id));
            // The details key also has its Apple extra ⌘I (older than the scheme); every other command has exactly its one key.
            else if (id == "media.details" && platform == ShortcutPlatform.MacOS) Assert.Contains(Identity(ShortcutGesture.Parse(gesture)), bindings.For(id).Select(Identity));
            else Assert.Equal(new[] { Identity(ShortcutGesture.Parse(gesture)) }, bindings.For(id).Select(Identity).ToArray());
        }
    }

    [Fact]
    public void TheKeysAreShownFromTheBindingsInTheLanguageAndPlatformOfTheWindow()
    {
        Assert.Equal("Shift+P", EffectiveShortcutBindings.Build(null, ShortcutPlatform.Windows).Display("gallery.context-toggle"));
        Assert.Equal("Shift+H", EffectiveShortcutBindings.Build(null, ShortcutPlatform.Linux).Display("gallery.tools-toggle"));
        Assert.Equal("⇧P", EffectiveShortcutBindings.Build(null, ShortcutPlatform.MacOS).Display("gallery.context-toggle"));
        Assert.Equal("⇧O", EffectiveShortcutBindings.Build(null, ShortcutPlatform.MacOS).Display("gallery.overlay-toggle"));
        Assert.Equal("M", EffectiveShortcutBindings.Build(null, ShortcutPlatform.MacOS).Display("video.mute"));
        Texts.SetLanguage("de");
        Assert.Equal("Umschalt+P", EffectiveShortcutBindings.Build(null, ShortcutPlatform.Windows).Display("gallery.context-toggle"));
        Assert.Equal("Umschalt+L", EffectiveShortcutBindings.Build(null, ShortcutPlatform.Linux).Display("gallery.filmstrip-toggle"));
        // A tooltip or a menu row asks the catalog, which asks the bindings: no key is typed into a text.
        Assert.Equal(CommandCatalog.ShortcutDisplay("video.snapshot"), EffectiveShortcutBindings.Build(null, EffectiveShortcutBindings.CurrentPlatform).Display("video.snapshot"));
        Assert.Contains(CommandCatalog.ShortcutDisplay("gallery.tools-toggle")!, CommandBindings.Tooltip("gallery.tools-toggle"));
    }

    // ---- collisions and invariants ----

    [Theory, MemberData(nameof(Platforms))]
    public void NoTwoCommandsOfTheGalleryShareAKeyAndTheTaggingModeShadowsNoLetterOrShiftedKey(ShortcutPlatform platform)
    {
        CommandDefinition[] commands = InGallery().ToArray();
        foreach (CommandDefinition first in commands)
            foreach (CommandDefinition second in commands.Where(other => string.CompareOrdinal(other.Id, first.Id) > 0))
                foreach (ShortcutGesture a in EffectiveShortcutBindings.Defaults(first, platform))
                    foreach (ShortcutGesture b in EffectiveShortcutBindings.Defaults(second, platform))
                    {
                        if (!EffectiveShortcutBindings.Equivalent(a, b, platform)) continue;
                        // The only shared gestures are the tagging mode's own keys (Space, Backspace, 1 to 5), which the mode routes first and
                        // which live in their own context. Never a letter and never Shift plus a letter.
                        bool shadow = first.ExecutionContexts.Contains("gallery-tagging") != second.ExecutionContexts.Contains("gallery-tagging");
                        Assert.True(shadow, first.Id + " and " + second.Id + " share " + a.Display(platform));
                        Assert.False(GalleryShortcutScheme.IsPlainLetter(a) || GalleryShortcutScheme.IsShiftLetter(a), first.Id + " / " + second.Id + " " + a.Display(platform));
                    }
    }

    [Theory, MemberData(nameof(Platforms))]
    public void EveryPlainLetterOfTheGalleryBelongsToExactlyOneCommandAndTheLettersAreThoseOfTheScheme(ShortcutPlatform platform)
    {
        Dictionary<string, List<string>> letters = new(StringComparer.Ordinal);
        foreach (CommandDefinition command in InGallery())
            foreach (ShortcutGesture gesture in EffectiveShortcutBindings.Defaults(command, platform).Where(GalleryShortcutScheme.IsPlainLetter))
                (letters.TryGetValue(gesture.Key, out var owners) ? owners : letters[gesture.Key] = new()).Add(command.Id);
        Assert.All(letters, pair => Assert.Single(pair.Value));
        Assert.Equal(new Dictionary<string, string>
        {
            // Revision 14: X slower and C faster (next to [ and ], which need AltGr on DE/FR), plain letters like the other playback keys.
            ["A"] = "video.ab-loop", ["B"] = "favorites.add", ["C"] = "video.speed-up", ["D"] = "gallery.slideshow-toggle", ["F"] = "favorites.toggle", ["I"] = "media.details",
            ["L"] = "video.loop", ["M"] = "video.mute", ["Q"] = "media.queue.add", ["R"] = "media.rating.edit", ["S"] = "video.snapshot",
            ["T"] = "gallery.show-in-thread", ["U"] = "favorites.remove", ["X"] = "video.speed-down"
        }.OrderBy(pair => pair.Key), letters.ToDictionary(pair => pair.Key, pair => pair.Value[0]).OrderBy(pair => pair.Key));
        // The playback letters of the scheme are among them.
        foreach (string id in GalleryShortcutScheme.PlaybackLetterIds)
            Assert.Contains(id, letters.Values.Select(owners => owners[0]));
    }

    [Theory, MemberData(nameof(Platforms))]
    public void TheShiftLettersOfTheGalleryAreTheSwitchesAndRotate(ShortcutPlatform platform)
    {
        Dictionary<string, string> shifted = new(StringComparer.Ordinal);
        foreach (CommandDefinition command in InGallery())
            foreach (ShortcutGesture gesture in EffectiveShortcutBindings.Defaults(command, platform).Where(GalleryShortcutScheme.IsShiftLetter))
                shifted.Add(gesture.Key, command.Id);
        Assert.Equal(new Dictionary<string, string>
        {
            // GL3's layout rows are registered in wave 6, so Shift+I (info line) and Shift+N (hints) are live defaults.
            ["H"] = "gallery.tools-toggle", ["I"] = "gallery.layout.info", ["K"] = "tags.queue.cards", ["L"] = "gallery.filmstrip-toggle",
            ["N"] = "gallery.layout.hints", ["O"] = "gallery.overlay-toggle", ["P"] = "gallery.context-toggle", ["R"] = "gallery.rotate", ["T"] = "gallery.mode-toggle"
        }.OrderBy(pair => pair.Key), shifted.OrderBy(pair => pair.Key));
    }

    [Theory, MemberData(nameof(Platforms))]
    public void TheSchemeKeepsOutOfTheRatingNumberAndCtrlKeys(ShortcutPlatform platform)
    {
        string[] touched = Table.Select(entry => entry.Id).ToArray();
        foreach (string id in touched)
            foreach (ShortcutGesture gesture in EffectiveShortcutBindings.Defaults(CommandCatalog.Get(id), platform))
            {
                // No digit (the fit / size keys, the cards 1 to 5 and the Shift+digit ratings stay as they are) and no Ctrl, ⌘ or Alt
                // (⌘ is reserved for the application, macOS owns ⌘H, ⌘M, ⌘Q and ⌘W).
                Assert.False(gesture.Key.Length == 2 && gesture.Key[0] == 'D' && char.IsAsciiDigit(gesture.Key[1]), id);
                if (id != "media.details") // its Apple extra ⌘I is older than the scheme
                    Assert.DoesNotContain(gesture.Modifiers, modifier => modifier is "Primary" or "Control" or "Ctrl" or "Meta" or "Alt");
            }
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(null, platform);
        foreach (string digit in new[] { "D0", "D1", "D2", "D3", "D4", "D5" })
            Assert.DoesNotContain(CommandCatalog.All.Where(command => bindings.For(command.Id).Any(g => g.Key == digit && g.Modifiers.SequenceEqual(new[] { "Shift" }))),
                command => !command.Id.StartsWith("media.rating.", StringComparison.Ordinal));
    }

    [Theory, MemberData(nameof(Platforms))]
    public void NoGalleryLetterDefaultIsKeptWhenSingleKeysAreOffAndEveryOtherKeyOfTheSchemeStays(ShortcutPlatform platform)
    {
        EffectiveShortcutBindings off = EffectiveShortcutBindings.Build(new ShortcutBindingsSection().WithSingleKeyShortcuts(false), platform);
        Assert.Empty(off.Issues);
        foreach ((string id, string? gesture) in Table.Where(entry => entry.Gesture != null))
            Assert.DoesNotContain(off.For(id), EffectiveShortcutBindings.IsSingleCharacterKey); // ⌘I of the details stays on a Mac
    }

    // ---- the rule ----

    /// <summary>The switches of the gallery by their names: "show / hide", "toggle", "on / off", the view / tag switch.</summary>
    private static readonly Regex SwitchLabel = new(@"(?i)show / hide|toggle|on / off|view / tag", RegexOptions.CultureInvariant);

    [Theory, MemberData(nameof(Platforms))]
    public void EveryShowHideSwitchOfTheGalleryUsesShiftPlusALetterOrHasNoDefault(ShortcutPlatform platform)
    {
        // The nine tag suggestions "toggle" a tag on the medium (Alt+1 to Alt+9): they show and hide no block.
        CommandDefinition[] switches = InGallery().Where(command => !command.Id.StartsWith("tags.quick.suggestion-", StringComparison.Ordinal)
            && (SwitchLabel.IsMatch(command.LabelEn) || command.Id.StartsWith("gallery.layout.", StringComparison.Ordinal)
                && !command.Id.EndsWith(".reset", StringComparison.Ordinal) && !command.Id.Contains("filmstrip-", StringComparison.Ordinal))).ToArray();
        Assert.True(switches.Length >= 7, "The rule found only " + switches.Length + " switches.");
        foreach (CommandDefinition command in switches)
            foreach (ShortcutGesture gesture in EffectiveShortcutBindings.Defaults(command, platform))
                Assert.True(GalleryShortcutScheme.IsShiftLetter(gesture), command.Id + " has " + gesture.Display(platform) + ": a switch takes Shift plus a letter or no default");
        // The data list of the scheme names every switch the labels find, so a new switch cannot slip past the rule unnoticed.
        string[] unlisted = switches.Select(command => command.Id).Except(GalleryShortcutScheme.PanelToggleIds)
            .Except(new[] { "viewer.shortcut-hints" }).ToArray(); // the hint display switch has no key and lives in the settings too
        Assert.True(unlisted.Length == 0, "Switches missing from GalleryShortcutScheme.PanelToggleIds: " + string.Join(", ", unlisted));
        foreach (string id in GalleryShortcutScheme.PanelToggleIds.Where(id => CommandCatalog.TryGet(id, out _)))
            foreach (ShortcutGesture gesture in EffectiveShortcutBindings.Defaults(CommandCatalog.Get(id), platform))
                Assert.True(GalleryShortcutScheme.IsShiftLetter(gesture), id);
    }

    [Theory, MemberData(nameof(Platforms))]
    public void TheSpeedKeysXAndCArePlainLettersBesideTheBracketsAndNothingElseUsesThem(ShortcutPlatform platform)
    {
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(null, platform);
        Assert.Empty(bindings.Issues);
        Assert.Equal(new[] { "OemOpenBrackets", "X" }, bindings.For("video.speed-down").Select(gesture => gesture.Key).ToArray());
        Assert.Equal(new[] { "OemCloseBrackets", "C" }, bindings.For("video.speed-up").Select(gesture => gesture.Key).ToArray());
        Assert.All(new[] { "video.speed-down", "video.speed-up" }, id =>
            Assert.Contains(bindings.For(id), gesture => GalleryShortcutScheme.IsPlainLetter(gesture)));
        // Neither letter is taken in any context of the application, with or without Shift (Ctrl+C copies, which is another gesture).
        foreach (string letter in new[] { "X", "C" })
            foreach (string[] modifiers in new[] { Array.Empty<string>(), new[] { "Shift" } })
            {
                string[] owners = CommandCatalog.All.Where(command => bindings.For(command.Id)
                    .Any(g => g.Key == letter && g.Modifiers.OrderBy(m => m).SequenceEqual(modifiers))).Select(command => command.Id).ToArray();
                Assert.Equal(modifiers.Length == 0 ? new[] { letter == "X" ? "video.speed-down" : "video.speed-up" } : Array.Empty<string>(), owners);
            }
        // The tagging mode (own context "gallery-tagging") routes Enter, Space, Backspace, Shift+Space, Shift+K and 1 to 5 first: none of its keys is X or C.
        Assert.DoesNotContain(CommandCatalog.All.Where(command => command.ExecutionContexts.Contains("gallery-tagging")),
            command => bindings.For(command.Id).Any(g => g.Key is "X" or "C"));
    }

    [Fact]
    public void ThePlaybackLettersAreNeverShifted()
    {
        foreach (string id in GalleryShortcutScheme.PlaybackLetterIds)
            Assert.All(CommandCatalog.Get(id).DefaultGestures, gesture => Assert.True(GalleryShortcutScheme.IsPlainLetter(gesture), id));
    }

    // ---- the layout commands of the Layout menu branch (GL3) ----

    [Theory, MemberData(nameof(Platforms))]
    public void TheLayoutSwitchesTakeShiftLettersWhenTheirCommandsAreRegisteredAndNothingOtherwise(ShortcutPlatform platform)
    {
        Assert.Equal(new[] { "gallery.layout.hints", "gallery.layout.info" }, GalleryShortcutScheme.LayoutDefaults.Keys.OrderBy(id => id).ToArray());
        foreach ((string id, string gesture) in GalleryShortcutScheme.LayoutDefaults)
        {
            CommandDefinition layout = new(id, "command." + id, "Layout row", "Layout-Zeile", "info", null, CommandScope.Gallery, new[] { "gallery-toolbar" });
            Assert.Equal(new[] { Identity(ShortcutGesture.Parse(gesture)) }, EffectiveShortcutBindings.Defaults(layout, platform).Select(Identity).ToArray());
            Assert.True(GalleryShortcutScheme.IsShiftLetter(ShortcutGesture.Parse(gesture)));
            Assert.Contains(id, EffectiveShortcutBindings.YieldingDefaultCommandIds);
            // The letters the rows take are free in the gallery for every other command (GL3 registers the row commands themselves).
            Assert.True(CommandCatalog.All.Any(command => command.Id == id), id + " is registered (GL3)");
            Assert.DoesNotContain(InGallery(), command => command.Id != id && EffectiveShortcutBindings.Defaults(command, platform)
                .Any(other => EffectiveShortcutBindings.Equivalent(other, ShortcutGesture.Parse(gesture), platform)));
        }
        // The tools row, the filmstrip positions, the reset and the menu itself stay without a key of their own.
        foreach (string id in new[] { "gallery.layout.tools", "gallery.layout.filmstrip-top", "gallery.layout.filmstrip-bottom", "gallery.layout.reset", "gallery.layout-menu" })
        {
            CommandDefinition layout = new(id, "command." + id, "Layout row", "Layout-Zeile", "info", null, CommandScope.Gallery, new[] { "gallery-toolbar" });
            Assert.Empty(EffectiveShortcutBindings.Defaults(layout, platform));
        }
        // An id that is no layout command never receives such a default.
        Assert.Empty(EffectiveShortcutBindings.Defaults(CommandCatalog.Get("gallery.context-close"), platform));
    }

    // ---- yielding ----

    [Theory, MemberData(nameof(Platforms))]
    public void EveryChangedOrNewDefaultOfRevision13YieldsToASavedBindingOfAnotherCommandOnItsKey(ShortcutPlatform platform)
    {
        Assert.True(EffectiveShortcutBindings.DefaultsRevision >= 13);
        foreach (string id in new[] { "gallery.context-toggle", "gallery.slideshow-toggle", "video.mute", "video.snapshot", "favorites.add",
            "gallery.tools-toggle", "gallery.overlay-toggle", "tags.queue.cards", "gallery.filmstrip-toggle", "gallery.layout.info", "gallery.layout.hints" })
            Assert.Contains(id, EffectiveShortcutBindings.YieldingDefaultCommandIds);
        foreach ((string id, string? text) in Table.Where(entry => entry.Gesture != null && entry.Id is "gallery.context-toggle" or "gallery.slideshow-toggle"
            or "video.mute" or "video.snapshot" or "favorites.add" or "gallery.tools-toggle" or "gallery.overlay-toggle" or "tags.queue.cards"))
        {
            // Another command of the same context was saved on the key the new default wants: it keeps it, the new default stays away,
            // nothing blocks the save and the saved list is not rewritten.
            string other = id == "tags.queue.cards" ? "tags.queue.skip" : "gallery.close";
            ShortcutBindingsSection section = new ShortcutBindingsSection().Set(other, [ShortcutGesture.Parse(text!)]);
            string before = section.Raw.GetRawText();
            EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(section, platform);
            Assert.Single(bindings.For(other));
            Assert.Empty(bindings.For(id));
            Assert.DoesNotContain(bindings.Issues, issue => issue.BlocksSave);
            Assert.Contains(bindings.Issues, issue => issue.CommandId == id && issue.OtherCommandId == other && !issue.BlocksSave);
            Assert.Equal(before, section.Raw.GetRawText());
        }
    }

    [Theory, MemberData(nameof(Platforms))]
    public void AProfileThatSavedTheOldKeysKeepsThemAndTheNewDefaultsStayOutOfTheWay(ShortcutPlatform platform)
    {
        // The old defaults, stored as explicit lists (an "add another shortcut" freezes the whole list): the person's keys win everywhere.
        ShortcutBindingsSection section = new ShortcutBindingsSection()
            .Set("gallery.context-toggle", [ShortcutGesture.Parse("P")])
            .Set("gallery.slideshow-toggle", [ShortcutGesture.Parse("S")])
            .Set("video.mute", [ShortcutGesture.Parse("Shift+M")])
            .Set("video.snapshot", [ShortcutGesture.Parse("Shift+S")])
            .Set("favorites.add", [ShortcutGesture.Parse("M")]);
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(section, platform);
        Assert.Equal("P", bindings.For("gallery.context-toggle").Single().Key);
        Assert.Equal("S", bindings.For("gallery.slideshow-toggle").Single().Key);
        Assert.Equal(new[] { "Shift" }, bindings.For("video.mute").Single().Modifiers);
        Assert.Equal("S", bindings.For("video.snapshot").Single().Key);
        Assert.Equal("M", bindings.For("favorites.add").Single().Key);
        Assert.DoesNotContain(bindings.Issues, issue => issue.BlocksSave);
        // The new defaults that found no saved key are all there.
        Assert.Equal("H", bindings.For("gallery.tools-toggle").Single().Key);
        Assert.Equal("O", bindings.For("gallery.overlay-toggle").Single().Key);
        Assert.Equal("L", bindings.For("video.loop").Single().Key);
    }

    [Fact]
    public void AnExplicitConflictBetweenTwoSavedBindingsStillRejectsBothAndNothingYieldsBetweenThem()
    {
        ShortcutBindingsSection section = new ShortcutBindingsSection()
            .Set("video.mute", [ShortcutGesture.Parse("Shift+Q")]).Set("gallery.tools-toggle", [ShortcutGesture.Parse("Shift+Q")]);
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(section, ShortcutPlatform.Linux);
        Assert.Empty(bindings.For("video.mute"));
        Assert.Empty(bindings.For("gallery.tools-toggle"));
        Assert.Contains(bindings.Issues, issue => issue.BlocksSave);
    }

    // ---- texts ----

    // A chord with Ctrl before the Shift (the tag editor's Ctrl+Shift+T) belongs to the application and is not what the scheme is about.
    private static readonly Regex GestureInText = new(@"(?<!\+)(?:Shift|Umschalt|Mayús|Maj|Maiusc)\+[A-Z]\b", RegexOptions.CultureInvariant);

    private static IEnumerable<(string Language, string Key, string Text)> AllTexts()
    {
        foreach (var pair in BuiltInCatalog.English) yield return ("en", pair.Key, pair.Value);
        foreach (var pair in BuiltInCatalog.German) yield return ("de", pair.Key, pair.Value);
        string packs = Path.Combine(Path.GetDirectoryName(Source())!, "../../Desktop/Localization/Packs");
        foreach (string file in Directory.GetFiles(packs, "*.json"))
            foreach (JsonProperty property in JsonDocument.Parse(File.ReadAllText(file)).RootElement.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String))
                yield return (Path.GetFileNameWithoutExtension(file), property.Name, property.Value.GetString() ?? string.Empty);
    }

    private static string Source([System.Runtime.CompilerServices.CallerFilePath] string source = "") => source;

    [Fact]
    public void NoTextOfTheGalleryNamesAFixedLetterKey()
    {
        // Tooltips, menu rows, settings hints and the shortcuts window take their keys from the bindings (Texts.T with the display of the
        // command), so the gallery's own texts never spell out "Shift+L" and the like: a rebinding would make them lie.
        string[] prefixes = { "gallery.", "viewer.", "settings.gallery", "settings.tagging", "cards.", "command.gallery.", "command.video.", "command.favorites." };
        var offenders = AllTexts().Where(text => prefixes.Any(prefix => text.Key.StartsWith(prefix, StringComparison.Ordinal)) && GestureInText.IsMatch(text.Text))
            .Select(text => text.Language + ":" + text.Key).ToArray();
        Assert.True(offenders.Length == 0, string.Join(", ", offenders));
    }

    [Fact]
    public void TheTextsOfTheSchemeExistInAllTwelveLanguagesWithTheirPlaceholdersAndRealTranslations()
    {
        string[] languages = Texts.BundledPackCodes().Prepend("de").Prepend("en").Distinct().ToArray();
        Assert.Equal(12, languages.Length);
        string english(string key) => Translate("en", key);
        foreach (string language in languages)
        {
            string scheme = Translate(language, "viewer.help.scheme");
            Assert.NotEqual("viewer.help.scheme", scheme);
            foreach (string key in new[] { "settings.galleryFilmstripKeyHint", "settings.tagging.cardsStart.keyHint" })
            {
                string text = Translate(language, key, "KEYX");
                Assert.Contains("KEYX", text);
                Assert.NotEqual(key, text);
            }
            if (language == "en") continue;
            Assert.NotEqual(english("viewer.help.scheme"), scheme);
            Assert.NotEqual(english("settings.galleryFilmstripKeyHint"), Translate(language, "settings.galleryFilmstripKeyHint"));
            Assert.NotEqual(english("settings.tagging.cardsStart.keyHint"), Translate(language, "settings.tagging.cardsStart.keyHint"));
            // The long hints lost their key sentence and keep their other content.
            Assert.NotEqual(english("settings.galleryFilmstripHint"), Translate(language, "settings.galleryFilmstripHint"));
            Assert.NotEqual(english("settings.tagging.cardsStart.hint"), Translate(language, "settings.tagging.cardsStart.hint"));
        }
        Assert.Equal("Shortcuts in the gallery: Shift shows or hides panels, letters control playback.", Translate("en", "viewer.help.scheme"));
        Assert.Equal("Tastenkürzel in der Galerie: Umschalt blendet Panels ein oder aus, Buchstaben steuern die Wiedergabe.", Translate("de", "viewer.help.scheme"));
        Assert.Equal("viewer.help.favorite", Translate("en", "viewer.help.favorite")); // the static line with fixed keys is gone
    }

    private static string Translate(string language, string key, params object[] arguments)
    {
        Texts.SetLanguage(language);
        try { return Texts.T(key, arguments); } finally { Texts.SetLanguage("en"); }
    }
}
