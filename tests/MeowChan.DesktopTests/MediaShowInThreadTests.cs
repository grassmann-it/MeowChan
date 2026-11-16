using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Platform;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using ArchiveCatalog = MeowChan.Archive.ArchiveCatalog;

namespace MeowChan.DesktopTests;

/// <summary>
/// Tom's hand test (16.11.2026): Media -> "Show in thread" opened the thread in the archive's inline reader, the list did not hold it and the
/// large reader (button and Ctrl+Enter) refused because its gate asked the list for exactly one selected row. Now the reader's commands follow the
/// loaded thread, the jump selects the thread in the list (paging, or replacing a search that does not find it) and opens the large reader on the
/// post, and the way back to Media travels with the reader's pane into the large reader.
/// </summary>
[Collection("Gallery viewer construction")]
public sealed class MediaShowInThreadTests
{
    private const string Origin = "synthetic-A";
    private static readonly ResourceId Jumped = new(Origin, "board-B", "9001");

    // ---- A: thread commands follow the loaded thread, not the list ----

    [Fact]
    public async Task TheLargeReaderAndTheTagsKeyActOnALoadedThreadTheListDoesNotHold()
    {
        ArchivedThreadView view = await SeedOneAndReadAsync();
        BooruComponentDispatcher.Run(() => WithPage(page =>
        {
            page.IsAvailable = true;
            Task load = page.LoadThreadAsync(view.Summary.Id, true, view);
            Assert.True(load.IsCompletedSuccessfully);
            Assert.Empty(page.FindControl<ListBox>("ArchiveThreads")!.SelectedItems!.OfType<object>()); // the list holds no row at all
            Assert.True(page.CanExpandReader);
            Assert.True(page.FindControl<Button>("ArchiveReadPosts")!.IsEnabled, "Large reader must act on the thread the reader shows.");
            Assert.Equal(view.Summary.Id, page.ThreadTagsShortcutTarget);
        }));
    }

    [Fact]
    public void TheLargeReaderGateReadsNoListSelection()
    {
        string gate = Member("Desktop/Views/Archive/ArchivePage.Gallery.cs", "internal bool CanExpandReader");
        Assert.DoesNotContain("FrozenArchiveSelection", gate);
        Assert.DoesNotContain("ArchiveLead", gate);
        Assert.DoesNotMatch(@"IsVisible|IsEffectivelyVisible", gate);
        Assert.Contains("ArchiveReadPosts.IsEnabled = CanExpandReader;", Source("Desktop/Views/Archive/ArchivePage.axaml.cs"));
        string click = Body("Desktop/Views/Archive/ArchivePage.Gallery.cs", "private async void OnReadPostsClick");
        Assert.Contains("CanExpandReader", click);
        Assert.DoesNotContain("FrozenArchiveSelection", click);
    }

    // ---- B: the list reflects the jump ----

    [Fact]
    public async Task TheRevealSelectsARowPagesTheSearchAndReplacesOnlyASearchThatMissesTheThread()
    {
        using TempDirectory temp = new();
        await SeedManyAsync(temp.Path);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(temp.Path);
        ArchiveSearchRequest all = ArchiveSearchRequest.Create(new ArchiveSearchSpec(sort: ArchiveThreadSort.Title));
        ResourceId[] order = (await reader.SelectThreadIdsBySpecAsync(all)).ToArray();
        Assert.Equal(63, order.Length);

        // 1. A row of the page shown: selected in place, nothing read.
        ArchivePage.ThreadRevealPlan here = await Plan(reader, order[3], order.Take(50).ToArray(), all);
        Assert.Equal(ArchivePage.ThreadRevealKind.Selected, here.Kind);
        Assert.Null(here.Request);

        // 2. Found by the applied search on another page: that page of the same search.
        ResourceId later = order[55];
        ArchivePage.ThreadRevealPlan paged = await Plan(reader, later, order.Take(50).ToArray(), all);
        Assert.Equal(ArchivePage.ThreadRevealKind.Paged, paged.Kind);
        Assert.Same(all, paged.Request);
        Assert.Equal(50, paged.Offset);

        // 3. Not found by the applied search (another board): the thread's board, the sort kept, on the page that holds it.
        ArchiveSearchRequest boardA = ArchiveSearchRequest.Create(new ArchiveSearchSpec(boards: [new ArchiveBoardConstraint(Origin, "board-A")],
            sort: ArchiveThreadSort.Title, text: "synthetic"));
        ArchivePage.ThreadRevealPlan replaced = await Plan(reader, Jumped, Array.Empty<ResourceId>(), boardA);
        Assert.Equal(ArchivePage.ThreadRevealKind.SearchReplaced, replaced.Kind);
        Assert.NotNull(replaced.Request);
        Assert.Equal(new[] { new ArchiveBoardConstraint(Origin, "board-B") }, replaced.Request!.Spec.Boards);
        Assert.Equal(string.Empty, replaced.Request.Spec.Text);
        Assert.Equal(ArchiveThreadSort.Title, replaced.Request.Spec.Sort);
        Assert.Equal(0, replaced.Offset);

        // A work filter that excludes the thread counts as "not found" (the frozen membership, as the list applies it).
        ArchivePage.ThreadRevealPlan excluded = await Plan(reader, later, Array.Empty<ResourceId>(), all, eligible: new HashSet<string>());
        Assert.Equal(ArchivePage.ThreadRevealKind.SearchReplaced, excluded.Kind);

        // The archive does not hold the thread: nothing changes.
        ArchivePage.ThreadRevealPlan missing = await Plan(reader, new ResourceId(Origin, "board-B", "404"), Array.Empty<ResourceId>(), all);
        Assert.Equal(ArchivePage.ThreadRevealKind.None, missing.Kind);
        Assert.Null(missing.Request);
    }

