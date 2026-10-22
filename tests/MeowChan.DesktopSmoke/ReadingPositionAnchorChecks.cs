using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopSmoke;

internal static class ReadingPositionAnchorChecks
{
    private const string ExpectationFile = "reading-restart-anchor.json";
    // Match the existing V18 visible-post/offset check; this is not a new tolerance.
    private const double OffsetTolerance = 2;

    internal static async Task PrepareRestartAsync(MainWindow window, string profile)
    {
        ArchivePage page = window.ArchivePage;
        ListBox posts = page.FindControl<ListBox>("ArchivePosts")!;
        await page.FocusPostAsync("1022");
        await page.MediaLoadTask;
        await FlushLayoutAsync(window);
        ScrollViewer scroll = posts.GetVisualDescendants().OfType<ScrollViewer>().First();
        ScrollContentPresenter viewport = scroll.GetVisualDescendants().OfType<ScrollContentPresenter>().First();
        double before = scroll.Offset.Y;
        using Pointer pointer = new(Pointer.GetNextFreeId(), PointerType.Mouse, true);
        Point point = viewport.TranslatePoint(new Point(20, 20), window)!.Value;
        viewport.RaiseEvent(new PointerWheelEventArgs(viewport, pointer, window, point, 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other),
            KeyModifiers.None, new Vector(0, -1)));
        await FlushLayoutAsync(window);
        Require(scroll.Offset.Y > before + 10, "The restart fixture did not actually scroll through reader wheel input.");
        LocalReadingPosition expected = VisiblePosition(page);
        Require(expected.SelectedPostId == "1022" && expected.Top < -10,
            "The restart fixture must retain selection and start inside a partially scrolled post.");
        await WaitForSavedAsync(window, expected);
        RestartExpectation expectation = new(Environment.ProcessId, expected, window.RenderScaling,
            window.ClientSize.Width, window.ClientSize.Height);
        await File.WriteAllTextAsync(Path.Combine(profile, ExpectationFile), JsonSerializer.Serialize(expectation));
        Program.CaptureWindow(window, "smoke-reading-position-before-restart.png");
        Trace("before restart", expectation, expected);
    }

    internal static async Task CheckRestartAsync(MainWindow window, string profile)
    {
        RestartExpectation expected = JsonSerializer.Deserialize<RestartExpectation>(
            await File.ReadAllTextAsync(Path.Combine(profile, ExpectationFile)))
            ?? throw new InvalidOperationException("The first process did not record its visible reading anchor.");
        Require(expected.ProcessId != Environment.ProcessId, "The restart check must run in a different process.");
        Require(expected.Position.Top < -10, "The first process did not leave a nonzero reading offset.");
        Require(window.SessionHost.Config.RememberReadingPosition, "The second process lost the explicit opt-in.");
        LocalReadingPosition? saved = await window.SessionHost.ReadReadingPositionAsync();
        Require(saved == expected.Position, "The second process did not load the exact persisted reading anchor.");
        ContentControl reader = window.FindControl<ContentControl>("ReaderHost")!;
        await FlushLayoutAsync(window);
        Require(!reader.IsVisible && window.SelectedPage == MainWindow.WorkspacePage.Downloads,
            "Restart automatically opened the saved reader or replaced the existing start page.");
        Require(window.RenderScaling == expected.Scale
            && Math.Abs(window.ClientSize.Width - expected.Width) < 1
            && Math.Abs(window.ClientSize.Height - expected.Height) < 1,
            "The restart fixture must compare offsets at the same scale and client size.");
        Program.CaptureWindow(window, "smoke-reading-position-restart-before-resume.png");
        MenuItem resume = window.FindControl<MenuItem>("WorkspaceMenuItem")!.Items.OfType<MenuItem>()
            .Single(item => item.Header as string == Texts.T("resume.action"));
        resume.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        ArchivePage page = window.ArchivePage;
        await Program.WaitUntilAsync(() => reader.IsVisible
            && (page.FindControl<ListBox>("ArchivePosts")!.SelectedItem as ArchivePostRow)?.Id == expected.Position.SelectedPostId);
        await page.MediaLoadTask;
        await FlushLayoutAsync(window);
        LocalReadingPosition actual = VisiblePosition(page);
        Trace("after explicit resume", expected, actual);
        Require(Matches(expected.Position, actual),
            $"Explicit restart resume lost the visible post/selected post/offset: expected {expected.Position}, actual {actual}.");
        Require(await window.SessionHost.ReadReadingPositionAsync() == expected.Position,
            "Resume itself changed the persisted conscious reading position.");
        Program.CaptureWindow(window, "smoke-reading-position-restarted.png");
        Console.WriteLine("SMOKE reading position: two real processes preserved the exact nonzero visible anchor and selection; startup stayed closed until the explicit resume menu action.");
    }

    private static LocalReadingPosition VisiblePosition(ArchivePage page)
    {
        ListBox posts = page.FindControl<ListBox>("ArchivePosts")!;
        (string Id, double Top) visible = posts.GetVisualDescendants().OfType<ListBoxItem>()
            .Where(item => item.DataContext is ArchivePostRow)
            .Select(item => (Id: ((ArchivePostRow)item.DataContext!).Id,
                Top: item.TranslatePoint(default, posts)!.Value.Y, Height: item.Bounds.Height))
            .Where(item => item.Top + item.Height > 0).OrderBy(item => item.Top)
            .Select(item => (item.Id, item.Top)).First();
        ResourceId thread = page.CurrentThread!.Summary.Id;
        return new LocalReadingPosition(thread.Source, thread.Board, thread.Thread!, visible.Id,
            visible.Top, (posts.SelectedItem as ArchivePostRow)?.Id);
    }

    private static bool Matches(LocalReadingPosition expected, LocalReadingPosition? actual) =>
        actual != null && expected.ThreadId == actual.ThreadId && expected.PostId == actual.PostId
        && expected.SelectedPostId == actual.SelectedPostId && Math.Abs(expected.Top - actual.Top) < OffsetTolerance;

    private static async Task WaitForSavedAsync(MainWindow window, LocalReadingPosition expected)
    {
        for (int attempt = 0; attempt < 100; ++attempt)
        {
            if (Matches(expected, await window.SessionHost.ReadReadingPositionAsync())) return;
            await Task.Delay(30);
        }
        throw new TimeoutException("Conscious reader wheel input did not persist the exact visible nonzero anchor.");
    }

    private static async Task FlushLayoutAsync(MainWindow window)
    {
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
    }

    private static void Trace(string stage, RestartExpectation expected, LocalReadingPosition actual) =>
        Console.WriteLine("SMOKE reading restart anchor: " + JsonSerializer.Serialize(new
        {
            stage, previousProcess = expected.ProcessId, currentProcess = Environment.ProcessId,
            expected.Position, actual, expected.Scale, expected.Width, expected.Height,
            pixelTop = actual.Top * expected.Scale, offsetErrorDip = Math.Abs(actual.Top - expected.Position.Top)
        }));

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed record RestartExpectation(int ProcessId, LocalReadingPosition Position, double Scale, double Width, double Height);
}
