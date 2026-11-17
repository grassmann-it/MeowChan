using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;

namespace MeowChan.DesktopTests;

// Integration of 16.11. (per-context yield + explicit favourite keys): the archive, reader and media pages run their key handlers in their own execution
// context, as GalleryViewer.OnKeyDown does. Without it, CommandBindings.Matches(e, id) asks without a context, and a default that a saved binding took away
// only in another context (favorites.toggle after a saved gallery command on F) went dead on these pages too.
[Collection("Avalonia global state")]
public sealed class KeyContextHostTests
{
    private static KeyEventArgs Press(Key key) => new() { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = KeyModifiers.None };

    [Fact]
    public void EveryHandlerUnderTheRootSeesThePagesContextAndTheScopeEndsWithTheEvent()
    {
        StackPanel root = new();
        Border reader = new(), list = new();
        root.Children.Add(reader); root.Children.Add(list);
        KeyContextHost.Install(root, e => ReferenceEquals(e.Source, reader) ? "reader" : "archive", closeNow: true);
        List<string?> seen = new();
        reader.AddHandler(InputElement.KeyDownEvent, (_, _) => seen.Add(KeyContextHost.Current), RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        list.AddHandler(InputElement.KeyDownEvent, (_, e) => { seen.Add(KeyContextHost.Current); e.Handled = true; }, RoutingStrategies.Bubble);
        root.AddHandler(InputElement.KeyDownEvent, (_, _) => seen.Add(KeyContextHost.Current), RoutingStrategies.Bubble, handledEventsToo: true);

        // The root's own Bubble handler is registered after the close here (closeNow, a detached test tree), so it runs outside the scope; on a page the
        // close is registered on attach, after the page's own root handlers.
        reader.RaiseEvent(Press(Key.F));
        Assert.Equal(new string?[] { "reader", "reader", null }, seen);
        Assert.Null(KeyContextHost.Current);
        seen.Clear();
        list.RaiseEvent(Press(Key.F)); // a handled event still closes the scope
        Assert.Equal(new string?[] { "archive", null }, seen);
        Assert.Null(KeyContextHost.Current);
    }

    [Fact]
    public void ADisposedHostEntersNothingAndEndsTheScopeItStillHolds()
    {
        StackPanel root = new();
        Border pane = new(), child = new();
        root.Children.Add(pane);
        pane.Child = child;
        IDisposable host = KeyContextHost.Install(root, _ => "reader", closeNow: true);
        List<string?> seen = new();
        pane.AddHandler(InputElement.KeyDownEvent, (_, _) => seen.Add(KeyContextHost.Current), RoutingStrategies.Tunnel);
        // Disposed in the middle of the key (a handler that closes the reader): the context is restored, the late closing handler is a no-op.
        child.AddHandler(InputElement.KeyDownEvent, (_, _) => host.Dispose(), RoutingStrategies.Tunnel);
        child.RaiseEvent(Press(Key.F));
        Assert.Equal(new string?[] { "reader" }, seen);
        Assert.Null(KeyContextHost.Current);
        seen.Clear();
        child.RaiseEvent(Press(Key.F));
        Assert.Equal(new string?[] { null }, seen);
        host.Dispose(); // twice is fine
    }

    [Fact]
    public void TunnelHandlersOfOneElementRunInReverseOrderSoAHostOnThePaneItselfIsInstalledLast()
    {
        // Avalonia raises the Tunnel handlers of one element in the reverse order of their registration. A host on the large reader's pane must therefore be
        // installed AFTER the pane's own tunnel handlers (WireReaderSurface), or they would run before the context is entered.
        Border pane = new();
        List<string> order = new();
        pane.AddHandler(InputElement.KeyDownEvent, (_, _) => order.Add("first registered"), RoutingStrategies.Tunnel);
        pane.AddHandler(InputElement.KeyDownEvent, (_, _) => order.Add("second registered"), RoutingStrategies.Tunnel);
        pane.RaiseEvent(Press(Key.F));
        Assert.Equal(new[] { "second registered", "first registered" }, order);
    }

    [Fact]
    public void ASecondHostOfTheSameContextDoesNotEnterItTwice()
    {
        // The pane's own host and the page's host while a pane is inside the page: one scope for the key, restored to null after it.
        StackPanel page = new();
        Border pane = new();
        page.Children.Add(pane);
        KeyContextHost.Install(page, _ => "reader", closeNow: true);
        KeyContextHost.Install(pane, _ => "reader", closeNow: true);
        List<string?> seen = new();
        pane.AddHandler(InputElement.KeyDownEvent, (_, _) => seen.Add(KeyContextHost.Current), RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        pane.RaiseEvent(Press(Key.F));
        Assert.Equal(new string?[] { "reader", "reader" }, seen); // still "reader" at the pane's bubble: the page's scope is the one that holds it
        Assert.Null(KeyContextHost.Current);
    }

    [Fact]
    public void AHandlerThatThrowsDoesNotLeaveTheContextBehind() => BooruComponentDispatcher.Run(() =>
    {
        StackPanel root = new();
        Border pane = new();
        root.Children.Add(pane);
        KeyContextHost.Install(root, _ => "reader", closeNow: true);
        pane.AddHandler(InputElement.KeyDownEvent, (_, _) => throw new InvalidOperationException("handler"), RoutingStrategies.Bubble);
        Assert.Throws<InvalidOperationException>(() => pane.RaiseEvent(Press(Key.F)));
        Assert.Equal("reader", KeyContextHost.Current); // the route was aborted before the closing handler
        Avalonia.Threading.Dispatcher.UIThread.RunJobs(); // the posted safety net ends it
        Assert.Null(KeyContextHost.Current);
        // The next key is clean again.
        List<string?> seen = new();
        pane.AddHandler(InputElement.KeyDownEvent, (_, _) => seen.Add(KeyContextHost.Current), RoutingStrategies.Tunnel);
        Assert.Throws<InvalidOperationException>(() => pane.RaiseEvent(Press(Key.F)));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(new string?[] { "reader" }, seen);
        Assert.Null(KeyContextHost.Current);
    });

    [Fact]
    public void APartlyYieldedFavouriteKeyMatchesOnThePagesThatKeepIt()
    {
        // A saved gallery command on F takes favorites.toggle's F from the gallery only.
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(
            new ShortcutBindingsSection().Set("gallery.close", [ShortcutGesture.Parse("F")]), ShortcutPlatform.Linux);
        KeyEventArgs f = Press(Key.F);
        Assert.False(bindings.Matches(f, "favorites.toggle")); // no context named: a saved binding is never overridden
        foreach (string page in new[] { "archive", "reader", "media" })
            using (EffectiveShortcutBindings.EnterKeyContext(page))
                Assert.True(bindings.Matches(f, "favorites.toggle"), page);
        using (EffectiveShortcutBindings.EnterKeyContext("gallery"))
            Assert.False(bindings.Matches(f, "favorites.toggle"));
    }

    [Fact]
    public void TheArchiveAndTheMediaPageInstallTheirContexts()
    {
        string archive = Source("Desktop/Views/Archive/ArchivePage.axaml.cs"), media = Source("Desktop/Views/Media/MediaPage.cs");
        Assert.Contains("KeyContextHost.Install(this, e => e.Source is Visual source && ArchiveReaderPane is { } pane", archive);
        Assert.Contains("? \"reader\" : \"archive\");", archive);
        // The large reader's pane carries the same context itself, installed after its own tunnel handlers (see TunnelHandlersOfOneElementRun...).
        string reader = Source("Desktop/Views/Archive/ArchivePage.Reader.cs");
        int wire = reader.IndexOf("private void WireReaderSurface", StringComparison.Ordinal);
        int host = reader.IndexOf("KeyContextHost.Install(pane, _ => \"reader\")", wire, StringComparison.Ordinal);
        Assert.True(wire >= 0 && host > wire);
        Assert.True(host > reader.IndexOf("pane.AddHandler(KeyDownEvent, OnReaderKeyDown", wire, StringComparison.Ordinal));
        Assert.True(host > reader.IndexOf("pane.AddHandler(KeyDownEvent, OnArchiveMediaContextKey", wire, StringComparison.Ordinal));
        Assert.Contains("KeyContextHost.Install(this, _ => \"media\");", media);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheRealDetachedSidebarCarriesItsOwnContextBesideThePane(bool fromStrip) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable scope = Avalonia.AvaloniaLocator.EnterScope();
        Avalonia.AvaloniaLocator.CurrentMutable.Bind<Avalonia.Platform.ICursorFactory>()
            .ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
        TokenApplication.Bind();
        ArchivePage page = new();
        try
        {
            Control frame = page.DetachReader();
            ReaderMediaSidebar sidebar = page.MediaSidebar!;
            Grid pane = page.FindControl<Grid>("ArchiveReaderPane")!;
            Assert.Same(frame, sidebar.Parent);
            Assert.Same(frame, pane.Parent); // sibling, not a descendant: the pane's context cannot help it
            Control source = fromStrip ? sidebar.Strip : sidebar.Open;
            EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(
                new ShortcutBindingsSection().Set("gallery.close", [ShortcutGesture.Parse("F")]), ShortcutPlatform.Linux);
            string? context = null;
            bool favorite = false;
            source.AddHandler(InputElement.KeyDownEvent, (_, e) =>
            {
                context = KeyContextHost.Current;
                favorite = bindings.Matches(e, "favorites.toggle");
            }, RoutingStrategies.Tunnel);
            KeyEventArgs press = Press(Key.F);
            source.RaiseEvent(press);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs(); // detached tree: the safety-net closes its scope
            Assert.Equal("reader", context);
            Assert.True(favorite, "A gallery-only rebind must not steal F from the detached reader sidebar.");
            Assert.Equal("reader", KeyContextHost.ContextOf(press));
            Assert.Null(KeyContextHost.Current);
            page.AttachReader(frame);
        }
        finally { page.EndLifetime(); }
    });

    // S3 P2-5: the window's own routes run after every page scope has closed; they ask for the context the innermost host resolved for the key.
    [Fact]
    public void TheContextTheInnermostHostResolvedStaysReadableForTheWindowAfterTheScopeClosed()
    {
        StackPanel page = new();
        Border pane = new(), list = new(), child = new();
        page.Children.Add(pane); page.Children.Add(list);
        pane.Child = child;
        KeyContextHost.Install(page, _ => "sources", closeNow: true);
        KeyContextHost.Install(pane, _ => "plugins", closeNow: true);

        KeyEventArgs inPane = new() { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F10, KeyModifiers = KeyModifiers.Shift };
        child.RaiseEvent(inPane);
        Assert.Null(KeyContextHost.Current);
        Assert.Equal("plugins", KeyContextHost.ContextOf(inPane)); // the nearer host wins
        KeyEventArgs inList = Press(Key.F10);
        list.RaiseEvent(inList);
        Assert.Equal("sources", KeyContextHost.ContextOf(inList));
        Assert.Null(KeyContextHost.ContextOf(Press(Key.F10))); // a key no host saw (header, footer, the window)
    }

    // S3 P2-4: the pages without a host asked without a context, so a saved binding of another page's command on the key of "Open context menu"
    // (Shift+F10 / Menu) made it dead on their lists while their handlers still swallowed Shift+F10. Every main page root now installs its host.
    [Theory]
    [InlineData("Desktop/Views/Sources/SourcesPage.axaml.cs", "sources")]
    [InlineData("Desktop/Views/Plugins/PluginsPage.axaml.cs", "plugins")]
    [InlineData("Desktop/Views/Diagnostics/DiagnosticsPage.axaml.cs", "diagnostics")]
    [InlineData("Desktop/Views/Statistics/StatisticsPage.axaml.cs", "statistics")]
    [InlineData("Desktop/Views/Rules/RulesPage.axaml.cs", "rules")]
    [InlineData("Desktop/Views/Booru/BooruWorkbenchView.cs", "booru")]
    [InlineData("Desktop/Views/Tagging/TaggingPage.cs", "tagging")]
    [InlineData("Desktop/Views/Media/MediaPage.cs", "media")]
    public void EveryMainPageRootInstallsItsHost(string file, string context)
    {
        Assert.Contains($"KeyContextHost.Install(this, _ => \"{context}\");", Source(file));
        // The name is one the commands declare (ExecutionContexts): a yield is recorded under that name.
        Assert.Contains(CommandCatalog.All, command => command.ExecutionContexts.Contains(context));
    }

    [Theory]
    [InlineData("sources")]
    [InlineData("plugins")]
    [InlineData("diagnostics")]
    [InlineData("statistics")]
    [InlineData("rules")]
    [InlineData("booru")]
    public void OpenContextMenuStaysOnAPageAfterASavedArchiveCommandTookItsKeyInTheArchive(string page)
    {
        Assert.Contains(page, EffectiveShortcutBindings.ExecutionContexts("menu.context-open"));
        foreach (string key in new[] { "Shift+F10", "Apps" })
        {
            ShortcutGesture gesture = ShortcutGesture.Parse(key);
            EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(
                new ShortcutBindingsSection().Set("archive.refresh", [gesture]), ShortcutPlatform.Linux);
            KeyGesture resolved = gesture.Resolve(ShortcutPlatform.Linux);
            KeyEventArgs e = new() { RoutedEvent = InputElement.KeyDownEvent, Key = resolved.Key, KeyModifiers = resolved.KeyModifiers };
            Assert.False(bindings.Matches(e, "menu.context-open"), key); // no context named: a saved binding is never overridden
            using (EffectiveShortcutBindings.EnterKeyContext("archive"))
                Assert.False(bindings.Matches(e, "menu.context-open"), key);
            using (EffectiveShortcutBindings.EnterKeyContext(page))
                Assert.True(bindings.Matches(e, "menu.context-open"), page + " " + key);
        }
    }

    private static string Source(string relative, [CallerFilePath] string sourcePath = "") =>
        BooruViewSources.ReadAll(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative)));
}