    [Fact]
    public void TheRevealedSearchHasAWayBackAndTheJumpReadsNoStaleThread()
    {
        string reveal = Body("Desktop/Views/Archive/ArchivePage.ThreadReveal.cs", "internal async Task<ThreadReveal> RevealThreadInListAsync");
        Assert.Contains("ReloadShownThread = false", reveal); // the jump loads its own thread next
        Assert.Contains("ReferenceEquals(_appliedSearch, request)", reveal); // the way back yields to a search of the user's own
        Assert.Contains("WriteSearchDraft(applied.Spec)", reveal);
        Assert.Contains("pending.ReloadShownThread", Body("Desktop/Views/Archive/ArchivePage.axaml.cs", "private async Task<bool> LoadListAsync"));
        string toast = Member("Desktop/Shell/MainWindow.MediaReturn.cs", "private void ShowRevealedSearchToast");
        Assert.Contains("archive.reveal.restoreSearch", toast);
        Assert.Contains("ShowActionNotification", toast);
    }

    // ---- C and D: the large reader on the post, the way back, the guards ----

    [Fact]
    public void ShowInThreadFromMediaAndTaggingOpensTheLargeReaderOnThePostOverTheRevealedArchive()
    {
        const string file = "Desktop/Shell/MainWindow.MediaReturn.cs";
        string jump = Body(file, "private async Task ShowThreadPostInLargeReaderAsync");
        string[] steps = ["RevealThreadInListAsync(thread)", "LoadThreadAsync(thread)", "OpenLargeReader(archive.ThreadListFocusTarget)",
            "FocusPostAsync(postId)", "EnsureLargeReaderFocus()"];
        int at = -1;
        foreach (string step in steps)
        {
            int next = jump.IndexOf(step, StringComparison.Ordinal);
            Assert.True(next > at, step + " must follow the previous step of the jump.");
            at = next;
        }
        // Every step after an await asks whether this jump still owns the archive (a later jump, a return, leaving the page).
        Assert.Contains("request == _mediaJumpRequest", Member(file, "bool Current()"));
        Assert.Equal(3, Regex.Matches(jump, @"Current\(\)").Count - 1);
        Assert.Contains("ShowThreadPostInLargeReaderAsync(entry.Thread, entry.PostId, request)", Body(file, "private async Task ShowMediaLibraryEntryInThreadAsync"));
        Assert.Contains("ShowThreadPostInLargeReaderAsync(item.Thread, item.PostId, request)",
            Body("Desktop/Shell/MainWindow.TaggingPage.cs", "private async Task ShowTaggingEntryInThreadAsync"));
    }

    [Fact]
    public void BackToMediaTravelsWithThePaneIntoTheLargeReaderAndClosesItOnReturn() => BooruComponentDispatcher.Run(() => WithPage(page =>
    {
        Button back = page.FindControl<Button>("ArchiveMediaReturn")!;
        Grid pane = page.FindControl<Grid>("ArchiveReaderPane")!;
        Assert.Contains(pane, back.GetVisualAncestors());
        Control frame = page.DetachReader();
        page.ShowMediaReturn(true);
        Assert.Contains(frame, back.GetVisualAncestors()); // the large reader's header has the way back
        Assert.True(back.IsVisible);
        bool returned = false;
        page.MediaReturn = () => returned = true;
        back.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.True(returned);
        page.AttachReader(frame);

        // The shell's gates: the way back is offered while the large reader is open, and taking it closes the reader without its origin.
        const string file = "Desktop/Shell/MainWindow.MediaReturn.cs";
        Assert.DoesNotContain("_largeReaderOpen", Member(file, "private bool CanReturnToMedia"));
        Assert.DoesNotContain("_largeReaderOpen", Member(file, "private void UpdateMediaReturn"));
        string ret = Body(file, "private void ReturnToMedia");
        Assert.True(ret.IndexOf("CloseLargeReader(restoreOrigin: false)", StringComparison.Ordinal)
            < ret.IndexOf("NavigateWorkspace(WorkspacePage.Media)", StringComparison.Ordinal));
    }));

