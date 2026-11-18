using System.Reflection;
using Avalonia.Threading;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

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
        bool queuedCallbackRan = false;
        int ownLanguageChanges = 0;
        Action shortcut = () => { };
        EventHandler changed = (_, _) => ownLanguageChanges++;

        void Probe() => BooruComponentDispatcher.Run(() =>
        {
            Assert.Empty(refreshers);
            ShortcutService.Observe(shortcut);
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
        Assert.Equal(languageBefore, Texts.Language);
        Assert.Equal(1, ownLanguageChanges); // Restore must not invoke ended test subscribers.
        GC.KeepAlive(shortcut);
        GC.KeepAlive(changed);
    }
}
