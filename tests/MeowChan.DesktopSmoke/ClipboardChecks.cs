using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopSmoke;

internal static class ClipboardChecks
{
    private static FixtureClipboard? _clipboard;

    // Avalonia X11 resolves the window clipboard through this platform service.
    // Install after platform services, before App creates any windows.
    public static void Install()
    {
        if (!OperatingSystem.IsLinux())
            return;

        _clipboard = new FixtureClipboard();
        AvaloniaLocator.CurrentMutable.Bind<IClipboard>().ToConstant(_clipboard);
    }

    internal static async Task<bool> CheckIsolatedCopyAsync(Window window, Func<Task> copy,
        IReadOnlyList<string> requiredValues, IReadOnlyList<string> forbiddenValues)
    {
        if (_clipboard == null)
        {
            Console.WriteLine("SMOKE SKIP: isolated technical copy fixture unavailable; native clipboard was not accessed.");
            return false;
        }
        FixtureClipboard clipboard = _clipboard;
        if (!ReferenceEquals(window.Clipboard, clipboard))
            throw new InvalidOperationException("Technical copy is not isolated from the native desktop clipboard.");
        int writes = clipboard.WriteCount, reads = clipboard.ReadCount;
        await copy();
        await WaitUntilAsync(() => clipboard.WriteCount == writes + 1);
        string payload = clipboard.WrittenText ?? string.Empty;
        if (clipboard.ReadCount != reads || requiredValues.Any(value => !payload.Contains(value, StringComparison.Ordinal))
            || forbiddenValues.Any(value => payload.Contains(value, StringComparison.Ordinal)))
            throw new InvalidOperationException("Technical copy omitted a full identity, exposed draft/settings text, or read the clipboard.");
        return true;
    }

    internal static async Task CheckCopyAsync(MainWindow window, ThreadRow thread, BoardRow board)
    {
        if (_clipboard == null)
            return;
        FixtureClipboard clipboard = _clipboard;
        if (!ReferenceEquals(window.Clipboard, clipboard))
            throw new InvalidOperationException("Copy fixture is not isolated from the native desktop clipboard.");

        DataGrid threads = window.FindControl<DataGrid>("ThreadsDataGrid")!;
        DataGrid boards = window.FindControl<DataGrid>("BoardsDataGrid")!;
        TabControl tabs = window.FindControl<TabControl>("DownloadsTabs")!;
        threads.ContextMenu!.Close();
        boards.ContextMenu!.Close();
        await WaitUntilAsync(() => !threads.ContextMenu.IsOpen && !boards.ContextMenu.IsOpen);
        tabs.SelectedIndex = 0;
        await GridChecks.OpenContextForAsync(window, threads, thread);
        int writes = clipboard.WriteCount;
        MenuItem threadCopy = threads.ContextMenu.Items.OfType<MenuItem>()
            .Single(item => item.Tag as string == "selection.copy-links");
        await WaitUntilAsync(() => threadCopy.IsEffectivelyVisible && threadCopy.IsEffectivelyEnabled);
        threadCopy.Command!.Execute(null);
        await WaitUntilAsync(() => clipboard.WriteCount == writes + 1);
        if (clipboard.WrittenText != thread.Url)
            throw new InvalidOperationException("Thread copy did not use the selected watch's canonical URL.");
        threads.ContextMenu!.Close();

        tabs.SelectedIndex = 1;
        await GridChecks.OpenContextForAsync(window, boards, board);
        MenuItem boardCopy = boards.ContextMenu.Items.OfType<MenuItem>()
            .Single(item => item.Tag as string == "selection.copy-links");
        await WaitUntilAsync(() => boardCopy.IsEffectivelyVisible && boardCopy.IsEffectivelyEnabled);
        boardCopy.Command!.Execute(null);
        await WaitUntilAsync(() => clipboard.WriteCount == writes + 2);
        if (clipboard.WrittenText != board.Url)
            throw new InvalidOperationException("Board copy did not use the selected watch's canonical URL.");
        boards.ContextMenu!.Close();

        string allUrls = string.Join(Environment.NewLine, window.UrlList.Select(row => row.Url)
            .Concat(window.BoardsList.Select(row => row.Url)).Distinct(StringComparer.Ordinal));
        Click(window.FindControl<Button>("CopyAllUrlsButton")!);
        await WaitUntilAsync(() => clipboard.WriteCount == writes + 3);
        if (clipboard.WrittenText != allUrls)
            throw new InvalidOperationException("Copy all omitted or changed a tracked thread or board URL.");
        Console.WriteLine("SMOKE isolated clipboard: selected thread/board and all tracked URLs copied without reading the desktop clipboard.");
    }

