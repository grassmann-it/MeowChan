using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Testing;

namespace MeowChan.DesktopSmoke;

// MEOWCHAN_MEDIA_PAGE_ONLY: focused composition checks for the one production MediaPage
// owner. Managed routed input only; no native desktop or screen-reader claim.
internal static class MediaMenuCompositionChecks
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    internal static async Task RunAsync(MainWindow owner)
    {
        string profile = TemporaryWorkspacePaths.CreateDirectory("media-menu-composition-");
        new Config { DownloadsPaused = true, SaveUrlsOnExit = false, CloseToTray = false,
            MinimizeToTray = false, WorkspaceMode = "Simple" }.SaveConfig(profile);
        await MediaRatingFixture.SeedAsync(profile);
        await using DesktopSessionHost host = new(profile, Array.Empty<IImageBoard>());
        await host.StartAsync().WaitAsync(Deadline);
        List<(string Id, MediaLibraryEntry Entry)> calls = new();
        MediaPage page = new(host.OpenArchiveReaderAsync)
        {
            ReadUserData = () => new(host.GetPublishedPrivateTagSnapshot().Snapshot, host.GetFavorites(), host.GetMediaRatings()),
            ExecuteMediaMenuActionAsync = (id, entry, current) =>
            {
                Require(current(), "A menu action reached the test host after its presentation became stale.");
                calls.Add((id, entry));
                return Task.CompletedTask;
            }
        };
        page.InitializeSharedMediaMenus();
        Window window = new() { Width = 1200, Height = 850, Content = page, ShowInTaskbar = false };
        try
        {
            window.Show(owner);
            page.IsAvailable = true;
            await page.ActivateAsync().WaitAsync(Deadline);
            MediaLibraryTileRow[] rows = page.Grid.ItemsSource!.Cast<MediaLibraryTileRow>().Take(2).ToArray();
            Require(rows.Length == 2, "The menu fixture needs two media tiles.");
            MediaLibraryTileRow a = rows[0], b = rows[1];
            page.Grid.SelectedItem = a;
            page.Grid.ScrollIntoView(b.Index);
            await UntilAsync(() => page.Grid.ContainerFromIndex(b.Index) is Control);
            Control bControl = (Control)page.Grid.ContainerFromIndex(b.Index)!;

            bControl.Focus();
            using (Pointer pointer = new(Pointer.GetNextFreeId(), PointerType.Mouse, true))
            {
                bControl.RaiseEvent(new PointerPressedEventArgs(bControl, pointer, window, default, 0,
                    new PointerPointProperties(RawInputModifiers.RightMouseButton, PointerUpdateKind.RightButtonPressed),
                    KeyModifiers.None));
                pointer.Capture(null);
            }
            ContextMenu bMenu = page.MediaMenu ?? throw new InvalidOperationException("The tile did not open the shared media menu.");
            Require(ReferenceEquals(bMenu, bControl.ContextMenu), "The menu was not attached to the exact requested tile control.");
            Require(Item(bMenu, "media.rating.edit").IsEnabled, "A bound media occurrence lost its enabled Rate and tag action.");
            Execute(bMenu, "media.rating.edit");
            Require(calls.Count == 1 && calls[0].Id == "media.rating.edit" && ReferenceEquals(calls[0].Entry, b.Entry)
                && ReferenceEquals(page.Grid.SelectedItem, a), "Right-clicking B while A is selected acted on the selected row.");
            bMenu.Close();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Require(window.FocusManager?.GetFocusedElement() is Control focused
                && (ReferenceEquals(focused, bControl) || focused.GetVisualAncestors().Contains(bControl)),
                "Closing the menu did not restore focus to the requested tile.");

            foreach ((Key Key, KeyModifiers Modifiers) in new[] { (Key.Apps, KeyModifiers.None), (Key.F10, KeyModifiers.Shift) })
            {
                calls.Clear();
                bControl.Focus();
                KeyEventArgs key = new() { RoutedEvent = InputElement.KeyDownEvent, Key = Key, KeyModifiers = Modifiers };
                bControl.RaiseEvent(key);
                Require(key.Handled && page.MediaMenu?.IsOpen == true, "The keyboard context gesture did not open the shared menu.");
                Execute(page.MediaMenu!, "media.rating.edit");
                Require(calls.Count == 1 && ReferenceEquals(calls[0].Entry, b.Entry),
                    "The keyboard context gesture followed A instead of the focused B tile.");
                page.MediaMenu!.Close();
            }

            calls.Clear();
            page.Grid.SelectedItem = a;
            page.Grid.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent });
            Require(page.MediaMenu?.IsOpen == true && ReferenceEquals(page.Grid.ContextMenu, page.MediaMenu),
                "A positionless synthetic grid request did not attach a menu to the grid.");
            Execute(page.MediaMenu!, "media.rating.edit");
            Require(calls.Count == 1 && ReferenceEquals(calls[0].Entry, a.Entry),
                "A positionless grid request did not use the grid's selected occurrence.");

            MenuItem stale = Item(page.MediaMenu!, "media.rating.edit");
            page.MediaMenu!.Close();
            page.GetVisualDescendants().OfType<ComboBox>().Single(control => control.Name == "MediaSort").SelectedIndex = 1;
            stale.Command!.Execute(null);
            Require(calls.Count == 1, "A filter/rebuild revived a frozen menu callback.");

            MediaLibraryEntry indexed = page.Snapshot!.Entries.First(entry => entry.Presence == MediaLibraryPresence.Present);
            GalleryEntry resolved = await MediaMenuIdentity.ResolveAsync(indexed, host.OpenArchiveReaderAsync,
                MediaAssetReader.OpenAsync, () => true, default)
                ?? throw new InvalidOperationException("The exact indexed occurrence did not resolve.");
            Require(MediaMenuIdentity.Matches(indexed, resolved), "The menu resolver did not retain the exact occurrence identity.");
            Require(!MediaMenuIdentity.Matches(indexed with { Sha256 = new string('0', 64) }, resolved),
                "The resolver treated a hash-only replacement as the same menu target.");

            page.EndLifetime();
            Console.WriteLine("SMOKE media menu composition: one page owner, tile/grid origin resolution, keyboard parity, native stale-callback rejection, focus restoration and exact occurrence admission checked.");
        }
        finally
        {
            page.MediaMenu?.Close();
            page.EndLifetime();
            window.Close();
        }
    }

    private static MenuItem Item(ContextMenu menu, string id) => menu.Items.OfType<MenuItem>()
        .Single(item => Equals(item.Tag, id));

    private static void Execute(ContextMenu menu, string id)
    {
        MenuItem item = Item(menu, id);
        Require(item.Command?.CanExecute(item.CommandParameter) == true, id + " was not executable.");
        item.Command!.Execute(item.CommandParameter);
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + Deadline;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Timed out waiting for media menu composition state.");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            await Task.Yield();
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
