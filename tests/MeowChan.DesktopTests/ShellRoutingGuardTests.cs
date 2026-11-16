using System.Text.RegularExpressions;
using Avalonia.Input;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;

namespace MeowChan.DesktopTests;

// Bug-class guards of the 16.11. audit, classes 4 and 5 (and the F1 list of class 3): the shell's routing must not read the state of a suspended gallery, and a
// right-click that opens a menu with its press must be paired with the request of its release, never with a tick posted by the press.
public sealed class ShellRoutingGuardTests
{
    private static string Root()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory != null && !File.Exists(Path.Combine(directory, "MeowChan.sln"))) directory = Path.GetDirectoryName(directory);
        return directory ?? throw new DirectoryNotFoundException("The repository root was not found.");
    }

    private static IEnumerable<string> DesktopFiles(string folder = "Desktop") =>
        Directory.EnumerateFiles(Path.Combine(Root(), folder), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                && !file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar));

    // ---- class 4: overlay flags and the suspended gallery -------------------------------------------------------------------------------

    [Theory]
    [InlineData(true, false, true)]   // the active gallery's overlay keeps the workspace shortcuts away
    [InlineData(true, true, false)]   // a suspended gallery (the post panel stays open behind the large reader) owns nothing
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    public void AnOverlayOwnsTheShortcutRoutingOnlyWhileTheGalleryIsTheActiveSurface(bool overlayOpen, bool suspended, bool owns) =>
        Assert.Equal(owns, MainWindow.GalleryOwnsShortcutRouting(overlayOpen, suspended));

    [Theory]
    [InlineData(true, false, true)]   // help or the favourite question owns the keys
    [InlineData(true, true, false)]   // ... not when the gallery sits behind the large reader
    [InlineData(false, false, false)] // the side panel alone never blocks the shell's power commands
    [InlineData(false, true, false)]
    public void TheShellPowerCommandsAreBlockedOnlyByTheActiveGalleriesKeyOwningOverlay(bool blocks, bool suspended, bool blocked) =>
        Assert.Equal(blocked, MainWindow.GalleryBlocksShellPowerCommands(blocks, suspended));

    [Fact]
    public void TheShellNeverReadsAGalleryOverlayFlagWithoutTheSuspendedState()
    {
        // The gallery keeps its overlay state while it is suspended behind the large reader; every read of it in the shell must name the suspended state
        // on the same or the previous line (or go through the two predicates, which take it as an argument).
        var flag = new Regex(@"\b(IsOverlayOpen|BlocksUnfocusedHostKeys)\b");
        var offenders = new List<string>();
        foreach (string file in DesktopFiles("Desktop/Shell"))
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.TrimStart().StartsWith("//") || line.TrimStart().StartsWith("///") || !flag.IsMatch(line)) continue;
                string window = (i > 0 ? lines[i - 1] : "") + line + (i + 1 < lines.Length ? lines[i + 1] : "");
                if (!window.Contains("uspended", StringComparison.Ordinal)) offenders.Add(Path.GetFileName(file) + ":" + (i + 1) + " " + line.Trim());
            }
        }
        Assert.Empty(offenders);
    }

    [Fact]
    public void TheWorkspaceRouteAndTheShellPowerGateUseTheSuspendedAwarePredicates()
    {
        string shortcuts = File.ReadAllText(Path.Combine(Root(), "Desktop/Shell/MainWindow.Shortcuts.cs"));
        Assert.Contains("GalleryOwnsShortcutRouting(openGallery.IsOverlayOpen, _gallerySuspended)", shortcuts, StringComparison.Ordinal);
        string fullscreen = File.ReadAllText(Path.Combine(Root(), "Desktop/Shell/MainWindow.Fullscreen.cs"));
        Assert.Contains("GalleryBlocksShellPowerCommands(blocking.BlocksUnfocusedHostKeys, _gallerySuspended)", fullscreen, StringComparison.Ordinal);
    }

    // ---- class 3 (F1 list): a hidden button does not take its command out of the help ---------------------------------------------------

    [Fact]
    public void TheReaderHelpListDoesNotAskTheVisibilityOfAButton()
    {
        string fullscreen = File.ReadAllText(Path.Combine(Root(), "Desktop/Shell/MainWindow.Fullscreen.cs"));
        int start = fullscreen.IndexOf("private MeowChan.Desktop.Views.Shortcuts.ShortcutHelpContext ShellHelpContext()", StringComparison.Ordinal);
        int end = fullscreen.IndexOf("else if (_selectedPage == WorkspacePage.Downloads", start, StringComparison.Ordinal);
        Assert.True(start > 0 && end > start);
        string reader = fullscreen[start..end];
        Assert.DoesNotContain("IsEffectivelyVisible", reader, StringComparison.Ordinal);
        Assert.DoesNotContain(".IsVisible", reader, StringComparison.Ordinal);
        // The three back commands are listed by the answer the Back gesture itself gets.
        Assert.Contains("foreach (string id in BackCommands) if (CanRunBack(id))", reader, StringComparison.Ordinal);
    }

    // ---- class 5: a press that opens a menu and the request of its release ------------------------------------------------------------

    private sealed class Deferred
    {
        internal readonly List<Action> Queue = new();
        internal void Defer(Action end) => Queue.Add(end);
        internal void RunAll() { foreach (Action end in Queue.ToArray()) end(); Queue.Clear(); }
    }

    [Fact]
    public void TheRequestThatComesWithTheReleaseIsConsumedOnceAndTheGateEndsAfterIt()
    {
        var later = new Deferred();
        var gate = new PressOpenedContextGate(later.Defer);
        gate.BeginPress();
        gate.MenuOpenedByPress();
        Assert.True(gate.IsPending);          // nothing a tick of the press' own handler may end: the release is still to come
        gate.Released(MouseButton.Right);     // the release; Avalonia raises ContextRequested after its handlers
        Assert.True(gate.IsPending);          // the end is deferred past that request
        Assert.True(gate.TryConsume());       // the paired request: no second menu
        Assert.False(gate.TryConsume());      // a later request (the Menu key) is routed normally
        later.RunAll();
        Assert.False(gate.IsPending);
    }

    [Fact]
    public void TheGateEndsAfterTheReleaseWhenNoRequestFollowed()
    {
        var later = new Deferred();
        var gate = new PressOpenedContextGate(later.Defer);
        gate.BeginPress();
        gate.MenuOpenedByPress();
        gate.Released(MouseButton.Right);
        Assert.Single(later.Queue);
        later.RunAll();
        Assert.False(gate.IsPending);
        Assert.False(gate.TryConsume());
    }

    [Fact]
    public void OnlyTheRightButtonsReleaseEndsTheGate()
    {
        var later = new Deferred();
        var gate = new PressOpenedContextGate(later.Defer);
        gate.MenuOpenedByPress();
        gate.Released(MouseButton.Left);
        gate.Released(MouseButton.Middle);
        Assert.Empty(later.Queue);
        Assert.True(gate.IsPending);
    }

    [Fact]
    public void AKeyOrTheNextPressEndsAGestureWhoseReleaseTheMenuPopupTook()
    {
        var gate = new PressOpenedContextGate(new Deferred().Defer);
        gate.MenuOpenedByPress();            // the popup took the release: Released never ran
        gate.Reset();                        // the next key (Menu, Shift+F10)
        Assert.False(gate.TryConsume());
        gate.MenuOpenedByPress();
        gate.BeginPress();                   // or the next press
        Assert.False(gate.TryConsume());
    }

    [Fact]
    public void ATickPostedByAnEarlierReleaseNeverEndsALaterGesture()
    {
        var later = new Deferred();
        var gate = new PressOpenedContextGate(later.Defer);
        gate.MenuOpenedByPress();
        gate.Released(MouseButton.Right);
        gate.BeginPress();                   // the next click starts before the deferred end ran
        gate.MenuOpenedByPress();
        later.RunAll();
        Assert.True(gate.IsPending);         // the new gesture keeps its gate
    }

    [Fact]
    public void NoPointerPressedHandlerEndsAPendingContextGateOnATickItPosts()
    {
        // The press arms a gate that lives until the release. A "...Pending = false" posted from a PointerPressed handler ends it long before Avalonia 12 raises
        // the ContextRequested of the release (the media grid's second menu).
        var offenders = new List<string>();
        var handler = new Regex(@"void\s+(On\w*PointerPressed\w*)\s*\(");
        foreach (string file in DesktopFiles())
        {
            string text = File.ReadAllText(file);
            foreach (Match match in handler.Matches(text))
            {
                int open = text.IndexOf('{', match.Index);
                if (open < 0) continue;
                int depth = 0, close = open;
                for (; close < text.Length; close++)
                {
                    if (text[close] == '{') depth++;
                    else if (text[close] == '}' && --depth == 0) break;
                }
                string body = text[open..Math.Min(close + 1, text.Length)];
                if (Regex.IsMatch(body, @"Post\([^;]*Context\w*\s*=\s*false") || Regex.IsMatch(body, @"Post\([^;]*Pending\s*=\s*false"))
                    offenders.Add(Path.GetFileName(file) + ": " + match.Groups[1].Value);
            }
        }
        Assert.Empty(offenders);
    }

    [Fact]
    public void TheMediaGridAndPanelPairTheirPressWithTheReleaseThroughOneGate()
    {
        string page = File.ReadAllText(Path.Combine(Root(), "Desktop/Views/Media/MediaPage.cs"));
        Assert.Contains("PressOpenedContextGate _sharedPointerContext", page, StringComparison.Ordinal);
        Assert.Contains("_grid.AddHandler(PointerReleasedEvent, OnSharedPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);", page, StringComparison.Ordinal);
        int press = page.IndexOf("private void OnSharedPointerPressed", StringComparison.Ordinal);
        int released = page.IndexOf("private void OnSharedPointerReleased", StringComparison.Ordinal);
        Assert.True(press > 0 && released > press);
        string pressBody = page[press..released];
        Assert.DoesNotContain("Post(", pressBody, StringComparison.Ordinal);
        Assert.Contains("_sharedPointerContext.BeginPress();", pressBody, StringComparison.Ordinal);
        Assert.Contains("_sharedPointerContext.MenuOpenedByPress();", pressBody, StringComparison.Ordinal);
        Assert.Contains("_sharedPointerContext.TryConsume()", page, StringComparison.Ordinal);
        // A key ends a gesture whose release the menu popup took.
        int keyDown = page.IndexOf("private void OnSharedContextKeyDown", StringComparison.Ordinal);
        Assert.Contains("_sharedPointerContext.Reset();", page[keyDown..(keyDown + 400)], StringComparison.Ordinal);
        string panel = File.ReadAllText(Path.Combine(Root(), "Desktop/Views/Media/MediaPage.Panel.cs"));
        Assert.Contains("PressOpenedContextGate _panelPointerContext", panel, StringComparison.Ordinal);
        Assert.Contains("_panelPointerContext.TryConsume()", panel, StringComparison.Ordinal);
    }
}
