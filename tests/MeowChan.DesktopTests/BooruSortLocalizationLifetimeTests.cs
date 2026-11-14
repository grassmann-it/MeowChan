using System;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using MeowChan.Archive;
using MeowChan.Gallery;

namespace MeowChan.DesktopTests;

// Regression coverage for the real MediaPage sort-selection subscription. The page is created without a
// native Window; scoped Skia serves compiled icon bitmaps, while the real ComboBox ItemsSource/SelectedIndex changes raise
// Avalonia SelectionChanged events through the production InstallReturnInterruption subscription.
[Collection("Booru exact navigation")]
public sealed class BooruSortLocalizationLifetimeTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void ProgrammaticSortSyncAndRestoreStayValidButAUserSelectionRevokesTheHandoff() => OnDispatcher(() =>
    {
        MediaPage page = new(static _ => Task.FromException<ArchiveReader>(
            new InvalidOperationException("The sort-lifetime fixture must not open an archive.")));
        CancellationTokenSource lifetime = Get<CancellationTokenSource>(page, "_lifetime");
        try
        {
            ComboBox sort = Get<ComboBox>(page, "_sort");
            sort.ItemsSource = new[] { "initial-0", "initial-1", "initial-2", "initial-3", "initial-4", "initial-5", "initial-6", "initial-7", "initial-8" };
            sort.SelectedIndex = 0;

            int selectionEvents = 0;
            sort.SelectionChanged += (_, _) => selectionEvents++;

            Func<bool> localizationGuard = page.CaptureBooruHandoffInteraction();
            Set(page, "_localizing", true);
            // This mirrors LocalizeSortControls: replace the source and then select the relabelled item.
            sort.ItemsSource = new[] { "localized-0", "localized-1", "localized-2", "localized-3", "localized-4", "localized-5", "localized-6", "localized-7", "localized-8" };
            sort.SelectedIndex = 1;
            sort.ItemsSource = new[] { "localized-again-0", "localized-again-1", "localized-again-2", "localized-again-3", "localized-again-4", "localized-again-5", "localized-again-6", "localized-again-7", "localized-again-8" };
            sort.SelectedIndex = 2;
            Dispatcher.UIThread.RunJobs();
            Assert.True(selectionEvents > 0, "The localization path must exercise a real SelectionChanged event.");
            Assert.True(localizationGuard(), "Programmatic ItemsSource/SelectedIndex changes during localization are not takeover.");

            Set(page, "_localizing", false);
            Dispatcher.UIThread.RunJobs();
            Assert.True(localizationGuard(), "No deferred localization event may become a later user interaction.");
            Func<bool> restoreGuard = page.CaptureBooruHandoffInteraction();
            int beforeRestore = selectionEvents;
            // Use the production RestoreSort scope rather than duplicating its suppression flag in the test.
            page.RestoreSort(MediaLibrarySort.Name, MediaLibrarySortDirection.Default);
            Dispatcher.UIThread.RunJobs();
            Assert.True(selectionEvents > beforeRestore, "RestoreSort must exercise the existing ComboBox subscription.");
            Assert.True(restoreGuard(), "RestoreSort's programmatic SelectedIndex change is not takeover.");

            Func<bool> userGuard = page.CaptureBooruHandoffInteraction();
            int beforeUserChange = selectionEvents;
            int restoredIndex = sort.SelectedIndex;
            sort.SelectedIndex = restoredIndex == 0 ? 1 : 0;
            Dispatcher.UIThread.RunJobs();
            Assert.True(selectionEvents > beforeUserChange, "The user-path probe must exercise SelectionChanged.");
            Assert.False(userGuard(), "A real post-restore selection change must revoke the handoff.");
        }
        finally
        {
            try { page.EndLifetime(); }
            finally { lifetime.Dispose(); }
        }
    });

    private static T Get<T>(MediaPage page, string field) =>
        (T)typeof(MediaPage).GetField(field, Private)!.GetValue(page)!;

    private static void Set(MediaPage page, string field, object value) =>
        typeof(MediaPage).GetField(field, Private)!.SetValue(page, value);

    private static void OnDispatcher(Action test) => BooruComponentDispatcher.Run(test);
}
