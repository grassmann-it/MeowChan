using System.Runtime.CompilerServices;
using Avalonia.Input;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;

namespace MeowChan.DesktopTests;

// Synthetic core seams and source wiring contracts only: not native input/focus evidence.
public sealed class ReaderThreadFindUiTests
{
    private static ArchivedThreadView View(long revision = 7) => new(
        new ArchiveThreadSummary(new ResourceId("synthetic", "b", "404"), "", "", "",
            ThreadSourceState.NotFound, true, DateTimeOffset.UnixEpoch, null, 651, revision),
        Enumerable.Range(0, 651).Select(i => new CapturedPost("p" + i, i,
            PostFieldPresence.All, Html: i == 650 ? "<b>needle</b> needle" : "other")).ToArray(),
        decimalPostOrder: false);

    [Fact]
    public void NavigationUsesWholeSavedThreadAndWrapsDistinctHitsInOnePost()
    {
        var result = ReaderThreadFindIndex.Create(View(), "needle");
        int cursor = ReaderThreadFindIndex.Next(-1, result.Hits.Count);
        Assert.Equal(0, cursor);
        Assert.Equal("p650", result.Texts[result.Hits[cursor].TextIndex].PostId);
        Assert.Equal(650, result.Texts[result.Hits[cursor].TextIndex].PostIndex);
        cursor = ReaderThreadFindIndex.Previous(cursor, result.Hits.Count);
        Assert.Equal(1, cursor);
        Assert.Equal("needle needle", result.Texts[result.Hits[cursor].TextIndex].Text);
        Assert.False(result.Matches(View(8).Summary.Id, 8));
        Assert.Equal(ReaderFindTruncation.None, result.Truncation);
    }

    [Fact]
    public void NoHitAndTruncationAreNotInventedSelections()
    {
        Assert.Equal(-1, ReaderThreadFindIndex.Next(-1,
            ReaderThreadFindIndex.Create(View(), "absent").Hits.Count));
        var limited = ReaderThreadFindIndex.Create(View(), "needle", new(MaxPosts: 500));
        Assert.Empty(limited.Hits);
        Assert.True(limited.Truncation.HasFlag(ReaderFindTruncation.Posts));
    }

    [Fact]
    public void DedicatedBarOwnsKeysAndDoesNotInstallGlobalFindShortcut()
    {
        string xaml = Source("Desktop/Views/Archive/ArchivePage.axaml");
        string queryElement = System.Text.RegularExpressions.Regex.Match(xaml,
            "<(?:controls:SearchBox|TextBox)[^>]*x:Name=\"ReaderFindQuery\"[^>]*>").Value;
        Assert.NotEmpty(queryElement);
        Assert.DoesNotContain("MaxLength=", queryElement);
        Assert.DoesNotContain("KeyDown=\"OnThreadFindKeyDown\"", xaml);
        foreach (string name in new[] { "ReaderFindOpen", "ReaderFindQuery", "ReaderFindStatus",
                     "ReaderFindExcerpt", "ReaderFindPrevious", "ReaderFindNext", "ReaderToolsClose" })
            Assert.Contains("x:Name=\"" + name + "\"", xaml);
        string reader = Source("Desktop/Views/Archive/ArchivePage.Reader.cs");
        Assert.Contains("if (IsThreadFindSource(e.Source)) return;", reader);
        string find = Source("Desktop/Views/Archive/ArchivePage.ThreadFind.cs");
        Assert.Contains("result.Texts[result.Hits[_threadFindCursor].TextIndex]", find);
        Assert.Contains("ShowPost(hit.PostId)", find);
        Assert.Contains("ExpandPostForFind(hit.PostId)", find); // A folded post with a body hit opens (I6).
        Assert.Contains("request == _threadFindRequest", find);
        Assert.Contains("result.Matches(_thread.Summary.Id, _thread.Summary.Revision)", find);
        Assert.Contains("CreateLinkedTokenSource(_lifetime.Token)", find);
        Assert.Contains("KeyModifiers.Shift", find);
        Assert.DoesNotContain("CommandBindings", find);
        Assert.DoesNotContain("OpenGallery", find);
    }

