using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Platform;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

/// <summary>
/// Guards of the bug-class audit (16.11.2026) for the archive reader: a command is gated on state, never on whether a button is visible (class 3);
/// whatever the page itself listens to for the reader is also on the pane while the large reader hosts it in the shell (class 7); a held-key latch
/// ends with the focus it was set for (class 2).
/// </summary>
[Collection("Gallery viewer construction")]
public sealed class ReaderStateGatesAndHostingTests
{
    // ---- class 3: the paging gesture ----

    // previous, referencesTab, readReady, hasThread, postOffset, pageSize, shown, hasBacklinkPost, refOffset, refTotal, refPageSize
    private static ReaderPageTarget Page(bool previous, bool references = false, bool ready = true, bool thread = true, int offset = 0,
        int size = 25, int shown = 100, bool backlink = true, int refOffset = 0, int refTotal = 60, int refSize = 20)
        => ReaderPaging.Target(previous, references, ready, thread, offset, size, shown, backlink, refOffset, refTotal, refSize);

    [Fact]
    public void PostsPageByStateAloneWhateverThePagerRowShows()
    {
        // No input says whether a pager button is visible: the target is the same with the pager collapsed, hidden by a work view or moved to More.
        Assert.Equal(ReaderPageTarget.Posts, Page(previous: false));
        Assert.Equal(ReaderPageTarget.Posts, Page(previous: true, offset: 25));
        Assert.Equal(ReaderPageTarget.None, Page(previous: true, offset: 0));
        Assert.Equal(ReaderPageTarget.None, Page(previous: false, offset: 75));
        Assert.Equal(ReaderPageTarget.Posts, Page(previous: false, offset: 50));
    }

    [Fact]
    public void ThePagingGestureDoesNothingWhereThereIsNothingToPage()
    {
        Assert.Equal(ReaderPageTarget.None, Page(previous: false, shown: 25)); // one page: no pager row, nothing to page
        Assert.Equal(ReaderPageTarget.None, Page(previous: false, ready: false));
        Assert.Equal(ReaderPageTarget.None, Page(previous: false, thread: false));
    }

    [Fact]
    public void TheReferencesTabPagesTheBacklinksAndNotThePosts()
    {
        Assert.Equal(ReaderPageTarget.References, Page(previous: false, references: true));
        Assert.Equal(ReaderPageTarget.None, Page(previous: true, references: true, refOffset: 0));
        Assert.Equal(ReaderPageTarget.References, Page(previous: true, references: true, refOffset: 20));
        Assert.Equal(ReaderPageTarget.None, Page(previous: false, references: true, refOffset: 40));
        Assert.Equal(ReaderPageTarget.None, Page(previous: false, references: true, backlink: false));
        // On the References tab the posts' offset does not matter, even when posts could be paged.
        Assert.Equal(ReaderPageTarget.None, Page(previous: true, references: true, offset: 50, refOffset: 0));
    }

    [Fact]
    public void TheGalleryReturnIsOfferedByStateNotByItsButton() => RunPage(page =>
    {
        bool returned = false;
        page.GalleryReturn = () => returned = true;
        Assert.False(page.CanReturnToGallery);
        page.ShowGalleryReturn(true);
        Assert.True(page.CanReturnToGallery);
        // A layout that moves the button off the bar (More, narrow chrome) must not take the gesture with it.
        page.FindControl<Button>("ArchiveGalleryReturn")!.IsVisible = false;
        Assert.True(page.CanReturnToGallery);
        page.ReturnToGalleryByShortcut();
        Assert.True(returned);
        page.ShowGalleryReturn(false);
        Assert.False(page.CanReturnToGallery);
    });

    [Fact]
    public void NoReaderGestureGateReadsAButtonsVisibility()
    {
        // Back and return are gestures of the whole reader; a button placed elsewhere or hidden for the layout does not decide them.
        Assert.DoesNotMatch(@"IsVisible|IsEffectivelyVisible|IsEnabled", Member("Desktop/Views/Archive/ArchivePage.Navigation.cs", "internal bool CanGoHistoryBack"));
        Assert.DoesNotMatch(@"IsVisible|IsEffectivelyVisible|IsEnabled", Member("Desktop/Views/Archive/ArchivePage.Gallery.cs", "internal bool CanReturnToGallery"));
        Assert.DoesNotMatch(@"IsVisible|IsEffectivelyVisible|IsEnabled", Body("Desktop/Views/Archive/ArchivePage.Reader.cs", "private bool TryPageByShortcut"));
    }

    // Gates that read a visibility on purpose, none of them a button's: the two live shortcuts act "where the live bar is shown" (the status line follows the
    // reader's tab and the page that owns it, and is Required in every work view); CanCheckReaderUpdate asks whether the page itself is on screen (a timer, not
    // a command); CanExportResults reads the export dialog's modal state. Anything new must be gated on state.
    private static readonly string[] VisibilityGatesByDesign =
        ["CanToggleFollowLiveByShortcut", "CanShowNewPostsByShortcut", "CanCheckReaderUpdate", "CanExportResults"];

