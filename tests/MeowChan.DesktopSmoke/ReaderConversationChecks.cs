using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Testing;

namespace MeowChan.DesktopSmoke;

/// <summary>
/// P4B's native fixture: the session toggle, depth clamping, depth-first order, duplicate reply-toggle suppression and
/// indentation in Fit/Full/Column at the two acceptance widths. The caller registers this helper in the shared smoke
/// registry; this file deliberately does not touch that parent-owned registry.
/// </summary>
internal static class ReaderConversationChecks
{
    private const string ThreadId = "700";

    internal static async Task RunAsync(MainWindow owner)
    {
        string profile = Path.Combine(TemporaryWorkspacePaths.TempRoot, "meowchan-reader-conversation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        ResourceId id = new("4chan", "g", ThreadId);
        await catalog.CaptureAsync(new CapturedThreadSnapshot(id, "https://boards.4chan.org/g/thread/" + ThreadId,
            "Conversation fixture", ThreadSourceState.Active, DateTimeOffset.UtcNow, profile, Posts()));

        ArchivePage page = new(token => ArchiveReader.OpenAsync(profile, token), (_, _) => Task.CompletedTask, _ => { });
        Control pane = page.DetachReader();
        Window window = new() { Width = 1440, Height = 900, Content = pane, Title = "Private conversation reader" };
        try
        {
            window.Show(owner);
            page.IsAvailable = true;
            await page.LoadThreadAsync(id);
            await Program.WaitUntilAsync(() => !page.IsBusy && page.FindControl<ListBox>("ArchivePosts") is { ItemCount: > 0 });

            Require(page.ReaderThreadingDepth == ReaderConversationTree.DepthDefault, "Conversation depth did not start at 6.");
            Require(page.ToggleReaderViewOption("reader.view.conversation"), "Conversation was not registered in the View menu.");
            await page.WaitForReaderViewAsync();
            CheckTree(page, expectedDepth: 6);
            Require(page.FindControl<ListBox>("ArchivePosts")!.ItemsSource!.Cast<ArchivePostRow>().All(row => row.SuppressReplyToggle),
                "Conversation mode left duplicate reply toggles visible.");

            page.ReaderThreadingDepth = 1;
            await page.WaitForReaderViewAsync();
            CheckTree(page, expectedDepth: 1);
            page.ReaderThreadingDepth = 16;
            await page.WaitForReaderViewAsync();
            CheckTree(page, expectedDepth: 16);

            foreach (double width in new[] { 900d, 1440d })
            {
                window.Width = width;
                await Program.WaitUntilAsync(() => Math.Abs(window.Bounds.Width - width) < 2);
                foreach (string mode in new[] { Config.ReaderPostWidthFit, Config.ReaderPostWidthFull, Config.ReaderPostWidthColumn })
                {
                    page.ReaderPostWidthPreference = mode;
                    await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
                    CheckIndentation(page, width, mode);
                    Program.CaptureWindow(window, $"smoke-reader-conversation-{mode.ToLowerInvariant()}-{width:0}.png");
                }
            }
            Console.WriteLine("SMOKE reader conversation: toggle, depth 1/6/16, order, reply-toggle suppression and Fit/Full/Column indentation at 900/1440 passed.");
        }
        finally
        {
            page.EndLifetime();
            window.Close();
            Directory.Delete(profile, recursive: true);
        }
    }

    // 700 OP, 701 independent root, then a long line below 701. 722 is another root; 723 quotes both branches and stays root.
    private static CapturedPost[] Posts()
    {
        return Enumerable.Range(0, 24).Select(index =>
        {
            string id = (700 + index).ToString();
            string html = index switch
            {
                0 or 1 or 22 => "<p>conversation fixture " + id + "</p>",
                23 => "<p><a href=\"#p702\" class=\"quotelink\">&gt;&gt;702</a> <a href=\"#p722\" class=\"quotelink\">&gt;&gt;722</a></p>",
                _ => "<p><a href=\"#p" + (index + 699) + "\" class=\"quotelink\">&gt;&gt;" + (index + 699) + "</a></p>"
            };
            return new CapturedPost(id, index, PostFieldPresence.All, PostedAt: DateTimeOffset.UnixEpoch.AddMinutes(index), Html: html);
        }).ToArray();
    }

    private static void CheckTree(ArchivePage page, int expectedDepth)
    {
        ArchivePostRow[] rows = page.FindControl<ListBox>("ArchivePosts")!.ItemsSource!.Cast<ArchivePostRow>().ToArray();
        Require(rows.Length == 24 && rows[0].Id == "700", "Conversation order did not retain the opening post.");
        Require(rows.Select(row => row.ThreadIndex).SequenceEqual(Enumerable.Range(0, 24)), "Conversation order is not depth-first.");
        Require(rows.Max(row => row.Depth) == Math.Min(expectedDepth, 16), "Conversation depth was not clamped/applied.");
        Require(rows[^1].Depth == 0, "The mixed sibling quote did not stay at the root.");
    }

    private static void CheckIndentation(ArchivePage page, double width, string mode)
    {
        ListBox list = page.FindControl<ListBox>("ArchivePosts")!;
        double limit = ReaderNavigationLayout.ConversationIndent(ReaderConversationTree.DepthMaximum, list.Bounds.Width) + 6;
        foreach (ListBoxItem item in list.GetVisualDescendants().OfType<ListBoxItem>().Where(item => item.DataContext is ArchivePostRow { Depth: > 0 }))
            Require(item.Margin.Left <= limit + 1, $"{mode} at {width:0} consumed the readable/navigation gutter.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