    [Fact]
    public void ReaderFindIsARebindableReaderOnlyCommandWithPrimaryF()
    {
        var command = MeowChan.Desktop.Commands.CommandCatalog.All.Single(c => c.Id == "reader.find");
        Assert.Equal("Primary+F", command.Shortcut);
        Assert.Equal(MeowChan.Desktop.Commands.CommandScope.Reader, command.Scope);
        Assert.DoesNotContain("Primary+F", Source("Desktop/Views/Archive/ArchivePage.ThreadFind.cs"));
        Assert.Contains("CommandBindings.Matches(e, \"reader.find\")", Source("Desktop/Views/Archive/ArchivePage.Reader.cs"));
    }

    [Fact]
    public void EnterOnFocusedFindButtonsRunsTheLabeledAction()
    {
        string find = Source("Desktop/Views/Archive/ArchivePage.ThreadFind.cs");
        Assert.Contains("case \"ReaderFindPrevious\": MoveThreadFind(false)", find);
        // F02: the shared x sits outside the find group; it closes every open group natively and on Escape.
        Assert.Contains("CloseThreadTools()", find);
        Assert.DoesNotContain("case \"ReaderToolsClose\"", find);
        Assert.Contains("case \"ReaderFindRetry\"", find);
    }

    [Fact]
    public void LifecycleHooksInvalidateAndBuiltInsRegisterBothLanguages()
    {
        foreach (string file in new[] { "ArchivePage.axaml.cs", "ArchivePage.Reader.cs",
                     "ArchivePage.Gallery.cs", "ArchivePage.Selection.cs", "ArchivePage.Deletion.cs", "ArchivePage.Navigation.cs" })
            Assert.Contains("CloseThreadFind();", Source("Desktop/Views/Archive/" + file));
        Assert.Equal(ReaderFindCatalog.Entries.Length,
            ReaderFindCatalog.Entries.Select(e => e.Key).Distinct().Count());
        foreach (var entry in ReaderFindCatalog.Entries)
        {
            Assert.Equal(entry.English, BuiltInCatalog.English[entry.Key]);
            Assert.Equal(entry.German, BuiltInCatalog.German[entry.Key]);
        }
    }

    [Fact]
    public void EveryPageThreadAssignmentHasImmediateInvalidationIncludingHistoryAndQuotes()
    {
        foreach (string file in new[] { "ArchivePage.axaml.cs", "ArchivePage.Gallery.cs",
                     "ArchivePage.Selection.cs", "ArchivePage.Deletion.cs", "ArchivePage.Navigation.cs" })
        {
            string source = Source("Desktop/Views/Archive/" + file);
            var assignments = System.Text.RegularExpressions.Regex.Matches(source, @"_thread\s*=(?!=)");
            Assert.NotEmpty(assignments.Cast<System.Text.RegularExpressions.Match>());
            foreach (System.Text.RegularExpressions.Match assignment in assignments)
                Assert.EndsWith("CloseThreadFind();", source[..assignment.Index].TrimEnd());
        }
    }

    [Fact]
    public void ExactInertSnippetMapsFullBodyBeyondReaderDisplayLimit()
    {
        string body = new string('x', 65536) + "😀Needle & <literal>" + new string('y', 100);
        var view = new ArchivedThreadView(
            View().Summary with { PostCount = 1 },
            new[] { new CapturedPost("tail", 0, PostFieldPresence.All,
                Html: body.Replace("<literal>", "&lt;literal&gt;")) }, decimalPostOrder: false);
        var result = ReaderThreadFindIndex.Create(view, "needle");
        var hit = Assert.Single(result.Hits);
        var text = result.Texts[hit.TextIndex];
        Assert.True(hit.Offset > 65536);
        var snippet = ArchivePage.ThreadFindSnippet(text.Text, hit.Offset, hit.Length);
        Assert.Equal("Needle", snippet.Match);
        Assert.Equal(text.Text.Substring(hit.Offset, hit.Length), snippet.Match);
        Assert.StartsWith("…", snippet.Before);
        Assert.EndsWith("…", snippet.After);
        Assert.Contains("& <literal>", snippet.After);
        Assert.Contains("readerFind.beyondDisplay", Source("Desktop/Views/Archive/ArchivePage.ThreadFind.cs"));
    }

