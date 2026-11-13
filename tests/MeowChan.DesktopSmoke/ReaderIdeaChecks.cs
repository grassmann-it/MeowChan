using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Testing;

namespace MeowChan.DesktopSmoke;

// Reader ideas I13 (imageboard formatting), I1 (quote preview, jump highlight), I3 (unread line),
// I6 (folded long posts) and I2 (inline replies) in a private profile with a detached reader.
internal static class ReaderIdeaChecks
{
    private sealed class Fixture : IAsyncDisposable
    {
        internal required string Profile { get; init; }
        internal required ProfileLease Lease { get; init; }
        internal required ArchiveCatalog Catalog { get; init; }
        internal required ResourceId Id { get; init; }
        internal ArchivePage Page { get; set; } = null!;
        internal Window Window { get; set; } = null!;
        internal ListBox List => Page.FindControl<ListBox>("ArchivePosts")!;

        public async ValueTask DisposeAsync()
        {
            Page?.EndLifetime();
            Window?.Close();
            await Catalog.DisposeAsync();
            Lease.Dispose();
            Directory.Delete(Profile, recursive: true);
        }
    }

    internal static async Task RunAsync(MainWindow owner)
    {
        await CheckFormattingAsync(owner);
    }

    private static async Task<Fixture> OpenAsync(MainWindow owner, string thread, Func<int, CapturedPost[]> posts, int initialCount = int.MaxValue)
    {
        string profile = Path.Combine(TemporaryWorkspacePaths.TempRoot, "meowchan-reader-ideas-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        ProfileLease lease = ProfileLease.Acquire(profile);
        ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        ResourceId id = new("4chan", "g", thread);
        Fixture fixture = new() { Profile = profile, Lease = lease, Catalog = catalog, Id = id };
        await CaptureAsync(fixture, posts(initialCount));
        ArchivePage page = new(token => ArchiveReader.OpenAsync(profile, token), (_, _) => Task.CompletedTask, _ => { });
        fixture.Page = page;
        Control pane = page.DetachReader();
        fixture.Window = new Window { Width = 1120, Height = 820, Content = pane, Title = "Private reader ideas" };
        fixture.Window.Show(owner);
        page.IsAvailable = true;
        await page.LoadThreadAsync(id);
        await Program.WaitUntilAsync(() => !page.IsBusy && fixture.List.ItemsSource is ArchivePostRow[]);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        return fixture;
    }

    private static Task CaptureAsync(Fixture fixture, CapturedPost[] posts) => fixture.Catalog.CaptureAsync(new CapturedThreadSnapshot(
        fixture.Id, "https://boards.4chan.org/g/thread/" + fixture.Id.Thread, "Reader ideas " + fixture.Id.Thread,
        ThreadSourceState.Archived, DateTimeOffset.UtcNow, fixture.Profile, posts));

    private static ListBoxItem Container(Fixture fixture, string postId) => fixture.List.GetVisualDescendants().OfType<ListBoxItem>()
        .Single(item => item.DataContext is ArchivePostRow row && row.Id == postId);

    private static ReaderBodyTextBlock Body(Fixture fixture, string postId) => Container(fixture, postId).GetVisualDescendants()
        .OfType<ReaderBodyTextBlock>().Single();

    private static Color Token(Window window, string key) => Application.Current!.TryGetResource(key, window.ActualThemeVariant, out object? value)
        && value is ISolidColorBrush brush ? brush.Color : throw new InvalidOperationException("Missing token " + key);

    // I13: dead references struck through with a note, spoilers collapsed until Enter or click, code on a surface,
    // external links underlined, internal references colour only.
    private static async Task CheckFormattingAsync(MainWindow owner)
    {
        await using Fixture fixture = await OpenAsync(owner, "300", _ =>
        [
            new("300", 0, PostFieldPresence.All, Html: "<p>Opening post</p>"),
            new("301", 1, PostFieldPresence.All, Html: "<a href='#p300' class='quotelink'>&gt;&gt;300</a> is saved, <a href='#p999' class='quotelink'>&gt;&gt;999</a> is not.<br>"
                + "<s>the secret</s> and <pre class='prettyprint'>x = 1;</pre> see <a href='https://example.org/a'>the site</a> or https://example.org/b")
        ]);
        ArchivePostRow row = fixture.List.ItemsSource!.Cast<ArchivePostRow>().Single(post => post.Id == "301");
        fixture.List.ScrollIntoView(row);
        await Program.WaitUntilAsync(() => fixture.List.GetVisualDescendants().OfType<ListBoxItem>().Any(item => ReferenceEquals(item.DataContext, row)));
        await Dispatcher.UIThread.InvokeAsync(fixture.Window.UpdateLayout, DispatcherPriority.Background);
        ReaderBodyTextBlock body = Body(fixture, "301");
        ReaderBodyDocument document = row.BodyDocument;
        Run RunWith(string text) => body.Inlines!.OfType<Run>().Single(run => run.Text != null && run.Text.Contains(text));
        string notCaptured = MeowChan.Desktop.Localization.Texts.T("reader.quoteNotCaptured");
        Require(document.Segments.Any(segment => segment.Kind == ReaderBodySegmentKind.DeadQuote) && body.PlainText.Contains(">>999 " + notCaptured),
            "The reference to an uncaptured post was not marked: " + body.PlainText);
        Run dead = RunWith(">>999");
        Require(dead.TextDecorations == TextDecorations.Strikethrough && RunWith(notCaptured).TextDecorations == null,
            "The dead reference was not struck through (alone, without its note).");
        Run live = RunWith(">>300");
        Require(live.TextDecorations == null && (live.Foreground as ISolidColorBrush)?.Color == Token(fixture.Window, "Workspace.Accent"),
            "An internal reference was not colour only.");
        Run site = RunWith("the site");
        Require(site.TextDecorations == TextDecorations.Underline && RunWith("https://example.org/b").TextDecorations == TextDecorations.Underline,
            "External links were not underlined.");
        Run code = RunWith("x = 1;");
        Require(code.FontFamily.Name == "monospace" && (code.Background as ISolidColorBrush)?.Color == Token(fixture.Window, "Workspace.ReaderCodeSurface"),
            "Code did not use monospace on its surface.");
        Color spoiler = Token(fixture.Window, "Workspace.ReaderSpoiler");
        Run secret = RunWith("the secret");
        Require((secret.Foreground as ISolidColorBrush)?.Color == spoiler && (secret.Background as ISolidColorBrush)?.Color == spoiler && body.HasHiddenSpoilers,
            "The spoiler was not collapsed to its area.");
        Program.CaptureWindow(fixture.Window, "smoke-reader-i13-formatting-hidden.png");
        body.Focus(NavigationMethod.Tab);
        body.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = body });
        await Dispatcher.UIThread.InvokeAsync(fixture.Window.UpdateLayout, DispatcherPriority.Background);
        Require(row.SpoilersRevealed && !body.HasHiddenSpoilers && (RunWith("the secret").Foreground as ISolidColorBrush)?.Color != spoiler,
            "Enter did not show the spoiler.");
        Program.CaptureWindow(fixture.Window, "smoke-reader-i13-formatting-shown.png");
        Console.WriteLine("SMOKE reader I13: dead reference, spoiler (Enter), code, external link and plain reference styles passed.");
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
