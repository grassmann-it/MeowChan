using System.Reflection;
using System.Runtime.ExceptionServices;
using Avalonia;
using Avalonia.Platform;
using Avalonia.Skia;
using Avalonia.Threading;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

// Used by nonparallel collections. Constructed controls must neither refresh another
// test's thread-owned controls nor outlive their dispatcher.
internal static class BooruComponentDispatcher
{
    private const BindingFlags StaticPrivate = BindingFlags.Static | BindingFlags.NonPublic;

    internal static void Run(Action test)
    {
        ExceptionDispatchInfo? failure = null;
        Thread ownerThread = new(() =>
        {
            try { RunOnOwnedDispatcher(test); }
            catch (Exception exception) { failure = ExceptionDispatchInfo.Capture(exception); }
        }) { IsBackground = true, Name = "Booru component dispatcher" };
        ownerThread.Start();
        if (!ownerThread.Join(TimeSpan.FromSeconds(30)))
            throw new TimeoutException("The Booru component dispatcher did not settle.");
        failure?.Throw();
    }

    // Async component work must be waited for on the owner thread. A synchronous Result/Wait
    // would block the dispatcher that the continuation needs, which is especially visible on
    // slower ARM runners. Pump only this owned dispatcher; the outer Run join still owns the
    // lifetime and bounds a genuinely stuck component.
    internal static T Wait<T>(Task<T> task)
    {
        PumpUntilCompleted(task);
        return task.GetAwaiter().GetResult();
    }

    internal static void Wait(Task task)
    {
        PumpUntilCompleted(task);
        task.GetAwaiter().GetResult();
    }

    private static void PumpUntilCompleted(Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        Dispatcher owner = Dispatcher.UIThread;
        owner.VerifyAccess();
        while (!task.IsCompleted)
        {
            owner.RunJobs();
            Thread.Yield();
        }
    }

    private static void RunOnOwnedDispatcher(Action test)
    {
        FieldInfo primary = Field(typeof(Dispatcher), "s_uiThread");
        FieldInfo languageHandlers = Field(typeof(Texts), "Changed");
        FieldInfo shortcutHandlers = Field(typeof(ShortcutService), "Changed");
        FieldInfo pruneCounter = Field(typeof(ShortcutService), "_observersUntilPrune");
        var refreshers = (List<WeakReference<Action>>)Field(typeof(ShortcutService), "Refreshers").GetValue(null)!;
        object? previousDispatcher = primary.GetValue(null);
        object? previousHandlers = languageHandlers.GetValue(null);
        object? previousShortcutHandlers = shortcutHandlers.GetValue(null);
        object? previousPruneCounter = pruneCounter.GetValue(null);
        WeakReference<Action>[] previousRefreshers = refreshers.ToArray();
        string language = Texts.Language;
        Dispatcher owner = Dispatcher.CurrentDispatcher;
        using IDisposable scope = AvaloniaLocator.EnterScope();
        try
        {
            // A fresh dispatcher is intentional: another test may have left a language-change
            // callback queued on the xunit worker's dispatcher. Running that stale callback here
            // would mutate controls owned by its original thread during fixture cleanup.
            refreshers.Clear();
            languageHandlers.SetValue(null, null);
            shortcutHandlers.SetValue(null, null);
            primary.SetValue(null, owner);
            // EnterScope alone inherits the parent's MediaContext and its animation clock.
            // Rendering and transitions must resolve services owned by this dispatcher.
            AvaloniaLocator.Current = AvaloniaLocator.CurrentMutable = new AvaloniaLocator();
            AvaloniaLocator.CurrentMutable.Bind<IAssetLoader>().ToConstant(new StandardAssetLoader());
            SkiaPlatform.Initialize(); // Compiled bitmap icons, without native windows.
            BooruReviewTestPlatform.BindTextShaper();
            owner.Invoke(() =>
            {
                Texts.Initialize();
                Texts.SetLanguage("en");
                test();
            }, DispatcherPriority.Send);
        }
        finally
        {
            try
            {
                // Fixture disposal has ended the page lifetime. Drain only work posted to this
                // fresh scope; it cannot contain callbacks inherited from another test.
                owner.RunJobs();
            }
            finally
            {
                // Also handles constructor failures before a fixture can be disposed: only
                // this scope's registrations are removed, and every previous observer survives.
                languageHandlers.SetValue(null, null);
                try { Texts.SetLanguage(language); }
                finally
                {
                    refreshers.Clear();
                    refreshers.AddRange(previousRefreshers);
                    pruneCounter.SetValue(null, previousPruneCounter);
                    languageHandlers.SetValue(null, previousHandlers);
                    shortcutHandlers.SetValue(null, previousShortcutHandlers);
                    primary.SetValue(null, previousDispatcher);
                }
            }
        }
    }

    private static FieldInfo Field(Type type, string name) => type.GetField(name, StaticPrivate)
        ?? throw new MissingFieldException(type.FullName, name);
}