    [Fact]
    public void OverlappingHitsHaveTheirOwnExactSnippetMapping()
    {
        Assert.Equal(("a", "aa", "a"), ArchivePage.ThreadFindSnippet("aaaa", 1, 2));
        Assert.Equal(("", "aa", "aa"), ArchivePage.ThreadFindSnippet("aaaa", 0, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => ArchivePage.ThreadFindSnippet("abc", 3, 1));
    }

    [Theory]
    [InlineData(Key.Enter, KeyModifiers.None, 1)]
    [InlineData(Key.Enter, KeyModifiers.Shift, -1)]
    [InlineData(Key.Escape, KeyModifiers.None, 2)]
    [InlineData(Key.Enter, KeyModifiers.Control, 0)]
    [InlineData(Key.Enter, KeyModifiers.Shift | KeyModifiers.Control, 0)]
    [InlineData(Key.Enter, KeyModifiers.Alt, 0)]
    [InlineData(Key.Escape, KeyModifiers.Shift, 0)]
    [InlineData(Key.Space, KeyModifiers.None, 0)]
    [InlineData(Key.Tab, KeyModifiers.None, 0)]
    [InlineData(Key.F, KeyModifiers.Control, 0)]
    public void ExactLocalGesturesLeaveOtherNativeAndGlobalKeysAlone(Key key, KeyModifiers modifiers, int expected)
        => Assert.Equal(expected, ArchivePage.ThreadFindGesture(key, modifiers));

    [Fact]
    public void BarTunnelReplacesBubbleAndHandlesBeforeAnyNativeButtonClick()
    {
        string find = Source("Desktop/Views/Archive/ArchivePage.ThreadFind.cs");
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(find,
            @"AddHandler\(KeyDownEvent").Count);
        Assert.Contains("OnThreadFindKeyDown, RoutingStrategies.Tunnel)", find);
        Assert.DoesNotContain("KeyDown=\"OnThreadFindKeyDown\"", Source("Desktop/Views/Archive/ArchivePage.axaml"));
        Assert.Contains("InitializeComponent(); InitializeThreadFind();", Source("Desktop/Views/Archive/ArchivePage.axaml.cs"));
        string handler = find[find.IndexOf("private void OnThreadFindKeyDown", StringComparison.Ordinal)..];
        Assert.Contains("e.Handled || _ended || ShortcutService.IsCapturing || IsReaderDialogOpen", handler);
        Assert.Contains("!IsThreadFindSource(e.Source)", handler);
        Assert.True(handler.IndexOf("e.Handled = true;", StringComparison.Ordinal)
            < handler.IndexOf("else MoveThreadFind", StringComparison.Ordinal));
    }

