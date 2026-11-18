using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;

namespace MeowChan.DesktopTests;

// "Expand replies automatically" (Config.ReaderExpandReplies, default off). The rule works on the post rows, so it is exercised without a
// window (ReplyAutoExpandPolicy); the page only calls it from ContainerPrepared, from the reply-count publication and from the shell's
// preference hand-over, which the source contracts below pin. Layout under a real list stays with the reader smoke.
[Collection("Localization")]
public sealed class ReaderExpandRepliesTests : IDisposable
{
    public ReaderExpandRepliesTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static string Source(string relative, [System.Runtime.CompilerServices.CallerFilePath] string sourcePath = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative)));

    private static readonly ResourceId ThreadA = new("4chan", "g", "100");
    private static readonly ResourceId ThreadB = new("4chan", "g", "200");

    private static ArchivePostRow Row(string id, int? replies = 3, bool stub = false, bool suppress = false)
    {
        ArchivePostRow row = new(id, string.Empty, "text", Array.Empty<ArchiveAttachmentRow>()) { IsStub = stub, SuppressReplyToggle = suppress };
        if (replies.HasValue) row.SetBacklinkCount(replies.Value);
        return row;
    }

    private static ReplyAutoExpandPolicy On(ResourceId? thread = null) => new ReplyAutoExpandPolicy { Enabled = true }.Opened(thread ?? ThreadA);

    // ---- the setting ----

    [Fact]
    public void TheSettingIsOffByDefaultAndRoundTripsThroughTheProfileAndEveryCopy()
    {
        Assert.False(new Config().ReaderExpandReplies);
        Assert.False(JsonSerializer.Deserialize<Config>("{}")!.ReaderExpandReplies);
        using TempDirectory directory = new();
        Config original = new() { ReaderExpandReplies = true, DownloadPath = Path.Combine(directory.Path, "media") };
        Config clone = original.Clone();
        Config settingsCopy = new();
        settingsCopy.CopySettingsFieldsFrom(clone);
        Config fullCopy = new();
        fullCopy.CopyFrom(settingsCopy);
        fullCopy.SaveConfig(directory.Path);
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Null(loaded.LoadWarning);
        foreach (Config candidate in new[] { original, clone, settingsCopy, fullCopy, loaded })
        {
            Assert.True(candidate.ReaderExpandReplies);
            candidate.Validate();
        }
        using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.Path, "settings.json")));
        Assert.True(saved.RootElement.GetProperty(nameof(Config.ReaderExpandReplies)).GetBoolean());
    }

    [Fact]
    public void TheSettingsTransferExportsAndImportsIt()
    {
        Config source = new() { ReaderExpandReplies = true };
        Assert.True(SettingsTransfer.CreatePortableSnapshot(source).ReaderExpandReplies);
        Assert.False(SettingsTransfer.CreatePortableSnapshot(new Config()).ReaderExpandReplies);
        JsonObject exported = SettingsTransfer.Export(source);
        Assert.True((bool?)exported[nameof(Config.ReaderExpandReplies)]);
        Config imported = JsonSerializer.Deserialize<Config>(exported.ToJsonString())!;
        Assert.True(imported.ReaderExpandReplies);
        Config target = new();
        target.CopySettingsFieldsFrom(imported);
        Assert.True(target.ReaderExpandReplies);
    }

    [Fact]
    public void ThePopoverRowTheSettingsRowTheResetTheSearchAndTheShellHandOverAreWired()
    {
        // The popover offers it as its own entry in the text and layout section, after the page size, as the same setting under the same id.
        Assert.Contains(SettingsFieldId.ReaderExpandReplies, ReaderQuickSettings.Fields.Select(field => field.Id));
        Config config = new();
        QuickSettingField field = ReaderQuickSettings.Fields.Single(f => f.Id == SettingsFieldId.ReaderExpandReplies);
        Assert.Equal(QuickSettingKind.Toggle, field.Kind);
        Assert.Equal(bool.FalseString, field.Read(config));
        Assert.True(field.TryCreateChange(bool.TrueString, out Action<Config> change));
        change(config);
        Assert.True(config.ReaderExpandReplies);
        Assert.Equal(bool.TrueString, field.Read(config));
        string settings = Source("Desktop/Views/Settings/SettingsEditor.axaml");
        Assert.Contains("x:Name=\"ReaderExpandRepliesInput\" IsChecked=\"{Binding ReaderExpandReplies, Mode=TwoWay}\"", settings);
        XDocument markup = XDocument.Parse(settings);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement input = Assert.Single(markup.Descendants(), element => (string?)element.Attribute(x + "Name") == "ReaderExpandRepliesInput");
        Assert.Equal("ToggleSwitch", input.Name.LocalName);
        Assert.Contains("Flag(ReaderExpandRepliesInput, () => ReaderExpandReplies, value => ReaderExpandReplies = value, config => config.ReaderExpandReplies),",
            Source("Desktop/Views/Settings/SettingsEditor.RowMenu.cs"));
        Assert.Contains("[\"ReaderExpandReplies\"] =", Source("Desktop/Views/Settings/SettingsSearchAliases.cs"));
        string display = Source("Desktop/Views/Settings/SettingsEditor.ReaderDisplay.cs");
        Assert.Contains("ReaderExpandReplies = config.ReaderExpandReplies;", display);         // start, discard, reset
        Assert.Contains("candidate.ReaderExpandReplies = ReaderExpandReplies;", display);       // save
        Assert.Contains("case SettingsFieldId.ReaderExpandReplies:", display);                  // the popover confirmed it
        // The shell hands the saved value to the page with the other reader display preferences (also after a popover change).
        Assert.Contains("reader.ReaderExpandRepliesPreference = config.ReaderExpandReplies;", Source("Desktop/ReaderSettingsPreferences.cs"));
    }

    [Fact]
    public void TheLabelAndHintExistInEveryLanguage()
    {
        string[] keys = { "settings.reader.expandReplies", "settings.reader.expandReplies.hint" };
        foreach (string code in new[] { "en", "de" }.Concat(Texts.BundledPackCodes()))
        {
            Texts.SetLanguage(code);
            foreach (string key in keys) Assert.NotEqual(key, Texts.T(key));
        }
    }

    // ---- the rule: realise ----

    [Fact]
    public void OffNothingOpensAndNothingIsMarkedAutomatic()
    {
        ReplyAutoExpandPolicy policy = new ReplyAutoExpandPolicy().Opened(ThreadA);
        ArchivePostRow row = Row("1");
        Assert.False(policy.Realise(row));
        Assert.True(row.RepliesRealised);
        Assert.False(row.RepliesExpanded);
        Assert.False(row.RepliesAutoExpanded);
    }

    [Fact]
    public void OnARealisedCardOpensItsRepliesOnTheRowAndAsksForTheFirstPageOnly()
    {
        ReplyAutoExpandPolicy policy = On();
        ArchivePostRow row = Row("1");
        Assert.True(policy.Realise(row));                  // opened before the container's first measure; the first page still loads
        Assert.True(row.RepliesExpanded);
        Assert.True(row.RepliesAutoExpanded);
        Assert.False(row.RepliesLoaded);
    }

    [Fact]
    public void ARecycledContainerThatIsPreparedAgainChangesNothingAndLoadsNothingTwice()
    {
        ReplyAutoExpandPolicy policy = On();
        ArchivePostRow row = Row("1");
        Assert.True(policy.Realise(row));
        row.SetRepliesLoading(true);
        Assert.False(policy.Realise(row));                 // the load is under way
        row.SetReplies(Array.Empty<ArchiveQuoteRow>(), 3, 0);
        row.SetRepliesLoading(false);
        Assert.False(policy.Realise(row));                 // loaded: the replies stay on the row, nothing is requested again
        Assert.True(row.RepliesExpanded);
        List<string> raised = new();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);
        policy.Realise(row);
        Assert.Empty(raised);                              // no change notification, so no late height change
    }

    [Fact]
    public void AFailedLoadIsNotRetriedByEveryRecycle()
    {
        ReplyAutoExpandPolicy policy = On();
        ArchivePostRow row = Row("1");
        Assert.True(policy.Realise(row));
        row.SetRepliesError("offline");
        Assert.False(policy.Realise(row));
        Assert.True(row.RepliesExpanded);
    }

    [Fact]
    public void OnlyPostsWithKnownRepliesOpenAndNeverStubsOrTheConversationViewsPosts()
    {
        ReplyAutoExpandPolicy policy = On();
        foreach (ArchivePostRow row in new[] { Row("1", replies: 0), Row("2", replies: null), Row("3", stub: true), Row("4", suppress: true) })
        {
            Assert.False(policy.Realise(row));
            Assert.False(row.RepliesExpanded);
        }
        // The count arrives after the card was realised: the page runs the rule again for the realised cards.
        ArchivePostRow late = Row("5", replies: null);
        Assert.False(policy.Realise(late));
        late.SetBacklinkCount(2);
        Assert.True(policy.Realise(late));
        Assert.True(late.RepliesExpanded);
    }

    [Fact]
    public void ARowTheReaderAlreadyOpenedIsNotMarkedAutomaticAndStaysOpenWhenTheSettingIsSwitchedOff()
    {
        ReplyAutoExpandPolicy policy = On();
        ArchivePostRow manual = Row("1"), automatic = Row("2");
        manual.RepliesExpanded = true;
        policy.RememberChoice(manual, true);
        policy.Realise(manual);
        policy.Realise(automatic);
        Assert.False(manual.RepliesAutoExpanded);
        Assert.True(automatic.RepliesAutoExpanded);
        policy.Disable(new[] { manual, automatic });
        Assert.True(manual.RepliesExpanded);
        Assert.False(automatic.RepliesExpanded);
        Assert.False(automatic.RepliesAutoExpanded);
    }

    // ---- the rule: the reader's own choice ----

    [Fact]
    public void ACollapsedPostStaysCollapsedWhileTheThreadIsOpenEvenWhenItsCardIsRealisedAgain()
    {
        ReplyAutoExpandPolicy policy = On();
        ArchivePostRow row = Row("1"), other = Row("2");
        Assert.True(policy.Realise(row));
        row.RepliesExpanded = false;                       // the click on the header counter
        policy.RememberChoice(row, false);
        Assert.False(row.RepliesAutoExpanded);
        for (int recycle = 0; recycle < 3; recycle++) Assert.False(policy.Realise(row));
        Assert.False(row.RepliesExpanded);
        Assert.True(policy.Realise(other));                // only this post is remembered
        // Another page of the same thread (new rows for the same post ids) keeps the memory.
        ArchivePostRow rebuilt = Row("1");
        Assert.False(policy.Realise(rebuilt));
        Assert.False(rebuilt.RepliesExpanded);
    }

    [Fact]
    public void OpeningItAgainForgetsTheCollapseAndAnotherThreadStartsWithoutTheMemory()
    {
        ReplyAutoExpandPolicy policy = On();
        ArchivePostRow row = Row("1");
        policy.RememberChoice(row, false);
        Assert.Equal(1, policy.CollapsedCount);
        policy.RememberChoice(row, true);
        Assert.Equal(0, policy.CollapsedCount);
        policy.RememberChoice(row, false);
        policy.SwitchThread(ThreadA);                      // same thread: kept
        Assert.Equal(1, policy.CollapsedCount);
        policy.SwitchThread(ThreadB);
        Assert.Equal(0, policy.CollapsedCount);
        Assert.True(policy.Realise(Row("1")));
        policy.SwitchThread(null);
        Assert.False(policy.Realise(Row("2")));            // no thread open: nothing to expand
    }

    // ---- live toggle ----

    [Fact]
    public void SwitchingItOnOpensOnlyTheCardsRealisedSoFarAndTheOthersWhenTheyAppear()
    {
        ReplyAutoExpandPolicy policy = new ReplyAutoExpandPolicy().Opened(ThreadA);
        ArchivePostRow shown = Row("1"), collapsed = Row("2"), unseen = Row("3");
        policy.Realise(shown); policy.Realise(collapsed);                       // realised while the setting was off
        policy.RememberChoice(collapsed, false);
        List<ArchivePostRow> toLoad = policy.Enable(new[] { shown, collapsed, unseen }, _ => true);
        Assert.Equal(new[] { shown }, toLoad);                                  // lazy: the unseen card loads nothing
        Assert.True(shown.RepliesExpanded);
        Assert.False(collapsed.RepliesExpanded);                                // the reader's collapse wins
        Assert.False(unseen.RepliesExpanded);
        Assert.False(unseen.RepliesRealised);
        Assert.True(policy.Realise(unseen));                                    // opens when its card is realised
        Assert.True(unseen.RepliesExpanded);
    }

    [Fact]
    public void SwitchingItOffClosesWhatItOpenedKeepsTheLoadedRepliesAndASecondSwitchOnReopensWithoutLoading()
    {
        ReplyAutoExpandPolicy policy = On();
        ArchivePostRow row = Row("1");
        Assert.True(policy.Realise(row));
        row.SetReplies(Array.Empty<ArchiveQuoteRow>(), 3, 0);
        policy.Disable(new[] { row });
        Assert.False(policy.Enabled);
        Assert.False(row.RepliesExpanded);
        Assert.True(row.RepliesLoaded);
        Assert.False(policy.Realise(row));                                      // off: stays closed
        Assert.Empty(policy.Enable(new[] { row }, _ => true));                             // on again: opens at once, nothing to load
        Assert.True(row.RepliesExpanded);
    }

    [Fact]
    public void SwitchingItOnDeepInABigThreadLoadsOnlyTheCardsThatExistNowNotEveryCardEverRealised()
    {
        ReplyAutoExpandPolicy policy = new ReplyAutoExpandPolicy().Opened(ThreadA);
        ArchivePostRow[] rows = Enumerable.Range(0, 500).Select(i => Row(i.ToString())).ToArray();
        foreach (ArchivePostRow row in rows) policy.Realise(row);               // scrolled through all of them while the setting was off (the flag is never reset)
        HashSet<object> live = rows.Skip(240).Take(12).ToHashSet(ReferenceEqualityComparer.Instance);     // only these cards exist now
        List<ArchivePostRow> toLoad = policy.Enable(rows, row => live.Contains(row));
        Assert.Equal(live.Count, toLoad.Count);
        Assert.All(toLoad, row => Assert.Contains(row, live));
        Assert.All(rows.Where(row => !live.Contains(row)), row => Assert.False(row.RepliesExpanded));
        Assert.True(policy.Realise(rows[0]));                                   // a card that appears later still opens when it is realised
    }

    // ---- the page ----

    [Fact]
    public void ThePageAppliesTheRuleWhenACardIsPreparedBeforeItsFirstMeasureAndWhenCountsArriveAndKeepsTheCollapseMemory()
    {
        string cards = Source("Desktop/Views/Archive/ArchivePage.PostCards.cs");
        int decorate = cards.IndexOf("DecorateReaderPost(e.Container", StringComparison.Ordinal);
        int apply = cards.IndexOf("ApplyAutomaticReplies(post);", StringComparison.Ordinal);
        Assert.True(decorate >= 0 && apply > decorate, "ContainerPrepared runs the rule");
        string replies = Source("Desktop/Views/Archive/ArchivePage.Replies.cs");
        int counts = replies.IndexOf("row.SetBacklinkCount(counts.GetValueOrDefault(row.Id));", StringComparison.Ordinal);
        int publish = replies.IndexOf("ApplyAutomaticRepliesToRealised(rows);", StringComparison.Ordinal);
        int restore = replies.IndexOf("if (restoreLayout != null) await restoreLayout();", StringComparison.Ordinal);
        Assert.True(counts >= 0 && publish > counts && restore > publish, "the counts' layout correction covers the automatic expansion");
        string auto = Source("Desktop/Views/Archive/ArchivePage.ReplyAutoExpand.cs");
        Assert.Contains("_replyAutoExpand.Enable(rows, HasLiveCard)", auto);                       // the switch loads for the cards that exist now only
        Assert.Contains("row.RepliesRealised && HasLiveCard(row)", auto);                           // so does the count publication
        Assert.Contains("ArchivePosts.ContainerFromItem(row) != null", auto);
        Assert.Contains("RememberReplyChoice(row, open);", replies);              // the header counter is the reader's own choice
        Assert.Contains("row.Replies.Count != loadedBefore", replies);            // a late page never lands on restored replies
        string state = Source("Desktop/Views/Archive/ArchivePage.Navigation.cs");
        Assert.Contains("row.RepliesAutoExpanded = state.Expanded && state.AutoExpanded;", state);
    }
}

internal static class ReplyAutoExpandPolicyTestExtensions
{
    internal static ReplyAutoExpandPolicy Opened(this ReplyAutoExpandPolicy policy, ResourceId thread)
    {
        policy.SwitchThread(thread);
        return policy;
    }
}
