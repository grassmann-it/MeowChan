using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Interfaces;
using MeowChan.Testing;

namespace MeowChan.DesktopSmoke;

// Dialog gallery scene "media-page": the Media page over the selection fixture (five bound files, one byte-identical pair).
// Opens the occurrences window of the pair and "Tag together" for a selection; both close with the window close, nothing is
// tagged or exported.
internal static partial class MediaPageChecks
{
    internal static async Task CaptureForDialogGalleryAsync(MainWindow owner)
    {
        string profile = TemporaryWorkspacePaths.CreateDirectory("meowchan-gallery-media-");
        try
        {
            new Config().SaveConfig(profile);
            await SeedSelectionAsync(profile);
            await using DesktopSessionHost host = new(profile, Array.Empty<IImageBoard>());
            await host.StartAsync().WaitAsync(Deadline);
            MediaPage page = new(host.OpenArchiveReaderAsync)
            {
                ReadUserData = () => UserData(host),
                OpenGallery = (_, _) => Task.CompletedTask,
                TagServices = new(host.GetPublishedPrivateTagSnapshot,
                    (name, subjects, revision, token) => host.DefineAndSetMediaSubjectTagsAsync(name, subjects, revision, token),
                    (tag, subjects, assigned, revision, token) => host.SetMediaSubjectTagsAsync(tag, subjects, assigned, revision, token))
            };
            Window window = new() { Width = 1300, Height = 860, Content = page, Title = "Media", ShowInTaskbar = false };
            try
            {
                window.Show(owner);
                page.IsAvailable = true;
                await page.ActivateAsync().WaitAsync(Deadline);
                Require(page.Tiles.Count == 4, "The selection fixture must give four tiles.");

                Button hint = await FindDuplicatesHintAsync(page);
                ClickRaise(hint);
                Window details = page.OccurrencesWindow ?? throw new InvalidOperationException("The hint did not open the occurrences.");
                await UntilAsync(() => details.GetVisualDescendants().OfType<StackPanel>().Count(row => row.Name == "MediaOccurrence") == 2);
                await DialogGalleryChecks.RecordWindowAsync(details, "media-occurrences-window");
                details.Close();
                await UntilAsync(() => page.OccurrencesWindow == null);

                await ClickTileAsync(page, window, 0);
                await ClickTileAsync(page, window, 2, KeyModifiers.Shift);
                ClickRaise(C<Button>(page, "MediaSelectionTag"));
                await UntilAsync(() => page.SelectionTagPopover is { IsOpen: true, Surface: not null });
                page.SelectionTagPopover!.Surface!.AdvancedButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
                await UntilAsync(() => page.SelectionTagWindow != null);
                MediaSelectionTagDialog dialog = (MediaSelectionTagDialog)page.SelectionTagWindow!;
                await UntilAsync(() => dialog.OccurrenceBoxes.Count >= 3);
                await DialogGalleryChecks.RecordWindowAsync(dialog, "media-tag-together-dialog");
                dialog.Close();
                await UntilAsync(() => page.SelectionTagWindow == null);
            }
            finally { page.EndLifetime(); window.Close(); }
        }
        finally { try { Directory.Delete(profile, recursive: true); } catch (IOException) { } }
    }
}
