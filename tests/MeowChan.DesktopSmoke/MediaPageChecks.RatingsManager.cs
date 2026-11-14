using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Commands;
using MeowChan.Gallery;
using MeowChan.Interfaces;

namespace MeowChan.DesktopSmoke;

// AP6: the saved-ratings manager belongs to the media (Media page, Archive rating row, gallery menu), not to Diagnostics.
internal static partial class MediaPageChecks
{
    private static async Task RatingsManagerEntryAsync(MainWindow owner, string profile)
    {
        await using DesktopSessionHost host = new(profile, Array.Empty<IImageBoard>());
        await host.StartAsync().WaitAsync(Deadline);
        MediaPage page = new(host.OpenArchiveReaderAsync, cachePath: Path.Combine(profile, "cache", MediaLibraryIndexCache.FileName));
        Window window = new() { Width = 1100, Height = 800, Content = page, Title = "Synthetic media page ratings", ShowInTaskbar = false };
        try
        {
            window.Show(owner);
            page.IsAvailable = true;
            Button manage = page.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "MediaManageRatings");
            Require(!manage.IsEnabled && !page.CanManageRatings, "The ratings manager entry is enabled although the shell supplied no dialog.");
            int opened = 0;
            page.ManageMediaRatingsAsync = () => { opened++; return Task.CompletedTask; };
            Require(manage.IsEnabled && page.CanManageRatings, "The ratings manager entry did not enable once the shell supplied the dialog.");
            string label = CommandCatalog.Label("media.rating.manage");
            Require(ToolTip.GetTip(manage)?.ToString()?.StartsWith(label, StringComparison.Ordinal) == true,
                "The ratings manager entry does not carry the command's label in its tooltip: " + ToolTip.GetTip(manage));
            manage.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Program.WaitUntilAsync(() => opened == 1);
            // The command has no default key (it is bound by the user, like before) and is no longer a Diagnostics command.
            Require(CommandCatalog.Get("media.rating.manage").Scope.HasFlag(CommandScope.Media)
                && !CommandCatalog.Get("media.rating.manage").Scope.HasFlag(CommandScope.Diagnostics),
                "The ratings manager command is still scoped to Diagnostics.");
            DiagnosticsPage diagnostics = new();
            Require(diagnostics.FindControl<Button>("ManageMediaRatingsButton") == null, "The Diagnostics page still offers the ratings manager.");
            // Language switch keeps the entry labelled.
            string before = Texts.Language;
            Texts.SetLanguage(before == "de" ? "en" : "de");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Require(ToolTip.GetTip(manage)?.ToString()?.StartsWith(CommandCatalog.Label("media.rating.manage"), StringComparison.Ordinal) == true,
                "The ratings manager entry kept the old language label.");
            Texts.SetLanguage(before);
            page.EndLifetime();
            Require(!page.CanManageRatings, "An ended media page still offers the ratings manager.");
            Console.WriteLine("SMOKE media page ratings manager: the Media page opens the manager through its own entry (disabled without a dialog, relabelled on language change, off after the page ended); Diagnostics no longer carries it.");
        }
        finally { page.EndLifetime(); window.Close(); }
    }
}