    [Fact]
    public void ShellEscapeDismissesSourceScopedFindAfterPopupsAndModalsBeforeFullscreenOrReader()
    {
        string shell = Source("Desktop/Shell/MainWindow.Fullscreen.cs");
        int start = shell.IndexOf("private void OnShellKeyDown", StringComparison.Ordinal);
        string escape = shell[start..shell.IndexOf("if (HasBlockingPageDecision())", start, StringComparison.Ordinal)];
        int dismissal = escape.IndexOf("_archivePage?.TryDismissOverlay(e.Source)", StringComparison.Ordinal);
        Assert.True(dismissal > escape.IndexOf("DismissShellMenu()", StringComparison.Ordinal));
        Assert.True(dismissal > escape.IndexOf("DismissShellHelp()", StringComparison.Ordinal));
        Assert.True(dismissal > escape.IndexOf("_gallery.TryDismissOverlay()", StringComparison.Ordinal));
        Assert.True(dismissal < escape.IndexOf("LeaveFullscreen();", StringComparison.Ordinal));
        Assert.True(dismissal < escape.IndexOf("CloseLargeReader();", StringComparison.Ordinal));
        string reader = Source("Desktop/Views/Archive/ArchivePage.Reader.cs");
        int method = reader.IndexOf("internal bool TryDismissOverlay", StringComparison.Ordinal);
        string body = reader[method..reader.IndexOf("private void OnReaderFullscreenClick", method, StringComparison.Ordinal)];
        Assert.Contains("object? source = null", body);
        Assert.True(body.IndexOf("_urlTextExportBusy", StringComparison.Ordinal) < body.IndexOf("IsThreadFindSource(source)", StringComparison.Ordinal));
        Assert.True(body.IndexOf("_cleanupDialog.RequestClose()", StringComparison.Ordinal) < body.IndexOf("IsThreadFindSource(source)", StringComparison.Ordinal));
        Assert.True(body.IndexOf("OnExportClose", StringComparison.Ordinal) < body.IndexOf("IsThreadFindSource(source)", StringComparison.Ordinal));
        Assert.Contains("ReaderFindBar.IsVisible && IsThreadFindSource(source)", body);
        Assert.DoesNotContain(".Focus(", body);
    }

    [Fact]
    public void LeftOuterContextIncludesWholeEmojiWhenSixtyUnitBoundarySplitsPair()
    {
        string text = "😀" + new string('a', 59) + "X";
        var snippet = ArchivePage.ThreadFindSnippet(text, 61, 1);
        Assert.Equal(("😀" + new string('a', 59), "X", ""), snippet);
        Assert.Equal(61, snippet.Before.Length);
        // The expansion reaches the beginning: there is no omitted prefix/ellipsis.
        Assert.Equal(text.Substring(61, 1), snippet.Match);
    }

    [Fact]
    public void RightOuterContextIncludesWholeEmojiWhenSixtyUnitBoundarySplitsPair()
    {
        string text = "X" + new string('a', 59) + "😀" + "z";
        var snippet = ArchivePage.ThreadFindSnippet(text, 0, 1);
        Assert.Equal(("", "X", new string('a', 59) + "😀…"), snippet);
        Assert.Equal(62, snippet.After.Length); // 61 context units plus ellipsis; z omitted.
        Assert.Equal(text.Substring(0, 1), snippet.Match);
    }

    [Fact]
    public void BmpContextStaysAtSixtyUnitsAndCompleteEmojiMatchKeepsExactOffsetAndLength()
    {
        string bmp = new string('a', 61) + "X" + new string('b', 61);
        Assert.Equal(("…" + new string('a', 60), "X", new string('b', 60) + "…"),
            ArchivePage.ThreadFindSnippet(bmp, 61, 1));
        string emojiMatch = new string('a', 61) + "😀" + new string('b', 61);
        var snippet = ArchivePage.ThreadFindSnippet(emojiMatch, 61, 2);
        Assert.Equal(("…" + new string('a', 60), "😀", new string('b', 60) + "…"), snippet);
        Assert.Equal(emojiMatch.Substring(61, 2), snippet.Match);
    }

    [Fact]
    public void CompleteEmojiInsideOrOutsideContextDoesNotExpandUnsplitBoundary()
    {
        string inside = "X" + new string('a', 58) + "😀z";
        Assert.Equal(new string('a', 58) + "😀…", ArchivePage.ThreadFindSnippet(inside, 0, 1).After);
        string outside = "X" + new string('a', 60) + "😀z";
        Assert.Equal(new string('a', 60) + "…", ArchivePage.ThreadFindSnippet(outside, 0, 1).After);
        string left = "z😀" + new string('a', 59) + "X";
        Assert.Equal("…😀" + new string('a', 59), ArchivePage.ThreadFindSnippet(left, 62, 1).Before);
    }

