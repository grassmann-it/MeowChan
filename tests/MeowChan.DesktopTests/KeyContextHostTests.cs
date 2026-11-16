using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;

namespace MeowChan.DesktopTests;

// Integration of 16.11. (per-context yield + explicit favourite keys): the archive, reader and media pages run their key handlers in their own execution
// context, as GalleryViewer.OnKeyDown does. Without it, CommandBindings.Matches(e, id) asks without a context, and a default that a saved binding took away
// only in another context (favorites.toggle after a saved gallery command on F) went dead on these pages too.
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
        Assert.Contains("KeyContextHost.Install(this, _ => \"media\");", media);
    }

    private static string Source(string relative, [CallerFilePath] string sourcePath = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative)));
}
