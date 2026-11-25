using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.Raw;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using static MeowChan.DesktopTests.DownloadWindowFixture;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class DownloadsInspectorCopyTests
{
    [Fact]
    public void TheNarrowInspectorCopiesBothCompleteValuesFromTheKeyboardAndReportsAtEachField()
    {
        RecordingClipboard clipboard = new();
        Run(async fixture =>
        {
            fixture.Window.SetRenderScaling(1.5);
            Assert.Equal(1.5, fixture.Window.RenderScaling);
            const string secondUrl = "https://downloads.invalid/a/thread/2";
            string url = "https://downloads.invalid/a/thread/1?value=" + new string('x', 1200) + "%2F%E2%98%83";
            using TempDirectory root = new();
            string folder = Path.Combine(root.Path, string.Concat(Enumerable.Repeat("日本語-e\u0301-🐈-", 35)), "Dateien");
            Assert.False(Directory.Exists(folder));
            DesktopThreadEntry first = Thread(1, url, folder), second = Thread(2, secondUrl);
            fixture.ShowThreads([first, second]);
            fixture.Select(1);
            fixture.CallWindow("OpenWorkspaceDetails");
            fixture.Find<Expander>("InspectorSourceDetails").IsExpanded = true;
            fixture.Layout();
            Border panel = fixture.Find<Border>("WorkspaceInspector");
            Button copyUrl = fixture.Find<Button>("InspectorCopyUrlButton");
            Button copyFolder = fixture.Find<Button>("InspectorCopyDirectoryButton");
            TextBlock urlStatus = fixture.Find<TextBlock>("InspectorCopyUrlStatus");
            TextBlock folderStatus = fixture.Find<TextBlock>("InspectorCopyDirectoryStatus");
            Assert.Same(clipboard, fixture.Window.Clipboard);
            Assert.InRange(panel.Bounds.Width, 259, 321);
            foreach (Button button in new[] { copyUrl, copyFolder })
            {
                Point rightEdge = button.TranslatePoint(new Point(button.Bounds.Width, 0), panel)!.Value;
                Assert.InRange(rightEdge.X, button.Bounds.Width, panel.Bounds.Width + 1);
            }
            Assert.Equal(url, ToolTip.GetTip(fixture.Find<SelectableTextBlock>("InspectorUrl")));
            Assert.Equal(folder, ToolTip.GetTip(fixture.Find<SelectableTextBlock>("InspectorDirectory")));
            Assert.True(copyUrl.Focus());
            Press(fixture.Window, Key.Space, PhysicalKey.Space);
            await fixture.WindowField<Task>("_inspectorClipboardWrite");
            Assert.Equal(new[] { url }, clipboard.Writes);
            Assert.Equal("URL copied", urlStatus.Text);
            Assert.True(urlStatus.IsVisible);
            Assert.False(folderStatus.IsVisible);
            Assert.True(copyUrl.IsFocused);

            for (int i = 0; i < 4 && !copyFolder.IsFocused; i++) Press(fixture.Window, Key.Tab, PhysicalKey.Tab);
            Assert.True(copyFolder.IsFocused);
            Press(fixture.Window, Key.Space, PhysicalKey.Space);
            await fixture.WindowField<Task>("_inspectorClipboardWrite");
            Assert.Equal(new[] { url, folder }, clipboard.Writes);
            Assert.Equal(Texts.T("media.copyPath.done"), folderStatus.Text);
            Assert.True(folderStatus.IsVisible);

            clipboard.BeforeWrite = _ => Task.FromException(new InvalidOperationException("Fixture clipboard unavailable."));
            Press(fixture.Window, Key.Space, PhysicalKey.Space);
            await fixture.WindowField<Task>("_inspectorClipboardWrite");
            Assert.Equal(new[] { url, folder }, clipboard.Writes);
            Assert.Equal(Texts.T("ctxmenu.copyFailed"), folderStatus.Text);
            Assert.Contains("workspace-error", folderStatus.Classes);
            Assert.Equal("URL copied", urlStatus.Text);

            TaskCompletionSource pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            clipboard.BeforeWrite = _ => pending.Task;
            Assert.True(copyUrl.Focus());
            Press(fixture.Window, Key.Space, PhysicalKey.Space);
            Task oldWrite = fixture.WindowField<Task>("_inspectorClipboardWrite");
            Assert.False(oldWrite.IsCompleted);
            fixture.Select(2);
            Assert.False(copyFolder.IsEnabled);
            Assert.Equal("No download folder", fixture.Find<SelectableTextBlock>("InspectorDirectory").Text);
            Assert.False(folderStatus.IsVisible);
            pending.SetResult();
            await oldWrite;
            Assert.False(urlStatus.IsVisible);
            clipboard.BeforeWrite = null;
            Assert.True(copyUrl.Focus());
            Press(fixture.Window, Key.Space, PhysicalKey.Space);
            await fixture.WindowField<Task>("_inspectorClipboardWrite");
            Assert.Equal(new[] { url, folder, url, secondUrl }, clipboard.Writes);

            foreach (string language in Texts.BundledPackCodes().Concat(new[] { "de", "en" }))
            {
                Texts.SetLanguage(language);
                fixture.Layout();
                Assert.Equal(language, Texts.Language);
                Assert.Equal(Texts.T("action.copyUrl"), copyUrl.Content);
                Assert.Equal(Texts.T("detail.copyDirectory"), copyFolder.Content);
                Assert.Equal(Texts.T("detail.urlCopied"), urlStatus.Text);
                Point rightEdge = copyFolder.TranslatePoint(new Point(copyFolder.Bounds.Width, 0), panel)!.Value;
                Assert.InRange(rightEdge.X, copyFolder.Bounds.Width, panel.Bounds.Width + 1);
            }
        }, () => AvaloniaLocator.CurrentMutable.Bind<IClipboard>().ToConstant(clipboard));
    }

    private static void Press(Window window, Key key, PhysicalKey physicalKey)
    {
        window.KeyPress(key, RawInputModifiers.None, physicalKey, null);
        window.KeyRelease(key, RawInputModifiers.None, physicalKey, null);
    }

    private sealed class RecordingClipboard : IClipboard
    {
        internal List<string> Writes { get; } = [];
        internal Func<string, Task>? BeforeWrite { get; set; }

        public async Task SetDataAsync(IAsyncDataTransfer? dataTransfer)
        {
            using IAsyncDataTransfer transfer = dataTransfer!;
            string value = (await transfer.TryGetValueAsync(DataFormat.Text))!;
            if (BeforeWrite is { } beforeWrite) await beforeWrite(value);
            Writes.Add(value);
        }

        public Task<IAsyncDataTransfer?> TryGetDataAsync() => throw new InvalidOperationException("Unexpected read.");
        public Task<IAsyncDataTransfer?> TryGetInProcessDataAsync() => throw new InvalidOperationException("Unexpected read.");
        public Task ClearAsync() => throw new InvalidOperationException("Unexpected clear.");
        public Task FlushAsync() => throw new InvalidOperationException("Unexpected flush.");
    }
}