    [Fact]
    public void AJumpIntoTheLargeReaderNeverLeavesTheFocusOnNothing()
    {
        // The post's focus is the jump's own; a post that could not take it (gone, hidden by the view) leaves the reader's Close focused,
        // never nothing or the page behind the reader (the real window is checked by the media return smoke).
        string ensure = Body("Desktop/Views/Archive/ArchivePage.ThreadReveal.cs", "internal void EnsureLargeReaderFocus");
        Assert.Contains("_largeReaderFrame ?? (Visual)ArchiveReaderPane", ensure);
        Assert.Contains("GetSelfAndVisualAncestors().Contains(reader)) return;", ensure);
        Assert.Contains("ArchiveCloseReader.Focus();", ensure);
        string jump = Body("Desktop/Shell/MainWindow.MediaReturn.cs", "private async Task ShowThreadPostInLargeReaderAsync");
        Assert.True(jump.IndexOf("EnsureLargeReaderFocus", StringComparison.Ordinal) > jump.IndexOf("FocusPostAsync(postId)", StringComparison.Ordinal));
    }

    // ---- helpers ----

    private static Task<ArchivePage.ThreadRevealPlan> Plan(ArchiveReader reader, ResourceId thread, ResourceId[] visible, ArchiveSearchRequest applied,
        IReadOnlySet<string>? eligible = null) =>
        ArchivePage.PlanThreadRevealAsync(reader, thread, visible, applied, eligible, ArchiveSearchRequest.Create, 50, CancellationToken.None);

    private static void WithPage(Action<MeowChan.ArchivePage> test)
    {
        using IDisposable scope = AvaloniaLocator.EnterScope();
        AvaloniaLocator.CurrentMutable.Bind<ICursorFactory>().ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
        TokenApplication.Bind();
        MeowChan.ArchivePage page = new(_ => Task.FromResult<ArchiveReader>(null!), (_, _) => Task.CompletedTask, _ => { });
        try
        {
            page.IsReadable = true;
            test(page);
        }
        finally { (page as IDisposable)?.Dispose(); }
    }

    private static async Task<ArchivedThreadView> SeedOneAndReadAsync()
    {
        using TempDirectory temp = new();
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(temp.Path, lease))
            await Capture(catalog, temp.Path, Jumped, 12);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(temp.Path);
        return (await reader.ReadThreadAsync(Jumped))!;
    }

    // 60 threads on board-A (two list pages), 3 on board-B (the jumped one among them).
    private static async Task SeedManyAsync(string profile)
    {
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        for (int i = 0; i < 60; i++) await Capture(catalog, profile, new ResourceId(Origin, "board-A", (1000 + i).ToString()), 1);
        foreach (string thread in new[] { "9000", "9001", "9002" }) await Capture(catalog, profile, new ResourceId(Origin, "board-B", thread), 1);
    }

    private static Task Capture(ArchiveCatalog catalog, string profile, ResourceId id, int posts) => catalog.CaptureAsync(new(id,
        "https://example.invalid/" + id.Board + "/" + id.Thread, "synthetic " + id.Board + " " + id.Thread, ThreadSourceState.NotFound,
        DateTimeOffset.UnixEpoch, Path.Combine(profile, "uncreated-media"),
        Enumerable.Range(0, posts).Select(i => new CapturedPost("p" + i.ToString("D3"), i, PostFieldPresence.All, Html: "stored " + i)).ToArray()));

    /// <summary>The declaration from <paramref name="start"/> to the end of its statement (expression-bodied) or of its closing brace.</summary>
    private static string Member(string file, string start)
    {
        string text = Source(file);
        int index = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(index >= 0, start + " is not in " + file);
        int semicolon = text.IndexOf(';', index), brace = text.IndexOf('{', index);
        return brace >= 0 && brace < semicolon ? Body(file, start) : text[index..(semicolon + 1)];
    }

    private static string Body(string file, string start)
    {
        string text = Source(file);
        int index = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(index >= 0, start + " is not in " + file);
        int open = text.IndexOf('{', index), depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}' && --depth == 0) return text[index..(i + 1)];
        }
        throw new InvalidOperationException("Unbalanced braces after " + start);
    }

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../.."));

    private static string Source(string relative) => File.ReadAllText(Path.Combine(Root(), relative));
}