    public static async Task RunAsync(MainWindow window)
    {
        if (!OperatingSystem.IsLinux())
        {
            Console.WriteLine("SMOKE SKIP: clipboard isolation uses X11; native clipboard untested.");
            return;
        }

        FixtureClipboard clipboard = _clipboard
            ?? throw new InvalidOperationException("The isolated clipboard was not installed.");
        if (!ReferenceEquals(window.Clipboard, clipboard))
            throw new InvalidOperationException("Window clipboard isolation failed; refusing to access the desktop clipboard.");

        Button add = window.FindControl<Button>("AddUrlButton")
            ?? throw new InvalidOperationException("The Add button is missing.");
        TextBox input = window.FindControl<TextBox>("UrlInput")
            ?? throw new InvalidOperationException("The URL input is missing.");

        // Unsupported hosts are rejected during Registry admission, before any provider request.
        const string explicitUrl = "  https://unsupported.invalid/explicit  ";
        clipboard.Read = () => throw new InvalidOperationException("Explicit Add unexpectedly read the clipboard.");
        input.Text = explicitUrl;
        Click(add);
        Expander reportExpander = (Expander)window.FindControl<Border>("AdmissionResultsPanel")!.Child!;
        reportExpander.IsExpanded = true;
        await WaitUntilAsync(() => add.IsEnabled && ReportLines(window).Length == 1);
        if (clipboard.ReadCount != 0 || input.Text != explicitUrl)
            throw new InvalidOperationException("Explicit Add read the clipboard or discarded a rejected URL.");
        RequireReport(window, ("https://unsupported.invalid/explicit", "admission.unsupported"));

        const string directBatch = "https://unsupported.invalid/one,not a URL\r\nhttps://unsupported.invalid/two";
        input.Text = directBatch;
        Click(add);
        await WaitUntilAsync(() => add.IsEnabled && ReportLines(window).Length == 3);
        RequireReport(window, ("https://unsupported.invalid/one", "admission.unsupported"),
            ("not a URL", "admission.invalid"), ("https://unsupported.invalid/two", "admission.unsupported"));
        if (clipboard.ReadCount != 0 || input.Text != directBatch)
            throw new InvalidOperationException("Direct batch intake read the clipboard or discarded rejected entries.");

        input.Text = "https://unsupported.invalid/entered";
        input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        await WaitUntilAsync(() => add.IsEnabled && ReportLines(window).Length == 1);
        RequireReport(window, ("https://unsupported.invalid/entered", "admission.unsupported"));
        if (clipboard.ReadCount != 0 || input.Text != "https://unsupported.invalid/entered")
            throw new InvalidOperationException("Enter submission did not retain the rejected URL or read the clipboard.");

        TaskCompletionSource<IAsyncDataTransfer?> clipboardRead = new TaskCompletionSource<IAsyncDataTransfer?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        clipboard.Read = () => clipboardRead.Task;
        input.Text = " ";
        Click(add);
        if (add.IsEnabled || clipboard.ReadCount != 1)
            throw new InvalidOperationException("Empty Add did not wait for exactly one clipboard read.");
        Click(add);
        if (clipboard.ReadCount != 1)
            throw new InvalidOperationException("An Add action overlapped the pending clipboard import.");
        clipboardRead.SetResult(TextData("https://unsupported.invalid/first\r\nnot a URL\n https://unsupported.invalid/second "));
        await WaitUntilAsync(() => add.IsEnabled && ReportLines(window).Length == 3);
        RequireReport(window, ("https://unsupported.invalid/first", "admission.unsupported"),
            ("not a URL", "admission.invalid"), ("https://unsupported.invalid/second", "admission.unsupported"));
        if (clipboard.ReadCount != 1)
            throw new InvalidOperationException("Multiline import read the clipboard more than once.");

        clipboard.Read = () => Task.FromResult<IAsyncDataTransfer?>(TextData("  \r\n "));
        input.Text = string.Empty;
        Click(add);
        await WaitUntilAsync(() => add.IsEnabled && Notifications(window, Texts.T("clipboard.title"))
            .Any(notification => notification.Message == Texts.T("clipboard.empty")));
        if (clipboard.ReadCount != 2)
            throw new InvalidOperationException("Empty clipboard import did not read once.");
        CloseNotifications(window);

        clipboard.Read = () => Task.FromException<IAsyncDataTransfer?>(
            new InvalidOperationException("Smoke clipboard read failure."));
        Click(add);
        await WaitUntilAsync(() => add.IsEnabled && Notifications(window, Texts.T("notification.error"))
            .Any(notification => notification.Message == Texts.T("clipboard.readFailed")));
        if (clipboard.ReadCount != 3 || !input.IsEnabled || window.UrlList.Count != 0 || window.BoardsList.Count != 0)
            throw new InvalidOperationException("Clipboard failure did not leave the empty session usable.");
        CloseNotifications(window);
        clipboard.Read = () => Task.FromResult<IAsyncDataTransfer?>(null);
        reportExpander.IsExpanded = false;

        Console.WriteLine("SMOKE isolated clipboard: explicit/batch Add bypassed clipboard; every valid-shaped and invalid token got an ordered result; empty Add imported once; overlapping Add ignored; empty/error feedback restored controls.");
    }

