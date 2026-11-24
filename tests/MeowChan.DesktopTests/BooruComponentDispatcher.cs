using System.Reflection;
using System.Runtime.ExceptionServices;
using Avalonia;
using Avalonia.Platform;
using Avalonia.Skia;
using Avalonia.Threading;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

// Used by nonparallel collections. Constructed controls must neither refresh another
// test's thread-owned controls nor outlive their dispatcher.
internal static class BooruComponentDispatcher
{
    private const BindingFlags StaticPrivate = BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(10);

    [ThreadStatic]
    private static Owner? _current;

    internal static void Run(Action test, Func<string>? waitingFor = null, TimeSpan? timeout = null)
    {
        using Owner owner = new(test, waitingFor, timeout ?? Deadline);
        owner.Run();
    }

    internal static CancellationToken Cancellation => Current.Cancellation;

    private static Owner Current => _current
        ?? throw new InvalidOperationException("This operation must run on the component dispatcher.");

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

    internal static void WaitUntil(Func<bool> condition, string expected) =>
        Wait(BooruTestSupport.WaitAsync(condition, expected));

    private static void PumpUntilCompleted(Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        Dispatcher owner = Dispatcher.UIThread;
        owner.VerifyAccess();
        while (!task.IsCompleted)
        {
            Current.CheckDeadline();
            owner.RunJobs();
            if (!task.IsCompleted) Thread.Sleep(1);
        }
    }

    private sealed class Owner : IDisposable
    {
        private readonly CancellationTokenSource _deadline;
        private readonly TimeSpan _timeout;
        private readonly Thread _thread;
        private readonly Func<string>? _waitingFor;
        private ExceptionDispatchInfo? _failure;

        internal Owner(Action test, Func<string>? waitingFor, TimeSpan timeout)
        {
            _timeout = timeout;
            _waitingFor = waitingFor;
            _deadline = new();
            _thread = new Thread(() => Execute(test)) { IsBackground = true, Name = "Booru component dispatcher" };
        }

        internal CancellationToken Cancellation => _deadline.Token;

        internal void CheckDeadline()
        {
            if (Cancellation.IsCancellationRequested) throw Timeout();
        }

        private TimeoutException Timeout() => new("The component dispatcher timed out waiting for "
            + (_waitingFor?.Invoke() ?? "component work to finish") + ".");

        internal void Run()
        {
            _deadline.CancelAfter(_timeout);
            _thread.Start();
            if (!_thread.Join(_timeout + ShutdownGrace))
            {
                // VSTest executes this assembly in a separate testhost. If a callback refuses
                // to return, end that process before any test can inherit the live dispatcher.
                Environment.FailFast("The component dispatcher did not acknowledge cancellation and shut down.");
            }
            _failure?.Throw();
        }

        private void Execute(Action test)
        {
            _current = this;
            try
            {
                RunOnOwnedDispatcher(test);
                CheckDeadline();
            }
            catch (OperationCanceledException) when (Cancellation.IsCancellationRequested)
            {
                _failure = ExceptionDispatchInfo.Capture(Timeout());
            }
            catch (Exception exception) { _failure = ExceptionDispatchInfo.Capture(exception); }
            finally { _current = null; }
        }

        public void Dispose() => _deadline.Dispose();
    }

    private static void RunOnOwnedDispatcher(Action test)
    {
        FieldInfo primary = Field(typeof(Dispatcher), "s_uiThread");
        FieldInfo languageHandlers = Field(typeof(Texts), "Changed");
        object? previousDispatcher = primary.GetValue(null);
        object? previousHandlers = languageHandlers.GetValue(null);
        string language = Texts.Language;
        Dispatcher owner = Dispatcher.CurrentDispatcher;
        using IDisposable scope = AvaloniaLocator.EnterScope();
        using ShortcutTestState shortcuts = new();
        try
        {
            // A fresh dispatcher is intentional: another test may have left a language-change
            // callback queued on the xunit worker's dispatcher. Running that stale callback here
            // would mutate controls owned by its original thread during fixture cleanup.
            languageHandlers.SetValue(null, null);
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
                try { owner.RunJobs(); }
                finally { owner.InvokeShutdown(); }
            }
            finally
            {
                // Also handles constructor failures before a fixture can be disposed: only
                // this scope's registrations are removed, and every previous observer survives.
                languageHandlers.SetValue(null, null);
                try { Texts.SetLanguage(language); }
                finally
                {
                    languageHandlers.SetValue(null, previousHandlers);
                    primary.SetValue(null, previousDispatcher);
                }
            }
        }
    }

    private static FieldInfo Field(Type type, string name) => type.GetField(name, StaticPrivate)
        ?? throw new MissingFieldException(type.FullName, name);
}
