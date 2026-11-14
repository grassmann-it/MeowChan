using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Views.Shortcuts;
using MeowChan.FolderSources;
using MeowChan.Persistence;

namespace MeowChan.DesktopTests;

// Review of UX wave 3 (14.11.2026), the findings outside folder sources, tagging and profiles: W3-13 (an inline player whose native
// cleanup failed stays in its frame), W3-14 (a neighbour's failure never reaches the shown medium), W3-15 (localized causes, no raw
// exception text), W3-16 (a save keeps edits made meanwhile), W3-17/W3-26 (native rotation rule and its options), W3-25 (undo depth).
[Collection("Localization")]
public sealed class Wave3ReviewRestTests : IDisposable
{
    private readonly string _language = Texts.Language;
    public Wave3ReviewRestTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage(_language); }

    private const string Archive = "navigation.archive";
    private const string Rules = "navigation.rules";
    private static ShortcutGesture G(string text) => ShortcutGesture.Parse(text);

    // ---- W3-13 --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task APlayerWhoseNativeCleanupFailedStaysInItsFrameAndTheFailureIsRaisedOnce()
    {
        List<string> calls = new();
        InvalidOperationException failure = new("Native video cleanup failed; ownership is retained.");
        InvalidOperationException raised = await Assert.ThrowsAsync<InvalidOperationException>(() => InlineVideoDisposal.DisposeAsync(
            () => { calls.Add("dispose"); return ValueTask.FromException(failure); },
            () => calls.Add("remove"),
            () => calls.Add("retain")));
        Assert.Same(failure, raised);
        Assert.Equal(new[] { "dispose", "retain" }, calls);
    }

    [Fact]
    public async Task APlayerLeavesItsFrameOnlyAfterItsCleanupSucceeded()
    {
        List<string> calls = new();
        await InlineVideoDisposal.DisposeAsync(
            async () => { calls.Add("dispose-start"); await Task.Yield(); calls.Add("dispose-done"); },
            () => calls.Add("remove"),
            () => calls.Add("retain"));
        Assert.Equal(new[] { "dispose-start", "dispose-done", "remove" }, calls);
    }

    // ---- W3-14 --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ANeighbourThatCannotBeResolvedOrWarmedIsSkippedWithoutAnError()
    {
        List<string> warmed = new();
        Func<CancellationToken, Task<string?>>?[] neighbours =
        {
            _ => throw new IOException("C:\\private\\path\\neighbour.png could not be opened"),
            null,
            _ => Task.FromResult<string?>(null),
            _ => Task.FromResult<string?>("broken"),
            _ => Task.FromResult<string?>("good")
        };
        await ReaderMediaSidebarRules.PreloadNeighboursAsync(neighbours, (entry, _) =>
        {
            if (entry == "broken") throw new InvalidOperationException("decode failed");
            warmed.Add(entry);
            return Task.CompletedTask;
        }, CancellationToken.None);
        Assert.Equal(new[] { "good" }, warmed);
    }

    [Fact]
    public async Task CancellingThePreloadStillEndsItsRun()
    {
        using CancellationTokenSource cancel = new();
        List<string> warmed = new();
        Func<CancellationToken, Task<string?>>?[] neighbours =
        {
            _ => Task.FromResult<string?>("first"),
            _ => Task.FromResult<string?>("second")
        };
        await Assert.ThrowsAsync<OperationCanceledException>(() => ReaderMediaSidebarRules.PreloadNeighboursAsync(neighbours, (entry, _) =>
        {
            warmed.Add(entry);
            cancel.Cancel();
            return Task.CompletedTask;
        }, cancel.Token));
        Assert.Equal(new[] { "first" }, warmed);
    }

    // ---- W3-15 --------------------------------------------------------------------------------------------------------------

    public static TheoryData<string> Languages => new() { "en", "de", "es", "fr", "ru", "zh-Hans", "ja", "ko", "pt-BR", "cs", "it", "pl" };

    [Theory]
    [MemberData(nameof(Languages))]
    public void AFailureIsShownAsALocalizedCauseThatNeverCarriesThePathOrTheEnglishFrameworkText(string language)
    {
        Texts.SetLanguage(language);
        const string path = "/home/someone/secret-folder/file.json";
        Exception[] errors =
        {
            new IOException("Could not find file '" + path + "'."),
            new UnauthorizedAccessException("Access to the path '" + path + "' is denied."),
            new InvalidOperationException("Sequence contains no elements; " + path),
            new FileNotFoundException("Unable to find the specified file.", path)
        };
        string[] expectedKeys = { "error.cause.io", "error.cause.access", "error.cause.other", "error.cause.io" };
        for (int i = 0; i < errors.Length; i++)
        {
            string cause = UserFacingError.Describe(errors[i]);
            Assert.Equal(Texts.T(expectedKeys[i]), cause);
            Assert.DoesNotContain(path, cause);
            Assert.DoesNotContain(errors[i].Message, cause);
            Assert.DoesNotContain("Sequence contains", cause);
            string full = Texts.T("resume.readFailed", cause);
            Assert.DoesNotContain(path, full);
        }
        // The causes are real translations in every pack, not the English text.
        if (language != "en") Assert.NotEqual(EnglishOf("error.cause.other"), Texts.T("error.cause.other"));
    }

    private static string EnglishOf(string key) => BuiltInCatalog.English[key];

    [Fact]
    public void AnAlreadyLocalizedStoreLimitRefusalKeepsItsOwnSentence()
    {
        InvalidDataException refusal = PrivateStoreLimit.Refusal(PrivateStoreLimitKind.Bookmarks, 1_000, "English store message");
        string own = StoreLimitText.Describe(refusal)!;
        Assert.Equal(own, UserFacingError.Describe(refusal));
        Assert.Equal(own, UserFacingError.Describe(StoreLimitText.Localize(refusal)));
    }

    [Fact]
    public void AFolderCatalogRefusalKeepsItsOwnSentenceThroughTheOneHelper()
    {
        FolderSourceException refusal = new(FolderSourceError.CatalogNewer);
        Assert.Equal(FolderSourceErrorText.Message(refusal), UserFacingError.Describe(refusal));
    }

    [Fact]
    public void TheNamedSitesNoLongerPutTheRawExceptionTextIntoTheWindow()
    {
        string[] sites =
        {
            "Desktop/ReaderInlineVideoSlot.cs", "Desktop/Views/Archive/ArchivePage.MediaSidebar.cs", "Desktop/Views/Shortcuts/ShortcutBindingsEditor.cs",
            "Desktop/Controls/QuickSettingsPopover.cs", "Desktop/Views/Archive/ArchivePage.ViewPreferences.cs", "Desktop/Shell/MainWindow.Resume.cs"
        };
        foreach (string site in sites)
        {
            string source = File.ReadAllText(RepositoryPath(site));
            Assert.DoesNotContain("error.Message", source);
            Assert.DoesNotContain("exception.Message", source);
        }
    }

    // ---- W3-16 --------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ASaveKeepsEditsMadeWhileItRanAndTheSessionStaysDirtyAgainstTheSavedState()
    {
        ShortcutDraftSession session = new(new ShortcutBindingsSection());
        List<ShortcutDraftChange> changes = new();
        session.Clear(Archive);
        ShortcutBindingsSection saved = session.Draft;
        session.Changed += (_, change) => changes.Add(change);
        session.Clear(Rules); // the other surface edits while the save of `saved` runs
        session.CommitSaved(saved);
        Assert.True(session.IsDirty);
        Assert.True(session.Draft.HasOverride(Rules));
        Assert.True(session.Baseline.HasOverride(Archive));
        Assert.False(session.Baseline.HasOverride(Rules));
        Assert.Equal(1, session.ChangeCount); // only the newer edit differs from what was saved
        Assert.True(session.CanUndo);
        Assert.Equal(new[] { ShortcutDraftChange.Edited, ShortcutDraftChange.Rebased }, changes);
        // Discard goes back to the saved state, not to the state before the save.
        session.Discard();
        Assert.True(session.Draft.HasOverride(Archive));
        Assert.False(session.Draft.HasOverride(Rules));
        Assert.False(session.IsDirty);
    }

    [Fact]
    public void ASaveWithoutNewerEditsIsCleanAsBefore()
    {
        ShortcutDraftSession session = new(new ShortcutBindingsSection());
        session.Clear(Archive);
        ShortcutBindingsSection saved = session.Draft;
        session.CommitSaved(saved);
        Assert.False(session.IsDirty);
        Assert.False(session.CanUndo);
        Assert.Equal(0, session.ChangeCount);
        // An edit that was taken back again during the save is no newer edit either.
        session.Set(Rules, new[] { G("Primary+Shift+K") });
        session.Undo();
        session.CommitSaved(session.Draft);
        Assert.False(session.IsDirty);
    }

    [Fact]
    public void TheWindowSaveDoesNotReplaceAnEditMadeOnTheOtherSurfaceDuringTheSave()
    {
        ShortcutDraftSession session = new(new ShortcutBindingsSection());
        ShortcutBindingsEditor editor = new(session);
        List<ShortcutBindingsSection> commits = new();
        editor.SaveHandler = draft =>
        {
            commits.Add(draft);
            session.Clear(Rules); // the Settings card changes the shared draft while the window's save is in flight
            return Task.CompletedTask;
        };
        session.Clear(Archive);
        editor.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Single(commits);
        Assert.False(commits[0].HasOverride(Rules));
        Assert.True(session.Draft.HasOverride(Rules));
        Assert.True(session.IsDirty);
        Assert.True(editor.SaveButton.IsEnabled); // the newer edit can be saved
        Assert.Equal(Texts.T("shortcuts.window.savedPending"), editor.StatusText.Text);
        Assert.NotEqual(Texts.T("shortcuts.window.saved"), editor.StatusText.Text);
    }

    [Fact]
    public void AFailedWindowSaveSaysTheLocalizedCauseNotTheExceptionText()
    {
        ShortcutDraftSession session = new(new ShortcutBindingsSection());
        ShortcutBindingsEditor editor = new(session) { SaveHandler = _ => throw new IOException("Could not write '/secret/profile.json'.") };
        session.Clear(Archive);
        editor.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(session.IsDirty);
        Assert.Equal(Texts.T("shortcuts.window.saveFailed", Texts.T("error.cause.io")), editor.StatusText.Text);
        Assert.DoesNotContain("secret", editor.StatusText.Text);
    }

    // ---- W3-25 --------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheUndoHistoryIsBoundedAndKeepsTheNewestSteps()
    {
        ShortcutDraftSession session = new(new ShortcutBindingsSection());
        for (int i = 0; i < 1000; i++)
            session.Set(Archive, new[] { G(i % 2 == 0 ? "Primary+Shift+K" : "Primary+Shift+J") });
        Assert.Equal(ShortcutDraftSession.UndoDepth, session.UndoCount);
        int undone = 0;
        while (session.Undo()) undone++;
        Assert.Equal(ShortcutDraftSession.UndoDepth, undone);
        // The newest 200 of 1000 edits are the ones that came back (the oldest were dropped, so the first state is gone).
        Assert.True(session.Draft.HasOverride(Archive));
    }

    // ---- W3-17 / W3-26 ------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Playing", 60_000L, true, "ReopenAtPosition")]
    [InlineData("Paused", 60_000L, true, "ReopenAtPosition")]
    [InlineData("Playing", 60_000L, false, "Unavailable")]
    [InlineData("Playing", 0L, true, "Unavailable")]
    [InlineData("Paused", 0L, false, "Unavailable")]
    [InlineData("Stopped", 0L, false, "ApplyOnNextOpen")]
    [InlineData("Stopped", 60_000L, true, "ApplyOnNextOpen")]
    public void ARunningVideoThatCannotReopenAtItsPositionIsNeverMarkedAsTurned(string mode, long length, bool seekable, string expected) =>
        Assert.Equal(Enum.Parse<NativeRotationAction>(expected), VideoNativeRotation.Decide(Enum.Parse<VideoPlaybackMode>(mode), length, seekable));

    [Fact]
    public void ARotationThatIsAlreadyAttachedAddsNoOptionsAndATurnAddsExactlyOneSet()
    {
        Assert.Empty(VideoNativeRotation.OptionsToAdd(90, 90));
        Assert.Empty(VideoNativeRotation.OptionsToAdd(0, 360));
        Assert.Empty(VideoNativeRotation.OptionsToAdd(270, -90));
        int applied = 0;
        List<string> attached = new();
        for (int turn = 1; turn <= 20; turn++)
        {
            int wanted = turn * 90 % 360;
            string[] options = VideoNativeRotation.OptionsToAdd(applied, wanted);
            // One set per turn: the filter and its transform-type (upright removes the filter only), never more.
            Assert.Equal(wanted == 0 ? 1 : 2, options.Length);
            Assert.Equal(wanted == 0 ? 0 : 1, options.Count(option => option.StartsWith(":transform-type=")));
            attached.AddRange(options);
            applied = wanted;
            // A repeated request for the same turn (a view update) adds nothing.
            Assert.Empty(VideoNativeRotation.OptionsToAdd(applied, wanted));
        }
        // 20 distinct turns attach 20 sets and no more: never one per view update.
        Assert.Equal(20, attached.Count(option => option.StartsWith(":video-filter=")));
        Assert.Equal(15, attached.Count(option => option.StartsWith(":transform-type=")));
        Assert.Equal(new[] { ":video-filter=transform", ":transform-type=90" }, VideoNativeRotation.OptionsToAdd(0, 90));
    }

    private static string RepositoryPath(string relative, [CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", relative));
}
