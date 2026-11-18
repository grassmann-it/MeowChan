using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;

namespace MeowChan.DesktopTests;

// Unit/guard coverage for the real MediaPage handoff-interaction API. This deliberately does not
// construct a window or a page host: native routed-input delivery remains a shell/GUI acceptance hook.
[Collection("Booru exact navigation")]
public sealed class BooruMediaHandoffInteractionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void CapturedHandoffIsValidUntilARealInteractionIsNoted()
    {
        MediaPage page = NewPage(out CancellationTokenSource lifetime);
        using var lifetimeLease = lifetime;
        Func<bool> guard = page.CaptureBooruHandoffInteraction();

        Assert.True(guard());
        page.EndPendingReturn();
        Assert.False(guard());
        Assert.False(lifetime.IsCancellationRequested);
    }

    [Fact]
    public async Task CapturedHandoffStaysValidAcrossActivationBuildAndPresentationChanges()
    {
        MediaPage page = NewPage(out CancellationTokenSource lifetime);
        using var lifetimeLease = lifetime;
        Func<bool> guard = page.CaptureBooruHandoffInteraction();
        var activation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> continuation = ContinueAfterAsync(activation.Task, guard);

        // A normal activation/build can replace all of these objects without being user takeover.
        Set(page, "_snapshot", Snapshot());
        SetProperty(page, "BuildTask", Task.FromResult(1));
        SetProperty(page, "PresentTask", Task.FromResult(2));
        SetProperty(page, "PresentCount", 1);
        SetProperty(page, "PresentRunCount", 1);
        Set(page, "_presentPending", true);
        using var building = new CancellationTokenSource();
        Set(page, "_building", building);

        activation.SetResult(true);
        Assert.True(await continuation);
        Assert.True(guard());
    }

    [Fact]
    public async Task UserInteractionRevokesAnAwaitingActivationButSnapshotBuildAndPresentChangesDoNot()
    {
        MediaPage page = NewPage(out CancellationTokenSource lifetime);
        using var lifetimeLease = lifetime;
        Func<bool> guard = page.CaptureBooruHandoffInteraction();
        var activation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> continuation = ContinueAfterAsync(activation.Task, guard);

        Set(page, "_snapshot", Snapshot());
        SetProperty(page, "BuildTask", Task.CompletedTask);
        SetProperty(page, "PresentTask", Task.CompletedTask);
        SetProperty(page, "PresentCount", 3);
        SetProperty(page, "PresentRunCount", 2);
        Set(page, "_presentPending", false);
        NoteInteraction(page); // The same production note used by pointer/key/filter paths.

        activation.SetResult(true);
        Assert.False(await continuation);
        Assert.False(guard());
    }

    [Fact]
    public void SelectionStampAndPageLifetimeRevokeTheCapturedHandoff()
    {
        MediaPage page = NewPage(out CancellationTokenSource lifetime);
        using var lifetimeLease = lifetime;
        Func<bool> selectionGuard = page.CaptureBooruHandoffInteraction();
        Assert.True(selectionGuard());
        Set(page, "_selectionStamp", 1L);
        Assert.False(selectionGuard());

        Func<bool> lifetimeGuard = page.CaptureBooruHandoffInteraction();
        Assert.True(lifetimeGuard());
        lifetime.Cancel(); // The cancellation signal raised by the real EndLifetime path.
        Assert.False(lifetimeGuard());
    }

    [Fact]
    public void LocalizationRefreshDoesNotCountAsUserInteraction()
    {
        MediaPage page = NewPage(out CancellationTokenSource lifetime);
        using var lifetimeLease = lifetime;
        Func<bool> guard = page.CaptureBooruHandoffInteraction();

        Set(page, "_localizing", true);
        NoteInteraction(page);
        Assert.True(guard());

        Set(page, "_localizing", false);
        NoteInteraction(page);
        Assert.False(guard());
    }

    [Theory]
    [InlineData("text")]
    [InlineData("sort direction")]
    [InlineData("shuffle")]
    public void FilterHooksRevokeBothActivationAndLateFocusGuardsWithoutPointerOrKeyEvents(string path) => OnDispatcher(() =>
    {
        MediaPage page = NewPage(out CancellationTokenSource lifetime);
        using var lifetimeLease = lifetime;
        Set(page, "_nameFilter", new SearchBox());
        Set(page, "_sortDirection", new Button());
        Set(page, "_shuffle", new Button());
        SetProperty(page, "BuildTask", Task.CompletedTask);
        SetProperty(page, "PresentTask", Task.CompletedTask);
        object run = new();
        Set(page, "_returnRun", run);
        Invoke(page, "InstallFilterHandoffInterruption");
        Func<bool> activation = page.CaptureBooruHandoffInteraction();
        Func<bool> focus = (Func<bool>)Invoke(page, "CaptureNavigationContext", run, false)!;
        Assert.True(focus());
        if (path == "text") page.NameFilter.SetTextSilently("IME/accessibility text"); // No QueryChanged: the synchronous text event must suffice.
        else Get<Button>(page, path == "shuffle" ? "_shuffle" : "_sortDirection").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(activation());
        Assert.False(focus());
    });

    [Fact]
    public void ProgrammaticFilterHookEventsDoNotRevokeActivationOrFocus() => OnDispatcher(() =>
    {
        MediaPage page = NewPage(out CancellationTokenSource lifetime);
        using var lifetimeLease = lifetime;
        Set(page, "_nameFilter", new SearchBox());
        Set(page, "_sortDirection", new Button());
        Set(page, "_shuffle", new Button());
        SetProperty(page, "BuildTask", Task.CompletedTask);
        SetProperty(page, "PresentTask", Task.CompletedTask);
        object run = new();
        Set(page, "_returnRun", run);
        Invoke(page, "InstallFilterHandoffInterruption");
        Func<bool> activation = page.CaptureBooruHandoffInteraction();
        Func<bool> focus = (Func<bool>)Invoke(page, "CaptureNavigationContext", run, false)!;
        Set(page, "_localizing", true);
        page.NameFilter.SetTextSilently("programmatic synchronization");
        Get<Button>(page, "_sortDirection").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Get<Button>(page, "_shuffle").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Set(page, "_localizing", false);
        Assert.True(activation());
        Assert.True(focus());
    });

    [Fact]
    public void M2M5MutationPathsConvergeOnTheUserEditGateWhileBooruPresentationBypassesIt()
    {
        // Source-wiring contract, not a native event-delivery or end-to-end test.
        string filters = Source("Desktop/Views/Media/MediaPage.Filters.cs");
        Assert.Contains("InstallFilterHandoffInterruption();", filters);
        Assert.Contains("_tagMode.SelectionChanged += (_, _) => FilterEdited();", filters);
        Assert.Contains("_nameFilter.TextChanging += (_, _) => NoteBooruHandoffInteraction();", filters);
        Assert.Contains("_nameFilter.QueryChanged += (_, _) =>", filters);
        foreach (string field in new[] { "_kindFilter", "_soundFilter", "_sourceFilter", "_boardFilter", "_minRating" })
            Assert.Contains(field + ".SelectionChanged +=", filters);
        foreach (string flag in new[] { "_favoritesOnly", "_withoutMediaTags", "_missing" })
            Assert.Contains("WireFlag(" + flag + ",", filters);
        string editTerms = filters[filters.IndexOf("private void EditTerms(", StringComparison.Ordinal)..filters.IndexOf("// Whether the set", StringComparison.Ordinal)];
        Assert.Contains("SyncFilterControls();", editTerms);
        Assert.Contains("FilterEdited();", editTerms);
        string gate = filters[filters.IndexOf("private void FilterEdited()", StringComparison.Ordinal)..filters.IndexOf("private void PresentEditedFilters()", StringComparison.Ordinal)];
        Assert.Contains("if (_localizing) return;", gate);
        Assert.Contains("NoteBooruHandoffInteraction();", gate);
        Assert.Contains("PresentEditedFilters();", gate);
        string chips = Source("Desktop/Views/Media/MediaPage.FilterChips.cs");
        Assert.Contains("_filterAdd.PickedTerm = AddFilterTerm;", chips);
        Assert.Contains("() => RemoveFilterTerm(term)", chips);
        Assert.Contains("ToggleFilterTerm(term)", chips);
        Assert.Contains("_tagMode.SelectedIndex = 0", chips);
        Assert.Contains("ResetFilters();", chips);
        string query = Source("Desktop/Views/Media/MediaPage.Query.cs");
        // The text scope (in:) leaves the set before it is applied (media search breakdown): "kept" is applied.Terms without the scope term.
        Assert.Contains("if (termsChanged) EditTerms(_ => kept);", query);
        Assert.Contains("else FilterEdited();", query);
        string navigation = Source("Desktop/Views/Media/MediaPage.BooruCrossLinks.cs");
        Assert.Contains("PresentEditedFilters();", navigation);
        Assert.DoesNotContain("FilterEdited();", navigation);
        string page = Source("Desktop/Views/Media/MediaPage.cs");
        int selectionStart = page.IndexOf("_grid.SelectionChanged += (_, e) =>", StringComparison.Ordinal);
        Assert.True(selectionStart >= 0, "The grid selection handler must remain wired.");
        int selectionEnd = page.IndexOf("};", selectionStart, StringComparison.Ordinal);
        Assert.True(selectionEnd > selectionStart, "The grid selection handler must have a body.");
        string selectionHandler = page[selectionStart..selectionEnd];
        int restoringGuard = selectionHandler.IndexOf("if (_restoring) return;", StringComparison.Ordinal);
        int cancelReturn = selectionHandler.IndexOf("EndPendingReturn();", StringComparison.Ordinal);
        int selectionStamp = selectionHandler.IndexOf("_selectionStamp++;", StringComparison.Ordinal);
        // Selection takeover must cancel an earlier gallery return before changing ownership.
        // Scope these checks to the handler, without depending on comments or LF/CRLF layout.
        Assert.True(restoringGuard >= 0 && cancelReturn > restoringGuard && selectionStamp > cancelReturn,
            "Selection must guard restoration, cancel the pending return, then advance its ownership stamp.");
        string returns = Source("Desktop/Views/Media/MediaPage.Return.cs");
        Assert.Contains("selection == _selectionStamp", returns);
        Assert.Contains("interaction == _booruHandoffInteractionRevision", returns);
    }

    private static object? Invoke(MediaPage page, string method, params object[] args) =>
        typeof(MediaPage).GetMethod(method, Private)!.Invoke(page, args);
    private static T Get<T>(MediaPage page, string field) => (T)typeof(MediaPage).GetField(field, Private)!.GetValue(page)!;
    private static string Source(string path, [CallerFilePath] string caller = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(caller)!, "../..", path)));

    private static void OnDispatcher(Action test)
    {
        FieldInfo primary = typeof(Dispatcher).GetField("s_uiThread", BindingFlags.Static | BindingFlags.NonPublic)!;
        object? previous = primary.GetValue(null);
        string language = Texts.Language;
        Dispatcher owner = Dispatcher.CurrentDispatcher;
        try
        {
            primary.SetValue(null, owner);
            owner.Invoke(() => { Texts.Initialize(); Texts.SetLanguage("en"); test(); }, DispatcherPriority.Send);
        }
        finally { Texts.SetLanguage(language); primary.SetValue(null, previous); }
    }

    private static async Task<bool> ContinueAfterAsync(Task activation, Func<bool> guard)
    {
        await activation;
        return guard();
    }

    private static MediaPage NewPage(out CancellationTokenSource lifetime)
    {
        var page = (MediaPage)RuntimeHelpers.GetUninitializedObject(typeof(MediaPage));
        lifetime = new CancellationTokenSource();
        Set(page, "_lifetime", lifetime);
        return page;
    }

    private static MediaLibrarySnapshot Snapshot() =>
        new(Array.Empty<MediaLibraryEntry>(), Array.Empty<MediaLibraryThreadState>(), Array.Empty<string>(), true);

    private static void NoteInteraction(MediaPage page) =>
        typeof(MediaPage).GetMethod("NoteBooruHandoffInteraction", Private)!.Invoke(page, null);

    private static void Set(MediaPage page, string field, object? value) =>
        typeof(MediaPage).GetField(field, Private)!.SetValue(page, value);

    private static void SetProperty(MediaPage page, string property, object value) =>
        typeof(MediaPage).GetProperty(property, Private)!.SetValue(page, value);
}
