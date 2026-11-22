using System.Reflection;
using Avalonia.Threading;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopTests;

[Collection("Booru exact navigation")]
public sealed class BooruComponentDispatcherTests
{
    [Fact]
    public void WaitPumpsOnlyTheOwnedDispatcherForAsyncComponentWork() => BooruComponentDispatcher.Run(() =>
    {
        TaskCompletionSource<int> completion = new();
        Dispatcher.UIThread.Post(() => completion.SetResult(42));
        Assert.Equal(42, BooruComponentDispatcher.Wait(completion.Task));
    });

    [Fact]
    public void WorkbenchModelNotificationsRenderOnTheViewsDispatcher() => BooruComponentDispatcher.Run(() =>
    {
        BooruUiHarness harness = new();
        using BooruWorkbenchModel model = new(harness.Services);
        using BooruWorkbenchView view = new(model, ownsModel: false);
        Dispatcher owner = Dispatcher.UIThread;
        int renders = 0;
        view.ChromeChanged += () =>
        {
            owner.VerifyAccess();
            renders++;
        };

        BooruComponentDispatcher.Run(() =>
        {
            Assert.NotSame(owner, Dispatcher.UIThread);
            model.Search.SetMode(BooruInputMode.Post);
        });
        Assert.Equal(0, renders);
        owner.RunJobs();
        Assert.Equal(1, renders);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScopeRestoresPreviousObserversLanguageAndDispatcherEvenWhenTheTestThrows(bool fail)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        FieldInfo primary = typeof(Dispatcher).GetField("s_uiThread", flags)!;
        FieldInfo handlers = typeof(Texts).GetField("Changed", flags)!;
        FieldInfo counter = typeof(ShortcutService).GetField("_observersUntilPrune", flags)!;
        var refreshers = (List<WeakReference<Action>>)typeof(ShortcutService).GetField("Refreshers", flags)!.GetValue(null)!;
        object? dispatcherBefore = primary.GetValue(null), handlersBefore = handlers.GetValue(null);
        object? counterBefore = counter.GetValue(null);
        WeakReference<Action>[] refreshersBefore = refreshers.ToArray();
        string languageBefore = Texts.Language;
        var shortcutsBefore = ShortcutService.Current;
        var shortcutSectionBefore = ShortcutService.CurrentSection;
        long shortcutRevisionBefore = ShortcutService.Revision;
        bool queuedCallbackRan = false;
        int ownShortcutRefreshes = 0;
        int ownLanguageChanges = 0;
        Action shortcut = () => ownShortcutRefreshes++;
        EventHandler changed = (_, _) => ownLanguageChanges++;

        void Probe() => BooruComponentDispatcher.Run(() =>
        {
            Assert.Empty(refreshers);
            ShortcutService.Observe(shortcut);
            ShortcutService.Publish(new());
            Assert.Equal(1, ownShortcutRefreshes);
            Assert.Equal(shortcutRevisionBefore + 1, ShortcutService.Revision);
            Texts.Changed += changed;
            Texts.SetLanguage("de");
            Assert.Equal(1, ownLanguageChanges);
            Dispatcher.UIThread.Post(() => queuedCallbackRan = true);
            if (fail) throw new InvalidOperationException("synthetic fixture failure");
        });

        if (fail) Assert.Throws<InvalidOperationException>(Probe);
        else Probe();

        Assert.True(queuedCallbackRan);
        Assert.Same(dispatcherBefore, primary.GetValue(null));
        Assert.Same(handlersBefore, handlers.GetValue(null));
        Assert.Equal(counterBefore, counter.GetValue(null));
        Assert.Equal(refreshersBefore, refreshers);
        Assert.Same(shortcutsBefore, ShortcutService.Current);
        Assert.Same(shortcutSectionBefore, ShortcutService.CurrentSection);
        Assert.Equal(shortcutRevisionBefore, ShortcutService.Revision);
        Assert.Equal(languageBefore, Texts.Language);
        Assert.Equal(1, ownLanguageChanges); // Restore must not invoke ended test subscribers.
        GC.KeepAlive(shortcut);
        GC.KeepAlive(changed);
    }
}
