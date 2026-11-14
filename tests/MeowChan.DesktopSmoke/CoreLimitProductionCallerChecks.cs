using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Tags;
using MeowChan.Interfaces;
using Ursa.Controls;

namespace MeowChan.DesktopSmoke;

// Parent dispatch hook required. Run inside the centrally built native smoke harness after catalog registration.
// Real, initialized pages and in-memory snapshots; every archive/manifest opener fails if called. No profile or DB.
internal static class CoreLimitProductionCallerChecks
{
    private static Guid Id(int i) => new(i, 0, 0, new byte[8]);
    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException("SP5 caller: " + reason);
    }

    private static T Invoke<T>(object target, string method, params object?[] args) =>
        (T)(target.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Missing actual page method: " + method)).Invoke(target, args)!;

    private static ArchivePage Archive() => new(
        _ => throw new InvalidOperationException("SP5 must not open an archive."),
        (_, _) => throw new InvalidOperationException("SP5 must not change capture."),
        _ => throw new InvalidOperationException("SP5 must not launch an external app."));

    internal static async Task RunAsync(Window owner)
    {
        Dispatcher.UIThread.VerifyAccess();
        Require(owner.IsVisible, "a visible native smoke owner is required");
        string language = Texts.Language;
        try
        {
            foreach (string locale in new[] { "en", "de" })
            {
                Texts.SetLanguage(locale);
                Require(Texts.T("coreLimits.tags.tooMany", 8) != "coreLimits.tags.tooMany", "parent catalog registration is missing");
                ArchiveAdmissionAndReconstruction();
                MediaAdmissionAndReconstruction();
            }
            ReaderSlashCache();
            await UrlWriterSnapshotAsync(owner);
        }
        finally { Texts.SetLanguage(language); }
    }

    private static void ArchiveAdmissionAndReconstruction()
    {
        ArchivePage page = Archive();
        try
        {
            page.ArchiveTagFilterTagsPerListPreference = 128;
            for (int i = 1; i <= 128; i++) Require(page.AddDraftTag(Id(i), true) == null, "archive refused a tag within configured128");
            for (int i = 1000; i < 1007; i++) Require(page.AddDraftTag(Id(i), false) == null, "archive excluded tag refused");
            page.ArchiveTagFilterTagsPerListPreference = 8;
            ArchiveTagFilter before = Invoke<ArchiveTagFilter>(page, "ReadTagFilterDraft");
            Require(before.Include.Count == 128 && before.Exclude.Count == 7, "128→8 trimmed archive draft");
            Require(page.AddDraftTag(Id(500), true) == Texts.T("coreLimits.tags.tooMany", 8), "archive growth ignored lowered admission limit");
            Require(page.AddDraftTag(Id(1), true) == null, "existing archive tag was not a no-op");
            Require(page.ToggleDraftTag(Id(1)) == null, "oversized source prevented move into destination with room");
            Require(page.ToggleDraftTag(Id(2)) == Texts.T("coreLimits.tags.tooMany", 8), "full destination accepted a move");
            page.FindControl<ComboBox>("ArchiveTagMode")!.SelectedIndex = 1;
            ArchiveTagFilter moved = Invoke<ArchiveTagFilter>(page, "ReadTagFilterDraft");
            Require(moved.Include.Count == 127 && moved.Exclude.Count == 8 && moved.Match == ArchiveTagMatch.Any,
                "move or mode change dropped existing archive tags");
            var chips = page.FindControl<WrapPanel>("ArchiveDraftTags")!;
            chips.Children.OfType<ClosableTag>().Single(chip => chip.Tag is Guid id && id == Id(2)).Command!.Execute(null);
            ArchiveSearchSpec searched = Invoke<ArchiveSearchSpec>(page, "ReadSearchDraft", false);
            Require(searched.Tags is { Include.Count: 126, Exclude.Count: 8, Match: ArchiveTagMatch.Any }, "draft removal/search lost tag constraints");
            Require(ArchiveSearchSpecJson.Deserialize(ArchiveSearchSpecJson.Serialize(searched)).Equals(searched), "edited draft is not roundtrip safe");
            // Bind the real request to an immutable in-memory vocabulary, not a store or an uninitialized host.
            PrivateTagDefinition Definition(Guid id)
            {
                Require(PrivateTagName.TryCreate("tag-" + id.ToString("N"), out var name, out _), "fixture tag name");
                return new(new TagId(id), name!);
            }
            var tags = new PrivateTagStoreSnapshot(1, "SP5-offline", searched.Tags!.Include.Concat(searched.Tags.Exclude).Select(Definition),
                Array.Empty<PrivateThreadTagAssignment>(), Array.Empty<PrivateMediaTagAssignment>());
            page.GetType().GetMethod("AdoptPrivateTags", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, [tags]);
            Require(page.CreateSearchRequest(searched).TagMembership != null, "search silently dropped lowered-limit tags");
        }
        finally { page.EndLifetime(); }
    }

    private static void MediaAdmissionAndReconstruction()
    {
        var page = new MediaPage(_ => throw new InvalidOperationException("SP5 must not open media archive."),
            (_, _) => throw new InvalidOperationException("SP5 must not open media manifests."));
        try
        {
            page.ArchiveTagFilterTagsPerListPreference = 128;
            for (int i = 1; i <= 128; i++) Require(page.AddTagFilter(Id(i), true) == null, "media refused configured128");
            for (int i = 1000; i < 1007; i++) Require(page.AddTagFilter(Id(i), false) == null, "media excluded tag refused");
            page.ArchiveTagFilterTagsPerListPreference = 8;
            Require(page.CurrentFilter().Tags is { Include.Count: 128, Exclude.Count: 7 }, "128→8 trimmed media filter");
            Require(page.AddTagFilter(Id(500), true) == Texts.T("coreLimits.tags.tooMany", 8), "media growth ignored lowered admission limit");
            Require(page.AddTagFilter(Id(1), true) == null, "existing media tag was not a no-op");
            Require(page.ToggleTagFilter(Id(1)) == null, "media move revalidated oversized source against8");
            Require(page.ToggleTagFilter(Id(2)) == Texts.T("coreLimits.tags.tooMany", 8), "media full destination accepted a move");
            var mode = (ComboBox)typeof(MediaPage).GetField("_tagMode", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!;
            mode.SelectedIndex = 1;
            Require(page.CurrentFilter().Tags is { Include.Count: 127, Exclude.Count: 8, Match: ArchiveTagMatch.Any },
                "media mode change lost constraints");
        }
        finally { page.EndLifetime(); }
    }

    private static void ReaderSlashCache()
    {
        ArchivePage page = Archive();
        try
        {
            ResourceId id = new("4chan", "b", "123");
            var thread = new ArchivedThreadView(new ArchiveThreadSummary(id, "https://boards.4chan.org/b/thread/123",
                string.Join(" ", Enumerable.Range(1, 20).Select(i => "/board" + i + "/")), "/unused",
                ThreadSourceState.NotFound, false, DateTimeOffset.UnixEpoch, null, 0, 1), Array.Empty<CapturedPost>());
            page.SlashTagSuggestionCountPreference = 20;
            var first = Invoke<IReadOnlyList<PrivateTagName>>(page, "ThreadTagSuggestions", thread);
            Require(first.Count == 20, "reader did not use slash count20");
            Require(ReferenceEquals(first, Invoke<IReadOnlyList<PrivateTagName>>(page, "ThreadTagSuggestions", thread)), "reader stopped caching unchanged suggestions");
            page.SlashTagSuggestionCountPreference = 3;
            var second = Invoke<IReadOnlyList<PrivateTagName>>(page, "ThreadTagSuggestions", thread);
            Require(second.Count == 3 && !ReferenceEquals(first, second), "same-thread/revision cache ignored20→3");
            page.SlashTagSuggestionCountPreference = 8;
            Require(Invoke<IReadOnlyList<PrivateTagName>>(page, "ThreadTagSuggestions", thread).Count == 8, "same-thread/revision cache ignored3→8");
        }
        finally { page.EndLifetime(); }
    }

    private static async Task UrlWriterSnapshotAsync(Window owner)
    {
        ArchivePage page = Archive();
        Window fixture = new() { Width = 900, Height = 650, Content = page };
        var summary = new TaskCompletionSource<ArchiveThreadSummary?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            page.IsAvailable = true;
            fixture.Show(owner);
            page.UrlTextExportLimitMiBPreference = 17;
            page.UrlTextSummaryForTests = (_, _) => summary.Task;
            int picks = 0, shown = 0;
            Exception? callbackFailure = null;
            page.UrlTextChooseForTests = () =>
            {
                picks++;
                return Task.FromResult<string?>(Path.Combine(Path.GetTempPath(), new string('x', UrlTextExportFileWriter.MaximumPathCharacters + 1)));
            };
            // Do NOT replace UrlTextWriteForTests: this checks the actual production writer path.
            page.UrlTextShowForTests = async (dialog, _) =>
            {
                try
                {
                    shown++;
                    Require(dialog.Operation.LimitMiB == 17, "dialog did not retain operation config snapshot");
                    await dialog.Operation.ConfirmAsync();
                    await dialog.Operation.ConfirmAsync();
                    Require(dialog.Operation.Result?.Error == UrlTextExportFileWriteError.LimitExceeded, "actual writer did not reject overlong path");
                    Require(dialog.Operation.StatusKey == "urltext.export.error.pathLimit", "path refusal mislabeled as output limit");
                    Require(dialog.FindControl<TextBlock>("UrlTextExportError")!.Text == Texts.T("urltext.export.error.pathLimit"), "dialog error strip text is wrong");
                }
                catch (Exception error) { callbackFailure = error; }
            };
            ResourceId id = new("4chan", "b", "123");
            Task run = page.OpenUrlTextExportForThreadAsync(id, null);
            page.UrlTextExportLimitMiBPreference = 64; // changing config while reading cannot change this confirmation
            summary.SetResult(new ArchiveThreadSummary(id, "https://boards.4chan.org/b/thread/123", "offline", "/unused",
                ThreadSourceState.NotFound, false, DateTimeOffset.UnixEpoch, null, 0, 1));
            await run.WaitAsync(TimeSpan.FromSeconds(15));
            if (callbackFailure != null) throw new InvalidOperationException("SP5 URL callback failed.", callbackFailure);
            Require(shown == 1 && picks == 1, "export dialog/picker did not run exactly once");
        }
        finally { page.EndLifetime(); fixture.Close(); }
    }
}