    private static string[] ReportLines(MainWindow window) => window.GetVisualDescendants().OfType<TextBox>()
        .SingleOrDefault(box => box.Name == "AdmissionReport")?.Text?.Split(
            new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();

    private static void RequireReport(MainWindow window, params (string Url, string StatusKey)[] expected)
    {
        string[] actual = ReportLines(window);
        string[] expectedLines = expected.Select(item => Texts.T(item.StatusKey) + " · " + item.Url).ToArray();
        if (!actual.SequenceEqual(expectedLines, StringComparer.Ordinal))
            throw new InvalidOperationException("Intake did not retain one ordered outcome for every entry: " + string.Join(" | ", actual));
    }

    private static DataTransfer TextData(string text)
    {
        DataTransfer data = new DataTransfer();
        data.Add(DataTransferItem.Create(DataFormat.Text, text));
        return data;
    }

    private static void Click(Button button) => SmokeActionClick.Execute(button);

    private static INotification[] Notifications(Window window, string title) => window.GetVisualDescendants()
        .OfType<NotificationCard>()
        .Where(card => !card.IsClosing && !card.IsClosed)
        .Select(card => card.Content)
        .OfType<INotification>()
        .Where(notification => notification.Title == title)
        .ToArray();

    private static void CloseNotifications(Window window)
    {
        foreach (NotificationCard card in window.GetVisualDescendants().OfType<NotificationCard>().ToArray())
            card.Close();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("Clipboard UI state did not become ready within five seconds.");
            await Task.Delay(25);
        }
    }

    // This fixture never delegates to a native clipboard, including for write operations.
    private sealed class FixtureClipboard : IClipboard
    {
        public int ReadCount { get; private set; }
        public int WriteCount { get; private set; }
        public string? WrittenText { get; private set; }
        public Func<Task<IAsyncDataTransfer?>> Read { get; set; } = () =>
            Task.FromException<IAsyncDataTransfer?>(new InvalidOperationException("No fixture clipboard content configured."));

        public Task<IAsyncDataTransfer?> TryGetDataAsync()
        {
            ReadCount++;
            return Read();
        }

        public async Task SetDataAsync(IAsyncDataTransfer? dataTransfer)
        {
            if (dataTransfer == null) throw UnexpectedApi();
            try
            {
                WrittenText = await dataTransfer.TryGetValueAsync(DataFormat.Text);
                WriteCount++;
            }
            finally { dataTransfer.Dispose(); }
        }
        public Task ClearAsync() => throw UnexpectedApi();
        public Task FlushAsync() => throw UnexpectedApi();
        public Task<IAsyncDataTransfer?> TryGetInProcessDataAsync() => throw UnexpectedApi();

        private static InvalidOperationException UnexpectedApi() =>
            new InvalidOperationException("The smoke fixture received an unexpected clipboard API call.");
    }
}
