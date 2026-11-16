using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Downloader;
using MeowChan.Http;
using MeowChan.Interfaces;
using MeowChan.PluginSdk;
using Ursa.Controls;

namespace MeowChan.DesktopTests;

// P2-3 audit (goal delta 16 Nov 2026): the Sources page has the same shape as the media grid, a plain bubble KeyDown handler on a list whose own items may
// consume Enter first (Avalonia 12 ListBoxItem). The key is raised on the focused item, the route the keyboard takes: Enter on a watch row opens that watch
// (the button's action), Enter on a source card opens its details.
[Collection("Booru exact navigation")]
public sealed class SourcesItemEnterTests
{
    private const BindingFlags InstanceNonPublic = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void EnterOnTheFocusedWatchRowOpensThatWatch() => Run(fixture =>
    {
        SelectingItemsControl watches = fixture.Page.FindControl<SelectingItemsControl>("SourceWatchesList")!;
        Assert.Equal(1, watches.ItemCount);
        watches.SelectedIndex = 0;

        fixture.Press(Container(watches, 0), Key.Enter);

        Assert.Equal(new DiagnosticTarget(DiagnosticTargetKind.Thread, 42), Assert.Single(fixture.OpenedWatches));
    });

    [Fact]
    public void EnterRaisedOnTheWatchListWithTheRowAsSourceStillOpensItBecauseTheListHandlerStays() => Run(fixture =>
    {
        SelectingItemsControl watches = fixture.Page.FindControl<SelectingItemsControl>("SourceWatchesList")!;
        watches.SelectedIndex = 0;

        watches.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = Container(watches, 0) });

        Assert.Single(fixture.OpenedWatches);
    });

    [Fact]
    public void EnterOnTheFocusedSourceCardOpensItsDetails() => Run(fixture =>
    {
        SelectionList cards = fixture.Page.FindControl<SelectionList>("SourceCards")!;
        Assert.Equal(2, cards.Items.Count);
        FieldInfo showDetails = typeof(SourcesPage).GetField("_showDetails", InstanceNonPublic)!;
        cards.SelectedIndex = 0;
        showDetails.SetValue(fixture.Page, false);

        fixture.Press(Assert.IsAssignableFrom<Control>(cards.Items[0]), Key.Enter);

        Assert.True((bool)showDetails.GetValue(fixture.Page)!, "Enter on the focused source card must open the details of the source.");
    });

    private static Control Container(SelectingItemsControl list, int index) => list.ContainerFromIndex(index)
        ?? throw new InvalidOperationException("The row " + index + " has no container.");

    private static void Run(Action<Fixture> test) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable scope = AvaloniaLocator.EnterScope();
        AvaloniaLocator.CurrentMutable.Bind<Avalonia.Platform.ICursorFactory>().ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
        TokenApplication app = new();
        AvaloniaLocator.CurrentMutable.Bind<Application>().ToConstant(app);
        app.Initialize();
        using Fixture fixture = new();
        test(fixture);
    });

    private sealed class TokenApplication : Application
    {
        public override void Initialize() =>
            Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://MeowChan/")) { Source = new Uri("avares://MeowChan/Desktop/WorkspaceStyles.axaml") });
    }

    private sealed class Fixture : IDisposable
    {
        private readonly Root _root = new();

        internal SourcesPage Page { get; }
        internal List<DiagnosticTarget> OpenedWatches { get; } = new();

        internal Fixture()
        {
            Texts.Initialize(); Texts.SetLanguage("en");
            Page = new SourcesPage(target => { OpenedWatches.Add(target); return true; }, _ => { }, _ => { }, () => { }, () => { });
            // No theme and no layout pass: the two lists get the bare presenter of the product's theme, and their containers are the sources of the key.
            foreach (string name in new[] { "SourceWatchesList", "SourceCards" })
            {
                SelectingItemsControl list = Page.FindControl<SelectingItemsControl>(name)!;
                list.ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel());
                list.Template = new FuncControlTemplate<SelectingItemsControl>((_, scope) =>
                {
                    ItemsPresenter presenter = new() { Name = "PART_ItemsPresenter", ItemsPanel = list.ItemsPanel };
                    presenter.RegisterInNameScope(scope);
                    return presenter;
                });
            }
            _root.Child = Page;
            DesktopThreadEntry thread = new(42, new ThreadSnapshot(new ResourceId("4chan", "g", "123"),
                "https://boards.4chan.org/g/thread/123", "Thread", "/media/123", ThreadActivity.Checking, ThreadSourceState.Active,
                new RequestPolicyProgress(RequestPolicyPhase.Sending, TimeSpan.Zero, 0), 3, 0,
                new Dictionary<string, FailureInfo>(), null, new DownloadCounts(0, 0)), "4chan", "g");
            DesktopSessionView view = new([thread], [], [], DesktopSessionState.Ready, null);
            Page.Refresh([Provider("4chan"), Provider("8kun")], view, available: true);
            foreach (SelectingItemsControl list in new[] { Page.FindControl<SelectingItemsControl>("SourceWatchesList")!, Page.FindControl<SelectingItemsControl>("SourceCards")! })
            {
                list.ApplyTemplate();
                foreach (ItemsPresenter presenter in list.GetVisualDescendants().OfType<ItemsPresenter>()) presenter.ApplyTemplate();
            }
        }

        internal void Press(Control source, Key key)
        {
            source.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, Source = source });
            for (int attempt = 0; attempt < 50; attempt++) Dispatcher.UIThread.RunJobs();
        }

        public void Dispose()
        {
            _root.Child = null;
            Page.EndLifetime();
        }

        private static ProviderDescriptor Provider(string id) => new(id, id,
            new Uri("https://" + (id == "4chan" ? "boards.4chan.org" : "8kun.top")),
            ProviderCapabilities.ThreadWatch | ProviderCapabilities.BoardCatalog, Array.Empty<string>(), null, false);

        private sealed class Root : Decorator, ILogicalRoot;
    }
}
