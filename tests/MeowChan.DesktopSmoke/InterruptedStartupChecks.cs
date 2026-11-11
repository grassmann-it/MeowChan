using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Persistence;

namespace MeowChan.DesktopSmoke;

// Real application start on a profile with a native import that stopped before its commit: the shell
// shows the recovery dialog, the confirmed discard lets startup reach Ready, and the parent is gone.
internal static class InterruptedStartupChecks
{
    private static string? _parent;

    internal static void Seed(string profile)
    {
        Guid operation = Guid.NewGuid();
        _parent = Path.Combine(profile, "import-media-operations", operation.ToString("N"));
        using (ProfileLease lease = ProfileLease.Acquire(profile))
        {
            MeowChan.Archive.ArchiveCatalog catalog = MeowChan.Archive.ArchiveCatalog.OpenAsync(profile, lease).GetAwaiter().GetResult();
            try
            {
                catalog.WriteAsync((connection, transaction) =>
                {
                    using var insert = ArchiveSql.Command(connection, transaction, """
                        INSERT INTO archive_media_import_operations (operation_id,request_sha256,package_fingerprint,catalog_instance,
                            catalog_generation,source,board,thread,operations_parent,imported_at,state,revision_keys_json,
                            candidate_directory,manifest_generation,bindings_sha256,post_count,attachment_count)
                        VALUES ($op,$hash,$hash,$op,0,'4chan','b','7001',$parent,$at,'Preparing','[]',NULL,NULL,NULL,1,1);
                        """);
                    insert.Parameters.AddWithValue("$op", operation.ToString("D"));
                    insert.Parameters.AddWithValue("$hash", new string('a', 64));
                    insert.Parameters.AddWithValue("$parent", _parent);
                    insert.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
                    return insert.ExecuteNonQuery();
                }).GetAwaiter().GetResult();
            }
            finally { catalog.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }
        Directory.CreateDirectory(Path.Combine(_parent, "partial"));
        File.WriteAllText(Path.Combine(_parent, "partial", "bytes"), "12345");
    }

    internal static async Task RunAsync(MainWindow window, Func<Func<bool>, Task> until)
    {
        string parent = _parent ?? throw new InvalidOperationException("The interrupted profile was not seeded.");
        await until(() => window.InterruptedTransfersDialogForTests?.IsVisible == true);
        InterruptedTransfersDialog dialog = window.InterruptedTransfersDialogForTests!;
        Require(window.SessionHost.State == DesktopSessionState.Starting, "Startup did not wait for the recovery decision.");
        Button discard = dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "InterruptedTransfersDiscard");
        Require(Equals(discard.Content, Texts.T("transfer.interrupted.discard")), "The discard action is not labeled.");
        discard.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await until(() => Equals(discard.Content, Texts.T("transfer.interrupted.confirm", 1, 5L)));
        Require(Directory.Exists(parent), "The first press already discarded.");
        discard.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await window.SessionHost.StartAsync();
        Require(window.SessionHost.State == DesktopSessionState.Ready,
            "Startup did not reach Ready after discarding: " + window.SessionHost.StartupError);
        Require(!Directory.Exists(parent), "The interrupted operations parent still exists.");
        await Dispatcher.UIThread.InvokeAsync(() => Require(!dialog.IsVisible, "The recovery dialog stayed open."));
        Console.WriteLine("SMOKE interrupted startup: real app start -> recovery dialog -> two-step discard -> Ready; parent removed.");
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