    [Fact]
    public void InputBoundaryRejectsOverlengthWithoutTruncatingSupplementaryCharacter()
    {
        string overlength = new string('a', 511) + "😀";
        Assert.Equal(513, overlength.Length);
        Assert.Equal("readerFind.invalid", ArchivePage.ThreadFindQueryStatus(overlength));
        Assert.EndsWith("😀", overlength);
        string fullEmoji = string.Concat(Enumerable.Repeat("😀", 256));
        Assert.Equal(512, fullEmoji.Length);
        Assert.Equal("readerFind.searching", ArchivePage.ThreadFindQueryStatus(fullEmoji));
        Assert.Equal("readerFind.searching", ArchivePage.ThreadFindQueryStatus(new string('a', 510) + "😀"));
        Assert.Equal("readerFind.searching", ArchivePage.ThreadFindQueryStatus(new string('a', 512)));
        Assert.Equal("readerFind.invalid", ArchivePage.ThreadFindQueryStatus(new string('a', 513)));
    }

    [Theory]
    [InlineData("\0")]
    [InlineData("before\0after")]
    public void InputBoundaryRejectsNulRatherThanSearchingIt(string query)
        => Assert.Equal("readerFind.invalid", ArchivePage.ThreadFindQueryStatus(query));

    [Fact]
    public void InputBoundaryKeepsNoSearchAndExactLiteralSemantics()
    {
        Assert.Equal("readerFind.ready", ArchivePage.ThreadFindQueryStatus(""));
        Assert.Equal("readerFind.searching", ArchivePage.ThreadFindQueryStatus(" "));
        Assert.Equal("readerFind.searching", ArchivePage.ThreadFindQueryStatus(" e\u0301 "));
        Assert.Equal("readerFind.invalid", ArchivePage.ThreadFindQueryStatus(new string('a', 511) + "\0"));
        var entry = Assert.Single(ReaderFindCatalog.Entries.Where(e => e.Key == "readerFind.invalid"));
        Assert.Equal("Use at most 512 UTF-16 code units; NUL is not supported.", entry.English);
        Assert.Equal("Höchstens 512 UTF-16-Codeeinheiten verwenden; NUL wird nicht unterstützt.", entry.German);
    }

    [Fact]
    public void InputValidationInvalidatesOldResultsAndReturnsBeforeBackgroundIndexCreation()
    {
        string source = Source("Desktop/Views/Archive/ArchivePage.ThreadFind.cs");
        int start = source.IndexOf("private async Task SearchThreadFindAsync()", StringComparison.Ordinal);
        string search = source[start..source.IndexOf("private void MoveThreadFind", start, StringComparison.Ordinal)];
        int validation = search.IndexOf("_threadFindStatus = ThreadFindQueryStatus(query);", StringComparison.Ordinal);
        int admission = search.IndexOf("_threadFindStatus != \"readerFind.searching\"", StringComparison.Ordinal);
        Assert.True(validation > search.IndexOf("++_threadFindRequest", StringComparison.Ordinal));
        Assert.True(validation > search.IndexOf("_threadFindCancellation?.Cancel();", StringComparison.Ordinal));
        Assert.True(validation > search.IndexOf("_threadFindResult = null;", StringComparison.Ordinal));
        Assert.True(validation > search.IndexOf("_threadFindCursor = -1;", StringComparison.Ordinal));
        Assert.Contains("if (_ended || thread == null || _threadFindStatus != \"readerFind.searching\") return;", search);
        Assert.True(admission > validation);
        Assert.True(admission > search.IndexOf("LocalizeThreadFind();", validation, StringComparison.Ordinal));
        Assert.True(search.IndexOf("Task.Run", StringComparison.Ordinal) > admission);
        Assert.DoesNotContain(".Text =", search);
        Assert.DoesNotContain("query[..", search);
        Assert.DoesNotContain("query.Substring", search);
    }

    private static string Source(string relative, [CallerFilePath] string path = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../..", relative)));
}