    [Fact]
    public void NoArchivePageCanMemberReadsAControlsVisibility()
    {
        var offenders = new List<string>();
        var member = new Regex(@"\bbool\s+(Can\w+)\s*(?:\([^)]*\))?\s*=>([^;]*);");
        foreach (string file in ArchivePageFiles())
            foreach (Match match in member.Matches(File.ReadAllText(file)))
                if (Regex.IsMatch(match.Groups[2].Value, @"\.IsVisible\b|\bIsEffectivelyVisible\b|\bIsVisible\b")
                    && !VisibilityGatesByDesign.Contains(match.Groups[1].Value))
                    offenders.Add(Path.GetFileName(file) + ": " + match.Groups[1].Value);
        Assert.True(offenders.Count == 0, "A command gate reads a control's visibility (gate on state; a hidden button must not kill a shortcut):\n" + string.Join("\n", offenders));
    }

    // ---- class 7: the large reader hosts the pane outside the page ----

    // Page-level handlers that belong to the results page only (their commands are not offered in the large reader).
    private static readonly string[] ListOnlyPageHandlers =
        ["RouteArchiveCleanupCommand", "RouteTransferExportCommand", "RouteSavedSearchCommand", "RouteTransferPreviewCommand"];

    [Fact]
    public void EveryHandlerThePageAddsForItselfIsAlsoOnThePaneInTheLargeReader()
    {
        string wire = Body("Desktop/Views/Archive/ArchivePage.Reader.cs", "private void WireReaderSurface");
        string unwire = Body("Desktop/Views/Archive/ArchivePage.Reader.cs", "private void UnwireReaderSurface");
        var bare = new Regex(@"(?<![\w.])AddHandler\(\s*[\w.]+\s*,\s*(\w+)");
        var offenders = new List<string>();
        foreach (string file in ArchivePageFiles())
            foreach (Match match in bare.Matches(File.ReadAllText(file)))
            {
                string handler = match.Groups[1].Value;
                if (ListOnlyPageHandlers.Contains(handler)) continue;
                if (!wire.Contains(handler, StringComparison.Ordinal)) offenders.Add(Path.GetFileName(file) + ": " + handler + " is not on the pane (WireReaderSurface)");
                else if (!unwire.Contains(handler, StringComparison.Ordinal)) offenders.Add(Path.GetFileName(file) + ": " + handler + " is not removed (UnwireReaderSurface)");
            }
        Assert.True(offenders.Count == 0,
            "The page listens to something for the reader that the large reader's pane (moved into the shell) never raises to it; add it to WireReaderSurface, or to the list-only allowlist if it belongs to the results page:\n"
            + string.Join("\n", offenders));
    }

    [Fact]
    public void BothHostsGoThroughTheSamePaneWiring()
    {
        string source = Source("Desktop/Views/Archive/ArchivePage.Reader.cs");
        Assert.Contains("WireReaderSurface(pane);", Body("Desktop/Views/Archive/ArchivePage.Reader.cs", "internal Control DetachReader"));
        Assert.Contains("UnwireReaderSurface(pane);", Body("Desktop/Views/Archive/ArchivePage.Reader.cs", "internal void AttachReader"));
        string wire = Body("Desktop/Views/Archive/ArchivePage.Reader.cs", "private void WireReaderSurface");
        // The rating caption of a media tile and the Menu key reach the pane only through it.
        Assert.Contains("RateAndTagRequestedEvent, OnTileRateAndTagRequested", wire);
        Assert.Contains("KeyDownEvent, OnArchiveMediaContextKey", wire);
        Assert.DoesNotContain("pane.AddHandler(", source.Replace(wire, string.Empty, StringComparison.Ordinal));
    }

    // ---- class 2: the held-key latch of the reader's media keys ----

    [Fact]
    public void TheHeldMediaKeysEndWithTheFocusAndTheWindowThatSetThem()
    {
        string focus = Member("Desktop/Views/Archive/ArchivePage.Reader.cs", "private void OnReaderMediaFocusChanged");
        Assert.Contains("_readerMediaHeldKeys.Clear()", focus);
        Assert.Contains("_readerMediaHeldKeys.Clear()", Body("Desktop/Views/Archive/ArchivePage.Reader.cs", "private void OnReaderMediaOwnerDeactivated"));
        Assert.Contains("_readerMediaHeldKeys.Clear()", Body("Desktop/Views/Archive/ArchivePage.Reader.cs", "private void StopObservingReaderMediaKeys"));
    }

    // ---- helpers ----

    private static void RunPage(Action<MeowChan.ArchivePage> test) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable scope = AvaloniaLocator.EnterScope();
        AvaloniaLocator.CurrentMutable.Bind<ICursorFactory>().ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
        TokenApplication.Bind();
        MeowChan.ArchivePage page = new();
        try { test(page); }
        finally { (page as IDisposable)?.Dispose(); }
    });

    private static IEnumerable<string> ArchivePageFiles() =>
        Directory.EnumerateFiles(Path.Combine(Root(), "Desktop/Views/Archive"), "ArchivePage*.cs");

    /// <summary>The declaration from <paramref name="start"/> to the end of its statement (expression-bodied) or of its closing brace.</summary>
    private static string Member(string file, string start)
    {
        string text = Source(file);
        int index = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(index >= 0, start + " is not in " + file);
        int semicolon = text.IndexOf(';', index), brace = text.IndexOf('{', index);
        return brace >= 0 && brace < semicolon ? Body(file, start) : text[index..(semicolon + 1)];
    }

    /// <summary>The braces of the member that starts with <paramref name="start"/>.</summary>
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
