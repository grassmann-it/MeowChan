using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Platform;
using MeowChan.Desktop.Controls;

namespace MeowChan.DesktopTests;

/// <summary>
/// The guard of the dead-affordance audit (15.11.2026), run on every page and view that can be built without a window: nothing may look like
/// something to press (chip, link, icon square, tag pill, hand cursor) and do nothing. A control passes by being a button that acts, by sitting inside
/// an item that acts, by being a static <c>chip-badge</c>, or by carrying the explicit mark <c>read-only</c> (<see cref="WorkspaceChips.MarkReadOnly{T}"/>).
/// The pages are built empty, so what a page makes from its data later is covered by <see cref="DeadAffordanceGuard.Offenders"/> in the tests
/// that fill a page; windows (the dialogs) need a platform and are covered where they are built.
/// </summary>
[Collection("Gallery viewer construction")]
public sealed class DeadAffordanceGuardTests
{
    // The pages and views built without a window. RequireWiring is false for a view whose owner connects its buttons.
    private static IEnumerable<(string Name, Func<Control> Build, bool RequireWiring)> Pages()
    {
        yield return ("RulesPage", () => new MeowChan.RulesPage(), true);
        yield return ("PluginsPage", () => new MeowChan.PluginsPage(), true);
        yield return ("SettingsEditor", () => new MeowChan.SettingsEditor(), true);
        yield return ("StatisticsPage", () => new MeowChan.StatisticsPage(), true);
        yield return ("StatusPage", () => new MeowChan.StatusPage(), true);
        yield return ("DiagnosticsPage", () => new MeowChan.DiagnosticsPage(), true);
        yield return ("SourcesPage", () => new MeowChan.SourcesPage(), true);
        yield return ("ArchivePage", () => new MeowChan.ArchivePage(), true);
        yield return ("MediaPage", () => new MeowChan.MediaPage(_ => throw new InvalidOperationException("The guard must not open archive data.")), true);
        yield return ("ShortcutBindingsEditor", () => new MeowChan.Desktop.Views.Shortcuts.ShortcutBindingsEditor(), true);
        yield return ("GalleryViewer", () => new MeowChan.Desktop.GalleryViewer(), true);
        yield return ("ReaderMediaSidebar", () => new MeowChan.Desktop.ReaderMediaSidebar(), false);
        // Item views, built without data (their owner binds the data and wires the buttons).
        yield return ("ArchiveAttachmentView", () => new MeowChan.Desktop.ArchiveAttachmentView(), false);
        yield return ("MediaLibraryTileView", () => new MeowChan.Desktop.MediaLibraryTileView(), false);
        yield return ("ArchiveMediaTileView", () => new MeowChan.Desktop.ArchiveMediaTileView(), false);
    }

    public static IEnumerable<object[]> PageNames() => Pages().Select(page => new object[] { page.Name });

    /// <summary>The design tokens (WorkspaceStyles) as application resources, the way the pages find them in the product; no theme, no window.</summary>
    private static void EnsureTokens()
    {
        if (Application.Current != null) return;
        TokenApplication app = new();
        // Application.Current is the application the locator holds; the dispatcher scope of the test keeps it from leaking into other tests.
        AvaloniaLocator.CurrentMutable.Bind<Application>().ToConstant(app);
        app.Initialize();
    }

    private sealed class TokenApplication : Application
    {
        public override void Initialize() =>
            Styles.Add(new StyleInclude(new Uri("avares://MeowChan/")) { Source = new Uri("avares://MeowChan/Desktop/WorkspaceStyles.axaml") });
    }

    // Known offenders of pages that other work packages own, as "Page: part of the control's path". Empty while all of them are fixed or marked.
    private static readonly string[] Known = [];

