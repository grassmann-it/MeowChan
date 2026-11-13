using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using MeowChan;
using MeowChan.Archive;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Storage;
using MeowChan.Testing;

namespace MeowChan.DesktopSmoke;

// Search group: filename search and SHA-256 media hash search. The draft stays in the fixed
// header across resizing and scrolling; results scroll in the body. Neither dialog has a
// separate error text: request failures are its polite status line, which sits in the header.
internal static partial class DialogFrameChecks
{
    private static readonly TimeSpan SearchDeadline = TimeSpan.FromSeconds(20);

    private static async partial Task RunSearchAsync(MainWindow owner)
    {
        string language = Texts.Language;
        try
        {
            if (!DialogGallery.Active) Texts.SetLanguage("en");
            await FilenameSearchFrameAsync(owner);
            await FilenameSearchFailureFrameAsync(owner);
            await MediaHashSearchFrameAsync(owner);
        }
        finally { Texts.SetLanguage(language); }
    }

    private static async Task FilenameSearchFrameAsync(MainWindow owner)
    {
        string profile = TemporaryWorkspacePaths.CreateDirectory("dialog-frame-filename-");
        // Reuse the existing 205-row synthetic catalog fixture of FilenameSearchChecks.
        MethodInfo seed = typeof(FilenameSearchChecks).GetMethod("SeedAsync", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("FilenameSearchChecks.SeedAsync fixture missing.");
        await (Task)seed.Invoke(null, [profile])!;
        var dialog = new FilenameSearchDialog(token => ArchiveReader.OpenAsync(profile, token));
        Task<ArchiveFilenameHit?> shown = dialog.ShowDialog<ArchiveFilenameHit?>(owner);
        try
        {
            await SearchVisibleAsync(dialog);
            TextBox query = Part<TextBox>(dialog, "FilenameSearchQuery", "filename");
            ListBox results = Part<ListBox>(dialog, "FilenameSearchResults", "filename");
            await VerifyFrameAsync(dialog, "search/filename empty normal");
            query.Text = "w19filename";
            await Settle();
            RequireDraftInHeader(dialog, query, "w19filename", "filename draft");
            await dialog.Session.SearchAsync().WaitAsync(SearchDeadline);
            await SearchWaitAsync(() => dialog.Session.Current.StatusKey == "filename.search.ready" && results.ItemCount == 100);
            Require(results.GetVisualAncestors().Contains(Part<ScrollViewer>(dialog, DialogFrame.BodyName, "filename")),
                "filename: results must live in the scrolling body.");
            Require(Find<Control>(dialog, DialogFrame.ErrorStripName) == null, "filename: unexpected error strip.");
            await VerifyFrameAsync(dialog, "search/filename results normal", "search-filename-normal");
            RequireDraftInHeader(dialog, query, "w19filename", "filename results");
            RequireInsideClient(dialog, results, "filename results normal");
            await SearchResizeAsync(dialog, dialog.MinWidth, dialog.MinHeight);
            await VerifyFrameAsync(dialog, "search/filename results minimum", "search-filename-minimum");
            RequireDraftInHeader(dialog, query, "w19filename", "filename minimum");
            RequireInsideClient(dialog, Part<Button>(dialog, "FilenameSearchSelect", "filename"), "filename select minimum");
            dialog.Width = 820; dialog.Height = 680;
            await Settle();
            Require(!shown.IsCompleted, "filename: layout checks closed the dialog.");
            SearchEscape(dialog);
            Require(await shown.WaitAsync(SearchDeadline) == null, "filename: Escape returned a hit.");
        }
        finally
        {
            if (dialog.IsVisible) dialog.Close();
            await shown.WaitAsync(SearchDeadline);
            await dialog.TerminateAsync().WaitAsync(SearchDeadline);
        }
    }

    private static async Task FilenameSearchFailureFrameAsync(MainWindow owner)
    {
        var dialog = new FilenameSearchDialog(_ => Task.FromException<ArchiveReader>(new InvalidOperationException("synthetic")));
        Task<ArchiveFilenameHit?> shown = dialog.ShowDialog<ArchiveFilenameHit?>(owner);
        try
        {
            await SearchVisibleAsync(dialog);
            TextBox query = Part<TextBox>(dialog, "FilenameSearchQuery", "filename failure");
            query.Text = "kept draft";
            await Settle();
            await dialog.Session.SearchAsync().WaitAsync(SearchDeadline);
            await SearchWaitAsync(() => dialog.Session.Current.StatusKey == "filename.search.failed");
            await Settle();
            TextBlock status = Part<TextBlock>(dialog, "FilenameSearchStatus", "filename failure");
            Require(status.Text == FilenameSearchCatalog.T("filename.search.failed")
                && status.GetVisualAncestors().Contains(Part<Control>(dialog, DialogFrame.HeaderName, "filename failure")),
                "filename: failure status must stay visible in the fixed header.");
            await VerifyFrameAsync(dialog, "search/filename failed normal", "search-filename-failed");
            await SearchResizeAsync(dialog, dialog.MinWidth, dialog.MinHeight);
            await VerifyFrameAsync(dialog, "search/filename failed minimum");
            RequireDraftInHeader(dialog, query, "kept draft", "filename failure");
            RequireInsideClient(dialog, status, "filename failure status minimum");
            Part<Button>(dialog, "FilenameSearchClose", "filename failure").RaiseEvent(
                new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Require(await shown.WaitAsync(SearchDeadline) == null, "filename: Close returned a hit.");
        }
        finally
        {
            if (dialog.IsVisible) dialog.Close();
            await shown.WaitAsync(SearchDeadline);
            await dialog.TerminateAsync().WaitAsync(SearchDeadline);
        }
    }

    private static async Task MediaHashSearchFrameAsync(MainWindow owner)
    {
        string hash = "0" + new string('a', 63);
        MediaAssetReference[] assets = Enumerable.Range(0, 120).Select(index => new MediaAssetReference(
            "asset-frame-" + index, "media-frame-" + index, index % 2 == 0 ? "original" : "preview", "synthetic.bin",
            "/synthetic-never-opened/" + index, 7, hash, "https://example.invalid/synthetic", "synthetic.bin", index % 3 == 0)).ToArray();
        var snapshot = new MediaAssetSnapshot("/synthetic-never-opened", MediaManifestStatus.Available,
            4, "generation-frame", assets, "synthetic");
        var dialog = new MediaHashSearchDialog(snapshot);
        Task<MediaAssetReference?> shown = dialog.ShowDialog<MediaAssetReference?>(owner);
        try
        {
            await SearchVisibleAsync(dialog);
            TextBox input = Part<TextBox>(dialog, "MediaHashSearchInput", "hash");
            await VerifyFrameAsync(dialog, "search/hash empty normal");
            // Invalid draft: the status line reports it in the header and the draft is kept.
            input.Text = "not-a-hash";
            await Settle();
            await dialog.Operation.SearchAsync(input.Text).WaitAsync(SearchDeadline);
            await SearchWaitAsync(() => dialog.Operation.Current.Status == MediaHashSearchOperation.Status.Invalid);
            await Settle();
            TextBlock status = Part<TextBlock>(dialog, "MediaHashSearchStatus", "hash");
            Require(status.Text == MediaHashSearchCatalog.T("mediahash.invalid", 0)
                && status.GetVisualAncestors().Contains(Part<Control>(dialog, DialogFrame.HeaderName, "hash")),
                "hash: invalid status must stay visible in the fixed header.");
            RequireDraftInHeader(dialog, input, "not-a-hash", "hash invalid");
            await VerifyFrameAsync(dialog, "search/hash invalid normal", "search-hash-invalid");
            input.Text = hash.ToUpperInvariant();
            await Settle();
            await dialog.Operation.SearchAsync(input.Text).WaitAsync(SearchDeadline);
            StackPanel rows = Part<StackPanel>(dialog, "MediaHashSearchResults", "hash");
            await SearchWaitAsync(() => dialog.Operation.Current.Status == MediaHashSearchOperation.Status.Available && rows.Children.Count == 100);
            Require(rows.GetVisualAncestors().Contains(Part<ScrollViewer>(dialog, DialogFrame.BodyName, "hash")),
                "hash: results must live in the scrolling body.");
            Require(Find<Control>(dialog, DialogFrame.ErrorStripName) == null, "hash: unexpected error strip.");
            await VerifyFrameAsync(dialog, "search/hash results normal", "search-hash-normal");
            RequireDraftInHeader(dialog, input, hash.ToUpperInvariant(), "hash results");
            await SearchResizeAsync(dialog, dialog.MinWidth, dialog.MinHeight);
            await VerifyFrameAsync(dialog, "search/hash results minimum", "search-hash-minimum");
            RequireDraftInHeader(dialog, input, hash.ToUpperInvariant(), "hash minimum");
            foreach (string name in new[] { "MediaHashSearchStart", "MediaHashSearchPrevious", "MediaHashSearchNext", "MediaHashSearchClose" })
                RequireInsideClient(dialog, Part<Button>(dialog, name, "hash"), "hash minimum " + name);
            dialog.Width = 760; dialog.Height = 640;
            await Settle();
            Require(!shown.IsCompleted, "hash: layout checks closed the dialog.");
            SearchEscape(dialog);
            Require(await shown.WaitAsync(SearchDeadline) == null, "hash: Escape returned an asset.");
        }
        finally
        {
            if (dialog.IsVisible) dialog.Close();
            await shown.WaitAsync(SearchDeadline);
            await dialog.TerminateAsync().WaitAsync(SearchDeadline);
        }
    }

    private static void RequireDraftInHeader(Window dialog, TextBox draft, string expected, string label)
    {
        Require(draft.Text == expected, label + ": draft changed to '" + draft.Text + "'.");
        Require(draft.GetVisualAncestors().Contains(Part<Control>(dialog, DialogFrame.HeaderName, label)),
            label + ": draft is not in the fixed header.");
        RequireInsideClient(dialog, draft, label);
    }

    private static void RequireInsideClient(Window dialog, Control control, string label)
    {
        Avalonia.Point? origin = control.TranslatePoint(default, dialog);
        Require(control.IsEffectivelyVisible && origin is { } p && control.Bounds.Width > 0 && control.Bounds.Height > 0
            && p.X >= -1 && p.Y >= -1 && p.X + control.Bounds.Width <= dialog.ClientSize.Width + 1
            && p.Y + control.Bounds.Height <= dialog.ClientSize.Height + 1,
            label + ": " + control.Name + " outside client " + control.Bounds + " / " + dialog.ClientSize);
    }

    private static async Task SearchVisibleAsync(Window dialog)
    {
        await SearchWaitAsync(() => dialog.IsVisible && dialog.TryGetPlatformHandle()?.Handle is nint handle && handle != 0);
        dialog.Activate();
        await Settle();
    }

    private static async Task SearchResizeAsync(Window dialog, double width, double height)
    {
        dialog.Width = width; dialog.Height = height;
        await SearchWaitAsync(() => Math.Abs(dialog.ClientSize.Width - width) <= 1 && Math.Abs(dialog.ClientSize.Height - height) <= 1);
        await Settle();
    }

    private static void SearchEscape(Window dialog) => dialog.RaiseEvent(new KeyEventArgs
    {
        RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape, KeyModifiers = KeyModifiers.None
    });

    private static Task SearchWaitAsync(Func<bool> predicate) => Program.WaitUntilAsync(predicate);
}
