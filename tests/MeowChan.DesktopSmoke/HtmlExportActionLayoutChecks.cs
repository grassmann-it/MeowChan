using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Testing;
using ArchiveCatalog = MeowChan.Archive.ArchiveCatalog;

namespace MeowChan.DesktopSmoke;

// Native host, production ArchivePage dialog and templates; managed actions, not OS keyboard input.
internal static class HtmlExportActionLayoutChecks
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);
    private static readonly (string Name, string Key)[] Actions =
    [
        ("ArchiveExportLegacyFind", "export.legacyFind"),
        ("ArchiveExportLegacyAssociate", "export.legacyAssociate"),
        ("ArchiveExportPrevious", "export.previous"),
        ("ArchiveExportAssociate", "export.associate")
    ];
    // Failed settlement must not abandon the lease or its page to GC.
    private static readonly List<object> RetainedOwnership = new();

    internal static async Task RunAsync(MainWindow owner)
    {
        Dispatcher.UIThread.VerifyAccess();
        Require(owner.IsVisible, "Visible native owner required.");
        string language = Texts.Language;
        string root = TemporaryWorkspacePaths.CreateDirectory("html-export-action-layout-");
        string profile = Path.Combine(root, "profile");
        string exports = Path.Combine(root, "exports");
        string media = Path.Combine(root, "synthetic-media");
        Directory.CreateDirectory(exports);
        Directory.CreateDirectory(media);
        ProfileLease? lease = null;
        ArchivePage? page = null;
        Window? host = null;
        Task? pendingOpen = null;
        Exception? failure = null;
        Console.WriteLine("SMOKE HTML action layout fixture: " + root);
        try
        {
            lease = ProfileLease.Acquire(profile);
            ResourceId first = new("html-action-layout", "local", "101");
            ResourceId second = new("html-action-layout", "local", "202");
            await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease))
                foreach (ResourceId id in new[] { first, second })
                    await catalog.CaptureAsync(new CapturedThreadSnapshot(id,
                        "https://offline.invalid/local/" + id.Thread, "Synthetic text-only layout fixture",
                        ThreadSourceState.Archived, DateTimeOffset.UtcNow, media,
                        [new CapturedPost(id.Thread!, 0, PostFieldPresence.All, Html: "<p>Local synthetic text only.</p>")]));
            ExportHistoryStore singleHistory = new(profile, lease);
            CollectionExportHistoryStore collectionHistory = new(profile, lease);
            string singleDestination = Path.Combine(exports, "prior-single");
            string collectionDestination = Path.Combine(exports, "prior-collection");
            // Supported exporters publish genuine identity-bearing markers. Await every writer
            // and history association before opening UI; no forged history JSON or visibility.
            await using (ArchiveReader reader = await ArchiveReader.OpenAsync(profile))
            {
                var single = await ArchiveHtmlExporter.ExportAsync(reader, first, singleDestination,
                    new ArchiveHtmlExportOptions(IncludeMedia: false));
                var singleEntry = await singleHistory.AssociateAsync(single.Directory, first);
                Require(single.Metadata != null && single.Metadata.Thread == first
                    && singleEntry.Thread == first && singleEntry.ExportId == single.Metadata.ExportId
                    && singleEntry.Generation == single.Metadata.Generation,
                    "Single history seed identity mismatch.");
                var group = await ArchiveCollectionHtmlExporter.ExportAsync(reader, [first, second], collectionDestination,
                    new ArchiveHtmlExportOptions(IncludeMedia: false));
                var groupEntry = await collectionHistory.AssociateAsync(group.Directory, [first, second]);
                Require(group.Metadata != null && groupEntry.Members.SequenceEqual(new[] { first, second })
                    && group.Metadata.Members.Select(member => member.Thread).SequenceEqual(new[] { first, second })
                    && groupEntry.ExportId == group.Metadata.ExportId && groupEntry.Generation == group.Metadata.Generation,
                    "Collection history seed member/marker identity mismatch.");
            }
            Require((await singleHistory.ListVerifiedAsync(first)).Single().Inspection != null,
                "Single prior export did not verify.");
            Require((await collectionHistory.ListVerifiedAsync([first, second])).Single().Inspection != null,
                "Collection prior export did not verify.");
            // No activation/load/media fetch; only the real local export-dialog opening paths.
            page = new ArchivePage(token => ArchiveReader.OpenAsync(profile, token),
                (_, _) => throw new InvalidOperationException("Unexpected settings mutation."),
                _ => throw new InvalidOperationException("External navigation forbidden."))
            {
                IsAvailable = true,
                LastExportDirectoryPreference = exports,
                ExportHistory = singleHistory,
                CollectionExportHistory = collectionHistory
            };
            host = new Window { Title = "HTML action layout — synthetic offline", Width = 640, Height = 800, Content = page };
            host.Show(owner);
            foreach (bool collection in new[] { false, true })
            {
                pendingOpen = collection ? page.OpenExportForThreadsAsync([first, second], null)
                    : page.OpenExportForThreadAsync(first, null);
                await pendingOpen.WaitAsync(Deadline);
                pendingOpen = null;
                Require(Need<Border>(page, "ArchiveExportDialog").IsVisible, "Production dialog did not open.");
                Need<CheckBox>(page, "ArchiveExportMedia").IsChecked = false;
                var sceneActions = collection ? Actions.Where(action => action.Name is
                    "ArchiveExportPrevious" or "ArchiveExportAssociate").ToArray() : Actions;
                string priorDestination = collection ? collectionDestination : singleDestination;
                if (!collection) Need<Expander>(page, "ArchiveExportLegacySection").IsExpanded = true;
                await SettleAsync(host);
                CheckScene(page, collection, priorDestination);
                var labels = sceneActions.ToDictionary(action => action.Name, action =>
                    Need<Button>(page, action.Name).Content as TextBlock
                    ?? throw new InvalidOperationException(action.Name + ": production TextBlock missing."));
                foreach (double width in new[] { 640d, 480d })
                {
                    host.Width = width;
                    await Program.WaitUntilAsync(() => PresentationChecks.MatchesRequestedSize(host, width, 800));
                    await SettleAsync(host);
                    // Never silently measure a wider/clamped host as the requested narrow scene.
                    Require(Math.Abs(host.ClientSize.Width - width) <= 1,
                        $"Native host did not support requested {width} DIP; actual {host.ClientSize.Width}. No layout PASS.");
                    foreach (string code in new[] { "es", "fr", "ru", "es" })
                    {
                        Texts.SetLanguage(code);
                        await page.ReaderLocalizationTask.WaitAsync(Deadline);
                        Require(Texts.Language == code && Texts.Warnings.Count == 0, "Bundled locale unavailable: " + code);
                        await SettleAsync(host);
                        CheckScene(page, collection, priorDestination);
                        foreach (var action in sceneActions)
                        {
                            Button button = Need<Button>(page, action.Name);
                            Require(ReferenceEquals(button.Content, labels[action.Name]), action.Name + ": language refresh replaced presenter.");
                            button.BringIntoView();
                            await SettleAsync(host);
                            Check(button, labels[action.Name], host, Texts.T(action.Key),
                                $"{(collection ? "collection" : "single")}/{width}/{code}/{action.Name}");
                        }
                    }
                }
                SmokeActionClick.Execute(Need<Button>(page, "ArchiveExportClose"));
                Require(!Need<Border>(page, "ArchiveExportDialog").IsVisible, "Dialog did not close.");
            }
        }
        catch (Exception error) { failure = error; }
        finally
        {
            try
            {
                page?.EndLifetime();
                if (pendingOpen != null) await pendingOpen.WaitAsync(Deadline);
                if (page != null)
                {
                    await page.ReaderLocalizationTask.WaitAsync(Deadline);
                    Require(!page.IsExportRunning, "Unexpected export writer still active; retain ownership.");
                }
                if (host != null) { host.Content = null; host.Close(); }
                lease?.Dispose();
                // Root owns filesystem cleanup. Retain only this synthetic fixture, never delete unrelated data.
            }
            catch (Exception cleanup)
            {
                RetainedOwnership.Add(new { root, page, host, lease, pendingOpen });
                failure = failure == null ? cleanup : new AggregateException(failure, cleanup);
            }
            Texts.SetLanguage(language);
            owner.Activate();
            Console.WriteLine("SMOKE HTML action layout retained synthetic fixture: " + root);
        }
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        Console.WriteLine("SMOKE HTML action layout PASS: single four actions/collection two history actions with legacy hidden, 640/480 DIP, ES/FR/RU; managed actions only.");
    }

    private static void CheckScene(ArchivePage page, bool collection, string priorDestination)
    {
        ComboBox history = Need<ComboBox>(page, "ArchiveExportHistory");
        Require(history.IsEffectivelyVisible && history.SelectedIndex == 0 && history.SelectedItem != null
            && history.SelectedItem.ToString()!.EndsWith(" · " + priorDestination, StringComparison.Ordinal),
            "Expected verified prior export is not selected in the production history control.");
        Button previous = Need<Button>(page, "ArchiveExportPrevious");
        Require(previous.IsEffectivelyVisible && previous.IsEffectivelyEnabled,
            "Verified history must expose an enabled Previous action.");
        Require(Need<Button>(page, "ArchiveExportAssociate").IsEffectivelyVisible,
            "History association action unexpectedly hidden.");
        Expander legacy = Need<Expander>(page, "ArchiveExportLegacySection");
        Require(legacy.IsVisible == !collection, "Legacy section visibility differs from production scene policy.");
        foreach (string name in new[] { "ArchiveExportLegacyFind", "ArchiveExportLegacyAssociate" })
            Require(Need<Button>(page, name).IsEffectivelyVisible == !collection,
                name + ": unexpected single/collection visibility.");
    }

    private static void Check(Button button, TextBlock label, Window host, string expected, string scene)
    {
        ContentPresenter presenter = label.GetVisualAncestors().OfType<ContentPresenter>()
            .FirstOrDefault() ?? throw new InvalidOperationException(scene + ": missing actual button content presenter");
        Require(presenter.GetVisualAncestors().Contains(button), scene + ": presenter is not inside button");
        var layout = label.TextLayout;
        string rendered = string.Concat(layout.TextLines.SelectMany(line => line.TextRuns)
            .Where(run => run is not TextEndOfLine).Select(run => run.Text.ToString()));
        Console.WriteLine("SMOKE HTML action geometry " + JsonSerializer.Serialize(new
        {
            scene, expected, rendered, hostWidth = host.ClientSize.Width, scale = host.RenderScaling,
            button = button.Bounds.ToString(), label = label.Bounds.ToString(), presenter = presenter.Bounds.ToString(),
            layout.MaxWidth, layout.MaxHeight, layout.Height, layout.WidthIncludingTrailingWhitespace,
            collapsed = layout.TextLines.Any(line => line.HasCollapsed)
        }));
        Require(button.IsEffectivelyVisible && label.IsEffectivelyVisible, scene + ": not realized/visible");
        // Legacy association may be disabled without a selected legacy candidate; never fake enablement.
        Require(label.Text == expected && AutomationProperties.GetName(button) == expected, scene + ": full text/name mismatch");
        Require(label.TextWrapping == TextWrapping.Wrap && label.TextTrimming == TextTrimming.None,
            scene + ": production text is not wrapping/untrimmed");
        Require(rendered == expected && layout.TextLines.All(line => !line.HasCollapsed), scene + ": shortened text runs");
        double contentWidth = presenter.Bounds.Width - presenter.Padding.Left - presenter.Padding.Right;
        Require(double.IsFinite(layout.MaxWidth) && layout.MaxWidth > 0 && layout.MaxWidth <= contentWidth + 1,
            scene + ": TextLayout not constrained to actual button content width");
        Require(layout.WidthIncludingTrailingWhitespace <= label.Bounds.Width + 1 && layout.Height <= label.Bounds.Height + 1,
            scene + ": glyph layout exceeds label bounds");
        Require(Fits(label, presenter, presenter.Padding) && Fits(label, button, button.Padding) && Fits(button, host, default),
            scene + ": label/button exceeds content bounds or native viewport");
        foreach (Control ancestor in label.GetVisualAncestors().OfType<Control>())
            if (ancestor.ClipToBounds || ancestor is ScrollViewer)
                Require(Fits(label, ancestor, default), scene + ": clipped by " + ancestor.GetType().Name);
    }

    private static bool Fits(Control child, Control container, Thickness inset)
    {
        Point? p = child.TranslatePoint(default, container);
        return p.HasValue && child.Bounds.Width > 0 && child.Bounds.Height > 0
            && p.Value.X >= inset.Left - 1 && p.Value.Y >= inset.Top - 1
            && p.Value.X + child.Bounds.Width <= container.Bounds.Width - inset.Right + 1
            && p.Value.Y + child.Bounds.Height <= container.Bounds.Height - inset.Bottom + 1;
    }
    private static async Task SettleAsync(Window host)
    {
        await Dispatcher.UIThread.InvokeAsync(host.UpdateLayout, DispatcherPriority.Background);
        await Dispatcher.UIThread.InvokeAsync(host.UpdateLayout, DispatcherPriority.Render);
        await Dispatcher.UIThread.InvokeAsync(host.UpdateLayout, DispatcherPriority.Background);
    }
    private static T Need<T>(ArchivePage page, string name) where T : Control =>
        page.FindControl<T>(name) ?? throw new InvalidOperationException("Missing production control: " + name);
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
