using System.Reflection;
using System.Runtime.ExceptionServices;
using Avalonia;
using Avalonia.Platform;
using Avalonia.Skia;
using Avalonia.Threading;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

// Used only by the nonparallel "Booru exact navigation" collection. Constructed controls
// must neither refresh another test's thread-owned controls nor outlive their dispatcher.
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

    private static void RunOnOwnedDispatcher(Action test)
    {
        FieldInfo primary = Field(typeof(Dispatcher), "s_uiThread");
        FieldInfo languageHandlers = Field(typeof(Texts), "Changed");
        FieldInfo pruneCounter = Field(typeof(ShortcutService), "_observersUntilPrune");
        var refreshers = (List<WeakReference<Action>>)Field(typeof(ShortcutService), "Refreshers").GetValue(null)!;
        object? previousDispatcher = primary.GetValue(null);
        object? previousHandlers = languageHandlers.GetValue(null);
        object? previousPruneCounter = pruneCounter.GetValue(null);
        WeakReference<Action>[] previousRefreshers = refreshers.ToArray();
        string language = Texts.Language;
        Dispatcher owner = Dispatcher.CurrentDispatcher;
        using IDisposable scope = AvaloniaLocator.EnterScope();
        AvaloniaLocator.CurrentMutable.Bind<IAssetLoader>().ToConstant(new StandardAssetLoader());
        SkiaPlatform.Initialize(); // Compiled bitmap icons, without native windows.
        try
        {
            // A fresh dispatcher is intentional: another test may have left a language-change
            // callback queued on the xunit worker's dispatcher. Running that stale callback here
            // would mutate controls owned by its original thread during fixture cleanup.
            refreshers.Clear();
            languageHandlers.SetValue(null, null);
            primary.SetValue(null, owner);
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
                    primary.SetValue(null, previousDispatcher);
                }
            }
        }
    }

    private static FieldInfo Field(Type type, string name) => type.GetField(name, StaticPrivate)
        ?? throw new MissingFieldException(type.FullName, name);
}