    [Theory]
    [MemberData(nameof(PageNames))]
    public void NothingOnAPageLooksPressableAndDoesNothing(string name) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable scope = AvaloniaLocator.EnterScope();
        AvaloniaLocator.CurrentMutable.Bind<ICursorFactory>().ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
        EnsureTokens();
        var entry = Pages().First(page => page.Name == name);
        Control page = entry.Build();
        try
        {
            string[] offenders = DeadAffordanceGuard.Offenders(page, entry.RequireWiring)
                .Where(line => !Known.Any(known => line.StartsWith(known, StringComparison.Ordinal) || line.Contains(known, StringComparison.Ordinal))).ToArray();
            Assert.True(offenders.Length == 0,
                name + " has controls that look pressable and do nothing (make them act, or mark a decorative chip read-only):\n" + string.Join("\n", offenders));
        }
        finally { (page as IDisposable)?.Dispose(); }
    });

    // ---- the guard itself: what it accepts and what it reports ----

    private static void WithCursors(Action test) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable scope = AvaloniaLocator.EnterScope();
        AvaloniaLocator.CurrentMutable.Bind<ICursorFactory>().ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
        test();
    });

    private static IReadOnlyList<string> Guard(params Control[] children)
    {
        StackPanel root = new();
        foreach (Control child in children) root.Children.Add(child);
        return DeadAffordanceGuard.Offenders(root);
    }

    [Fact]
    public void AChipThatNothingHandlesIsReportedAndTheWaysToActOrToBeStaticAreAccepted() => WithCursors(() =>
    {
        Assert.Single(Guard(new Border { Classes = { "chip" } }));
        Assert.Single(Guard(new TextBlock { Classes = { "link" } }));
        Assert.Single(Guard(new Border { Classes = { "tag-pill" } }));
        // Acting: a pointer handler, a button that is enabled, a chip inside an item that acts.
        Border handled = new() { Classes = { "chip" } };
        handled.Tapped += (_, _) => { };
        Assert.Empty(Guard(handled));
        Assert.Empty(Guard(new Button { Classes = { "chip" } }));
        Assert.Empty(Guard(new ToggleButton { Classes = { "icon-square" } }));
        Border inside = new() { Classes = { "chip" } };
        Assert.Empty(Guard(new Button { Content = inside }));
        Assert.Empty(Guard(new ListBox { Items = { new ListBoxItem { Content = new Border { Classes = { "chip" } } } } }));
        // Static on purpose: the badge, and anything explicitly marked read-only.
        Assert.Empty(Guard(WorkspaceChips.CreateBadge("Fact", "v1")));
        Assert.Empty(Guard(WorkspaceChips.MarkReadOnly(new Border { Classes = { "chip" } })));
    });

    [Fact]
    public void ADisabledButtonIsAcceptedOnlyWhenSomethingIsWiredToIt() => WithCursors(() =>
    {
        Assert.Single(Guard(new Button { Classes = { "icon-square" }, IsEnabled = false }));
        Button clicked = new() { Classes = { "icon-square" }, IsEnabled = false };
        clicked.Click += (_, _) => { };
        Assert.Empty(Guard(clicked));
        Assert.Empty(Guard(new Button { Classes = { "link" }, IsEnabled = false, Command = new MeowChan.Desktop.Commands.DelegateCommand(() => { }) }));
        // A view built without its owner does not have to be wired yet.
        Assert.Empty(DeadAffordanceGuard.Offenders(new Button { Classes = { "icon-square" }, IsEnabled = false }, requireWiring: false));
    });

    [Fact]
    public void AHandCursorNeedsSomethingThatActsUnderIt() => WithCursors(() =>
    {
        Assert.Single(Guard(new Border { Cursor = new Cursor(StandardCursorType.Hand) }));
        Assert.Empty(Guard(new Border { Cursor = new Cursor(StandardCursorType.Arrow) }));
        Border pressed = new() { Cursor = new Cursor(StandardCursorType.Hand) };
        pressed.PointerPressed += (_, _) => { };
        // The frame acts, so the picture inside it with the same cursor is part of it.
        Border picture = new() { Cursor = new Cursor(StandardCursorType.Hand) };
        pressed.Child = picture;
        Assert.Empty(Guard(pressed));
    });

    [Fact]
    public void TheReadOnlyMarkIsPlainAndAClassNothingElseUses()
    {
        Assert.Equal("read-only", WorkspaceChips.ReadOnly);
        Border chip = WorkspaceChips.MarkReadOnly(WorkspaceChips.MarkOwn(new Border()));
        Assert.Contains("read-only", chip.Classes);
        Assert.Contains("chip", chip.Classes);
    }
}
