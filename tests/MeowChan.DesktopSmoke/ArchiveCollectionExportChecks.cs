using Avalonia.Controls;
using Avalonia.Interactivity;
using MeowChan.Archive;

namespace MeowChan.DesktopSmoke;

internal static class ArchiveCollectionExportChecks
{
    internal static async Task RunAsync(ArchivePage page, ResourceId first, ResourceId second, string root,
        Action<TaskCompletionSource?> setGate, Func<string?> opened)
    {
        Button Button(string name) => page.FindControl<Button>(name)!;
        TextBox target = page.FindControl<TextBox>("ArchiveExportDestination")!;
        void Click(string name) => Button(name).RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        int closed = 0;
        List<ResourceId> selection = new() { first, second, first };
        Task opening = page.OpenExportForThreadsAsync(selection, null, () => closed++);
        selection.Clear(); selection.Add(new ResourceId(first.Source, first.Board, "not-selected"));
        await opening;
        Check(page.FindControl<TextBlock>("ArchiveExportSummary")!.Text!.Contains(second.Thread!), "Collection lost its frozen member set.");
        Check(!page.FindControl<Expander>("ArchiveExportLegacySection")!.IsVisible, "Collection offered single-thread legacy association.");
        string original = Path.Combine(root, "collection-original");
        target.Text = original;
        await page.OpenExportForThreadsAsync(new[] { second }, null, () => throw new InvalidOperationException("Duplicate callback replaced original."));
        Check(target.Text == original, "Duplicate collection dialog replaced destination.");
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        setGate(gate);
        Click("ArchiveExportStart");
        target.Text = Path.Combine(root, "collection-changed-during-run");
        page.FindControl<CheckBox>("ArchiveExportMedia")!.IsChecked = false;
        Click("ArchiveExportStart");
        gate.TrySetResult(); setGate(null);
        await Program.WaitUntilAsync(() => !page.IsExportRunning);
        ArchiveCollectionHtmlExportResult result = page.LastCollectionExport ?? throw new InvalidOperationException(page.FindControl<TextBlock>("ArchiveExportStatus")!.Text);
        Check(result.Directory == original && result.ThreadCount == 2 && result.PostCount == 2 && result.FileCount == 1 && result.Omissions.Count == 0,
            "Collection counts/options/destination did not reflect frozen selection and start state.");
        Check(result.Metadata!.Members.Select(item => item.Thread).SequenceEqual(new[] { first, second }), "Collection members changed or duplicate identity was exported.");
        Check(result.Metadata.Members.All(item => File.Exists(Path.Combine(result.Directory, item.PagePath))), "Collection published without all member pages.");
        Check(page.LastExport == null && File.Exists(result.IndexPath), "Collection result was conflated with one-thread export.");
        target.Text = Path.Combine(root, "collection-edited-after-success");
        Click("ArchiveExportOpen");
        Check(opened() == original, "Collection result action followed edited draft.");
        Check(page.FindControl<TextBlock>("ArchiveExportStatus")!.Text!.Contains(second.Thread!), "Collection result omitted member detail.");
        Click("ArchiveExportClose");
        Check(closed == 1, "Closing collection did not restore shell exactly once.");
        await page.OpenExportForThreadsAsync(new[] { first, second }, null, () => closed++);
        Check(page.FindControl<Button>("ArchiveExportPrevious")!.IsEnabled && !Button("ArchiveExportOpen").IsEnabled,
            "Collection history and current result were conflated.");
        Check(page.FindControl<RadioButton>("ArchiveExportNew")!.IsChecked == true, "Collection history silently chose update.");
        page.FindControl<RadioButton>("ArchiveExportUpdate")!.IsChecked = true;
        page.FindControl<CheckBox>("ArchiveExportBackup")!.IsChecked = true;
        File.WriteAllText(Path.Combine(original, "personal-note.txt"), "must survive");
        // An update must reject a changed observed fingerprint, then refresh the inspection.
        Click("ArchiveExportStart");
        await Program.WaitUntilAsync(() => !page.IsExportRunning);
        Check(page.LastCollectionExport == null && File.Exists(Path.Combine(original, "personal-note.txt")), "Stale collection inspection was accepted.");
        Click("ArchiveExportStart");
        await Program.WaitUntilAsync(() => !page.IsExportRunning);
        result = page.LastCollectionExport ?? throw new InvalidOperationException(page.FindControl<TextBlock>("ArchiveExportStatus")!.Text);
        Check(result.Metadata!.Generation == 2 && result.BackupDirectory != null &&
            File.ReadAllText(Path.Combine(result.BackupDirectory, "personal-note.txt")) == "must survive", "Collection update did not preserve changed content.");
        page.FindControl<RadioButton>("ArchiveExportNew")!.IsChecked = true;
        string another = Path.Combine(root, "collection-new-folder"); target.Text = another;
        Click("ArchiveExportStart"); await Program.WaitUntilAsync(() => !page.IsExportRunning);
        Check(page.LastCollectionExport?.Directory == another && page.LastCollectionExport.Metadata!.Generation == 1 && File.Exists(Path.Combine(original, "index.html")),
            "Collection new-folder action replaced previous root.");
        byte[] previous = File.ReadAllBytes(Path.Combine(another, "index.html"));
        Click("ArchiveExportStart"); await Program.WaitUntilAsync(() => !page.IsExportRunning);
        Check(page.LastCollectionExport == null && !Button("ArchiveExportOpen").IsEnabled && previous.SequenceEqual(File.ReadAllBytes(Path.Combine(another, "index.html"))),
            "Conflicting collection create changed root or retained stale result.");
        target.Text = Path.Combine(root, "collection-cancelled");
        gate = new(TaskCreationOptions.RunContinuationsAsynchronously); setGate(gate);
        Click("ArchiveExportStart"); Click("ArchiveExportClose");
        await Program.WaitUntilAsync(() => !page.IsExportRunning);
        Check(closed == 1 && page.FindControl<Border>("ArchiveExportDialog")!.IsVisible && !Directory.Exists(target.Text!),
            "Collection cancellation closed shell early or published output.");
        gate.TrySetResult(); setGate(null);
        ComboBox history = page.FindControl<ComboBox>("ArchiveExportHistory")!;
        history.SelectedIndex = history.Items.Cast<object>().Select((item, index) => (item, index))
            .Single(item => item.item.ToString()!.Contains(another, StringComparison.Ordinal)).index;
        page.FindControl<RadioButton>("ArchiveExportUpdate")!.IsChecked = true;
        Directory.Move(another, another + "-moved");
        Click("ArchiveExportStart"); await Program.WaitUntilAsync(() => !page.IsExportRunning);
        Check(page.LastCollectionExport == null && !Button("ArchiveExportPrevious").IsEnabled &&
            !page.FindControl<RadioButton>("ArchiveExportUpdate")!.IsEnabled && target.Text == another,
            "Missing collection target retained stale inspection or switched silently to a different export.");
        Click("ArchiveExportClose"); Check(closed == 2, "Collection callback was lost or called twice.");
        // Singleton selection returns to the established single-thread workflow.
        await page.OpenExportForThreadsAsync(new[] { first, first }, null);
        Check(page.FindControl<Expander>("ArchiveExportLegacySection")!.IsVisible && page.LastCollectionExport == null,
            "Singleton export retained collection state.");
        Click("ArchiveExportClose");
        Console.WriteLine("SMOKE collection export: frozen selection, duplicate guard, explicit generation/fingerprint/backup update, new folder, exact result path and cancellation passed.");
    }
}
