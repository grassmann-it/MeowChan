using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;

namespace MeowChan.DesktopTests;

// Menu audit (16 Nov 2026): the keyboard route of the Diagnostics tables ("Open context menu") called ContextMenu.Open, which in Avalonia 12 skips the
// menu's Opening event, the only place the entries were composed. The menu then showed nothing or the entries of an earlier row. It is composed first now.
[Collection("Booru exact navigation")]
public sealed class DiagnosticsKeyboardMenuTests
{
    [Fact]
    public void TheContextOpenKeyOnASelectedRowComposesTheMenuBeforeOpeningIt() => Run(page =>
    {
        DataGrid grid = page.FindControl<DataGrid>("PlatformGrid")!;
        Assert.NotEmpty(grid.ItemsSource!.Cast<object>());
        grid.SelectedItem = grid.ItemsSource.Cast<object>().First();
        Assert.IsType<DiagnosticEntry>(grid.SelectedItem);
        Assert.Equal(0, grid.ContextMenu!.ItemCount);

        grid.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Apps, Source = grid });
        for (int attempt = 0; attempt < 20; attempt++) Dispatcher.UIThread.RunJobs();

        Assert.Contains("diagnostics.copy", grid.ContextMenu.Items.OfType<MenuItem>().Select(item => (string?)item.Tag));
    });

    private static void Run(Action<DiagnosticsPage> test) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable scope = AvaloniaLocator.EnterScope();
        AvaloniaLocator.CurrentMutable.Bind<Avalonia.Platform.ICursorFactory>().ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
        TokenApplication app = new();
        AvaloniaLocator.CurrentMutable.Bind<Application>().ToConstant(app);
        app.Initialize();
        Texts.Initialize(); Texts.SetLanguage("en");
        Root root = new();
        DiagnosticsPage page = new(_ => true, _ => Task.CompletedTask);
        root.Child = page;
        page.Refresh(new DesktopSessionView([], [], [], DesktopSessionState.Ready, null), available: true);
        try { test(page); }
        finally { root.Child = null; }
    });

    private sealed class TokenApplication : Application
    {
        public override void Initialize() =>
            Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://MeowChan/")) { Source = new Uri("avares://MeowChan/Desktop/WorkspaceStyles.axaml") });
    }

    private sealed class Root : Decorator, Avalonia.LogicalTree.ILogicalRoot;
}
