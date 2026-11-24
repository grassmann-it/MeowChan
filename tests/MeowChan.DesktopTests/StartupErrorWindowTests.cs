using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

[Collection("Avalonia global state")]
public sealed class StartupErrorWindowTests
{
    private const string Message = " Fehler beim Start 🚧\r\n日本語 · Żółć\n\n  Letzte Zeile  ";

    [Fact]
    public void AConstructedWindowJoinsLanguageChangesOnlyWhenItOpens() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        StartupErrorWindow window = new(Message);
        string initialTitle = window.Title!;
        try
        {
            Texts.SetLanguage("de");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(initialTitle, window.Title);

            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Equal(Texts.T("startup.title"), window.Title);
            Assert.Equal(Texts.T("action.exit"), Find<Button>(window, "StartupErrorExit").Content);

            window.Close();
            Texts.SetLanguage("en");
            Dispatcher.UIThread.RunJobs();
            Assert.NotEqual(Texts.T("startup.title"), window.Title);
        }
        finally
        {
            if (window.IsVisible) window.Close();
        }
    });

    [Fact]
    public void KeyboardCopiesTheWholeDisplayedMessageAndCanExit() => WithWindow(null, window =>
    {
        var detail = Find<SelectableTextBlock>(window, "StartupErrorMessage");
        var copy = Find<Button>(window, "StartupErrorCopy");
        var exit = Find<Button>(window, "StartupErrorExit");
        var status = Find<TextBlock>(window, "StartupErrorCopyStatus");
        Assert.Null(window.Owner);
        Assert.Equal(Message, detail.Text);
        Assert.False(status.IsVisible);

        Assert.True(detail.Focus());
        Press(window, Key.Tab, PhysicalKey.Tab);
        Assert.Same(copy, window.FocusManager!.GetFocusedElement());
        Press(window, Key.Enter, PhysicalKey.Enter);

        Task<string?> read = window.Clipboard!.TryGetTextAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.True(read.IsCompletedSuccessfully);
        Assert.Equal(Message, read.GetAwaiter().GetResult());
        Assert.Equal("Message text copied.", status.Text);
        Assert.True(status.IsVisible);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(status));

        Texts.SetLanguage("de");
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Assert.Equal("Meldungstext kopiert.", status.Text);
        Assert.Equal("Meldungstext kopieren", AutomationProperties.GetName(copy));
        Rect copyBounds = BoundsInWindow(window, copy);
        Rect exitBounds = BoundsInWindow(window, exit);
        Assert.True(copyBounds.Right <= exitBounds.Left + 1 || copyBounds.Bottom <= exitBounds.Top + 1);

        Press(window, Key.Tab, PhysicalKey.Tab);
        Assert.Same(exit, window.FocusManager.GetFocusedElement());
        Press(window, Key.Enter, PhysicalKey.Enter);
        Assert.False(window.IsVisible);
    });

    [Fact]
    public void MissingClipboardShowsLocalFailureAndKeepsExitAvailable() => WithWindow(() => null, window =>
    {
        var copy = Find<Button>(window, "StartupErrorCopy");
        var exit = Find<Button>(window, "StartupErrorExit");
        var status = Find<TextBlock>(window, "StartupErrorCopyStatus");
        Assert.True(copy.Focus());
        Press(window, Key.Space, PhysicalKey.Space);

        Assert.True(status.IsVisible);
        Assert.Equal("The text could not be copied.", status.Text);
        Assert.True(copy.IsEnabled);
        Press(window, Key.Tab, PhysicalKey.Tab);
        Assert.Same(exit, window.FocusManager!.GetFocusedElement());
        Press(window, Key.Enter, PhysicalKey.Enter);
        Assert.False(window.IsVisible);
    });

    [Fact]
    public void AFailedWriteIsRetryableAndEscapeCanCloseDuringTheRetry()
    {
        var clipboard = new PendingClipboard();
        WithWindow(() => clipboard, window =>
        {
            var copy = Find<Button>(window, "StartupErrorCopy");
            var exit = Find<Button>(window, "StartupErrorExit");
            var status = Find<TextBlock>(window, "StartupErrorCopyStatus");
            Assert.True(copy.Focus());
            Press(window, Key.Enter, PhysicalKey.Enter);
            Press(window, Key.Enter, PhysicalKey.Enter);
            Assert.Equal(1, clipboard.Writes);
            Assert.False(status.IsVisible);
            Assert.True(exit.IsEffectivelyEnabled);

            clipboard.Completion.SetException(new InvalidOperationException("Clipboard is busy."));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("The text could not be copied.", status.Text);
            Assert.Same(copy, window.FocusManager!.GetFocusedElement());

            clipboard.Completion = new TaskCompletionSource();
            Press(window, Key.Enter, PhysicalKey.Enter);
            Assert.Equal(2, clipboard.Writes);
            Assert.False(status.IsVisible);
            Press(window, Key.Escape, PhysicalKey.Escape);
            Assert.False(window.IsVisible);
            clipboard.Completion.SetResult();
            Dispatcher.UIThread.RunJobs();
            Assert.False(status.IsVisible);
        });
    }

    private static void WithWindow(Func<IClipboard?>? getClipboard, Action<StartupErrorWindow> run) =>
        OwnedUiDispatcher.Run(() =>
        {
            using IDisposable platform = BooruReviewTestPlatform.Enter();
            string previousLanguage = Texts.Language;
            Texts.SetLanguage("en");
            var window = getClipboard == null ? new StartupErrorWindow(Message) : new StartupErrorWindow(Message, getClipboard);
            try
            {
                window.Width = window.MinWidth;
                window.Height = window.MinHeight;
                window.Show();
                window.PlatformImpl!.Activated?.Invoke();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                run(window);
            }
            finally
            {
                if (window.IsVisible) window.Close();
                Texts.SetLanguage(previousLanguage);
                Dispatcher.UIThread.RunJobs();
            }
        });

    private static T Find<T>(Window window, string name) where T : Control =>
        Assert.Single(window.GetVisualDescendants().OfType<T>(), control => control.Name == name);

    private static Rect BoundsInWindow(Window window, Control control) =>
        new(control.TranslatePoint(default, window)!.Value, control.Bounds.Size);

    private static void Press(Window window, Key key, PhysicalKey physicalKey)
    {
        window.KeyPress(key, RawInputModifiers.None, physicalKey, null);
        if (window.IsVisible)
            window.KeyRelease(key, RawInputModifiers.None, physicalKey, null);
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class PendingClipboard : IClipboard
    {
        public TaskCompletionSource Completion { get; set; } = new();
        public int Writes { get; private set; }

        public async Task SetDataAsync(IAsyncDataTransfer? dataTransfer)
        {
            using (dataTransfer)
            {
                Writes++;
                await Completion.Task;
            }
        }

        public Task ClearAsync() => throw new NotSupportedException();
        public Task FlushAsync() => throw new NotSupportedException();
        public Task<IAsyncDataTransfer?> TryGetDataAsync() => throw new NotSupportedException();
        public Task<IAsyncDataTransfer?> TryGetInProcessDataAsync() => throw new NotSupportedException();
    }
}
