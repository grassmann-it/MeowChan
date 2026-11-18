using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Feedback;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>Live regions of the rebuilt Danbooru cards and of the shell toasts (1.1.0 accessibility audit H1, H4).</summary>
[Collection("Localization")]
public sealed class BooruAccessibilityTests : IDisposable
{
    public BooruAccessibilityTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static readonly BooruNotice Created = new(BooruNoticeKind.Success, "booru.ui.watcher.created", new object?[] { "cat" });

    [Fact]
    public void ARowThatChangesIsMirroredOnceAndAppearingRowsAnnounceNothing()
    {
        BooruLiveMirror<(BooruRowLoad, string?)?> mirror = new();
        // A new result page: rows only appear, nothing is mirrored.
        Assert.False(mirror.Update(new (string, (BooruRowLoad, string?)?)[] { ("1", null), ("2", (BooruRowLoad.AlreadyLoaded, null)) }, null));
        Assert.Null(mirror.RowId);
        // The same state rendered again (language change, clock) changes nothing.
        Assert.False(mirror.Update(new (string, (BooruRowLoad, string?)?)[] { ("1", null), ("2", (BooruRowLoad.AlreadyLoaded, null)) }, null));
        // "Load original" on row 1: handing over, then queued; each step is mirrored.
        Assert.True(mirror.Update(new (string, (BooruRowLoad, string?)?)[] { ("1", (BooruRowLoad.Handing, null)), ("2", (BooruRowLoad.AlreadyLoaded, null)) }, null));
        Assert.Equal("1", mirror.RowId);
        Assert.True(mirror.Update(new (string, (BooruRowLoad, string?)?)[] { ("1", (BooruRowLoad.Queued, null)), ("2", (BooruRowLoad.AlreadyLoaded, null)) }, null));
        Assert.Equal("1", mirror.RowId);
        Assert.False(mirror.Update(new (string, (BooruRowLoad, string?)?)[] { ("1", (BooruRowLoad.Queued, null)), ("2", (BooruRowLoad.AlreadyLoaded, null)) }, null));
        // A newer tab notice wins over the mirrored row; the same notice object again does not.
        Assert.True(mirror.Update(new (string, (BooruRowLoad, string?)?)[] { ("1", (BooruRowLoad.Queued, null)) }, Created));
        Assert.Null(mirror.RowId);
        Assert.False(mirror.Update(new (string, (BooruRowLoad, string?)?)[] { ("1", (BooruRowLoad.Queued, null)) }, Created));
        // A row that leaves the list stops being mirrored.
        mirror.Update(new (string, (BooruRowLoad, string?)?)[] { ("3", null), ("4", null) }, Created);
        mirror.Update(new (string, (BooruRowLoad, string?)?)[] { ("3", (BooruRowLoad.Failed, "x")) }, Created);
        Assert.Equal("3", mirror.RowId);
        mirror.Update(new (string, (BooruRowLoad, string?)?)[] { ("4", null) }, Created);
        Assert.Null(mirror.RowId);
    }

    [Fact]
    public void AWatcherMessageIsComparedByIdentitySoARepeatedSentenceIsAnnouncedAgain()
    {
        BooruLiveMirror<BooruNotice> mirror = new(ReferenceEqualityComparer.Instance);
        BooruNotice paused = new(BooruNoticeKind.Info, "booru.ui.watcher.msg.paused");
        BooruNotice pausedAgain = new(BooruNoticeKind.Info, "booru.ui.watcher.msg.paused");
        Assert.Equal(paused, pausedAgain); // value-equal records: only the identity tells the two results apart
        mirror.Update(new (string, BooruNotice?)[] { ("w1", null), ("w2", null) }, null);
        Assert.True(mirror.Update(new (string, BooruNotice?)[] { ("w1", paused), ("w2", null) }, null));
        Assert.Equal("w1", mirror.RowId);
        // The busy step (message cleared) and the next result are coalesced into one render.
        Assert.True(mirror.Update(new (string, BooruNotice?)[] { ("w1", pausedAgain), ("w2", null) }, null));
        Assert.Equal("w1", mirror.RowId);
        // Removing the watcher: its row goes and the removal notice is shown instead.
        BooruNotice removed = new(BooruNoticeKind.Success, "booru.ui.watcher.msg.removed");
        mirror.Update(new (string, BooruNotice?)[] { ("w2", null) }, removed);
        Assert.Null(mirror.RowId);
    }

    [Fact]
    public void EveryToastIsANamedLiveRegionAndOnlyErrorsInterrupt()
    {
        FeedbackToastContent info = new("Danbooru watcher", "Watcher “cat” found new posts.", null, _ => { });
        SelectableTextBlock message = Assert.Single(info.GetLogicalDescendants().OfType<SelectableTextBlock>());
        Assert.Equal("Watcher “cat” found new posts.", message.Text);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(message));
        Assert.Equal("Danbooru watcher. Watcher “cat” found new posts.", AutomationProperties.GetName(info));
        // The title is not live: one announcement per toast.
        TextBlock title = info.GetLogicalDescendants().OfType<TextBlock>().First(block => block is not SelectableTextBlock);
        Assert.Equal(AutomationLiveSetting.Off, AutomationProperties.GetLiveSetting(title));

        SessionFeedbackStore store = new();
        SessionFeedback error = store.Record(FeedbackCategory.Error, "clipboard.write", null, "Clipboard unavailable")!;
        SessionFeedback warning = store.Record(FeedbackCategory.Warning, "directory.open", null, "Folder missing")!;
        FeedbackToastContent errorToast = new("Error", error.Summary, error, _ => { });
        FeedbackToastContent warningToast = new("Warning", warning.Summary, warning, _ => { });
        Assert.Equal(AutomationLiveSetting.Assertive,
            AutomationProperties.GetLiveSetting(errorToast.GetLogicalDescendants().OfType<SelectableTextBlock>().Single()));
        Assert.Equal(AutomationLiveSetting.Polite,
            AutomationProperties.GetLiveSetting(warningToast.GetLogicalDescendants().OfType<SelectableTextBlock>().Single()));
        Assert.Equal("Error. Clipboard unavailable", AutomationProperties.GetName(errorToast));
        // Information is never recorded as shell feedback, so the toast (and its live region) is its only channel.
        Assert.Null(store.Record(FeedbackCategory.Information, "booru.watcher.queued", "w1", "found"));
        Assert.Equal("Only message", FeedbackToastContent.AccessibleName("", "Only message"));
        Assert.Equal("Done. Saved", FeedbackToastContent.AccessibleName("Done.", "Saved"));
    }
}
