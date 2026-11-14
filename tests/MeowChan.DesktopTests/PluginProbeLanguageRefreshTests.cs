using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MeowChan;
using MeowChan.Desktop.Localization;
using MeowChan.PluginSdk;
using ProbePresentation = MeowChan.PluginsPage.PluginProbePresentation;

namespace MeowChan.DesktopTests;

// Pure detached presentation and source-wiring contracts. No workers, compiler,
// XAML construction, native UI or translation-quality acceptance is implied.
[Collection("Localization")]
public sealed class PluginProbeLanguageRefreshTests : IDisposable
{
    private readonly string _previousLanguage = Texts.Language;
    private static readonly SourceThreadId ThreadId = new("synthetic", "test", "42");
    private static readonly SourceBoardId BoardId = new("synthetic", "test");
    private static readonly Uri ThreadUrl = new("https://example.invalid/test/thread/42");
    private static readonly DateTimeOffset RetryAt = new(2026, 11, 3, 12, 34, 0, TimeSpan.FromHours(1));

    public PluginProbeLanguageRefreshTests()
    {
        Texts.Initialize();
        Texts.SetLanguage("en");
    }

    public void Dispose()
    {
        Texts.Initialize();
        Texts.SetLanguage(_previousLanguage);
    }

    [Fact]
    public void CompletedThreadRetainsOnlyBoundedRawPreviewAndRendersCurrentFraming()
    {
        const string raw = "plugins.unchanged {0}\r\nEspañol / Français / Русский";
        var attachment = new SourceAttachment(0, "media-raw", Url: new("https://example.invalid/image.jpg"),
            OriginalFileName: "plugins.notFound {1}.jpg");
        var posts = Enumerable.Range(0, 101).Select(index => new SourcePost("post-" + index, index,
            SourceFieldPresence.All, Subject: "subject-" + index, Text: raw,
            Attachments: new[] { attachment })).ToArray();
        var state = ProbePresentation.Capture(new MeowChan.PluginSdk.ThreadSnapshot(new(
            ThreadId, ThreadUrl, "not retained title", SourceThreadState.Active, posts)));
        string expectedRaw = string.Join("\n", posts.Take(100).Select(post =>
            post.PostId + ": " + post.Subject + " " + post.Text + "\n  "
            + attachment.MediaId + " · " + attachment.OriginalFileName + " · " + attachment.Url));
        Assert.Equal(ProbePresentation.ResultKind.Thread, state.Kind);
        Assert.Equal(101, state.EntryCount);
        Assert.Equal(101L, state.AttachmentCount);
        Assert.True(state.PreviewLimited);
        Assert.Equal(expectedRaw, state.RawContent);
        Assert.DoesNotContain("post-100:", state.RawContent);
        string english = state.Render();
        // Capture has detached both raw values and counts from the source array.
        posts[0] = posts[0] with { Text = "mutated worker result", Attachments = null };
        foreach (string language in new[] { "es", "fr", "ru", "en" })
        {
            Texts.SetLanguage(language);
            Assert.Equal(language, Texts.Language);
            Assert.Equal(Texts.T("plugins.threadResult", 101, 101L) + "\n" + expectedRaw
                + "\n" + Texts.T("plugins.previewLimited"), state.Render());
            Assert.Equal(expectedRaw, state.RawContent);
            Assert.Contains(raw, state.Render());
            if (language != "en") Assert.NotEqual(english, state.Render());
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(101)]
    public void ThreadPreviewBoundaryDoesNotInventALimitAtOneHundred(int count)
    {
        var posts = Enumerable.Range(0, count).Select(index => new SourcePost("p" + index,
            index, SourceFieldPresence.Text, Text: "raw")).ToArray();
        var state = ProbePresentation.Capture(new MeowChan.PluginSdk.ThreadSnapshot(new(
            ThreadId, ThreadUrl, null, SourceThreadState.Active, posts)));
        Assert.Equal(count > 100, state.PreviewLimited);
        Assert.Equal(count, state.EntryCount);
        Assert.Equal(0L, state.AttachmentCount);
        Assert.Equal(count > 100, state.Render().Contains(Texts.T("plugins.previewLimited"), StringComparison.Ordinal));
        if (count == 0) Assert.Equal(Texts.T("plugins.threadResult", 0, 0L) + "\n", state.Render());
    }

    [Fact]
    public void CatalogCountsAllEntriesButDetachesOnlyTheExistingHundredEntryPreview()
    {
        var threads = Enumerable.Range(0, 101).Select(index => new SourceThreadReference(
            ThreadId with { ThreadId = "thread-" + index }, ThreadUrl, "plugins.catalogResult {0} · raw " + index)).ToArray();
        string raw = string.Join("\n", threads.Take(100).Select(thread =>
            thread.Id.ThreadId + " · " + thread.Title + " · " + thread.Url));
        var state = ProbePresentation.Capture(new CatalogSnapshot(new(BoardId, threads, true)));
        threads[0] = threads[0] with { Title = "changed after capture" };
        Assert.Equal(ProbePresentation.ResultKind.Catalog, state.Kind);
        Assert.DoesNotContain("thread-100 ·", state.RawContent);
        foreach (string language in new[] { "es", "fr", "ru", "en" })
        {
            Texts.SetLanguage(language);
            Assert.Equal(Texts.Count("plugins.catalogResult", 101) + "\n" + raw, state.Render());
            Assert.Equal(raw, state.RawContent);
        }
    }

    public static IEnumerable<object[]> StatusCases()
    {
        const string reason = "plugins.notFound {0} raw-reason";
        yield return new object[] { new ThreadDeferred(ThreadId, RetryAt, reason), "plugins.deferred", new object?[] { RetryAt, reason } };
        yield return new object[] { new CatalogDeferred(BoardId, RetryAt, reason), "plugins.deferred", new object?[] { RetryAt, reason } };
        yield return new object[] { new ThreadNotFound(ThreadId, 404), "plugins.notFound", new object?[] { 404 } };
        yield return new object[] { new ThreadNotFound(ThreadId), "plugins.notFound", new object?[] { null } };
        yield return new object[] { new ThreadUnchanged(ThreadId, new()), "plugins.unchanged", Array.Empty<object?>() };
        yield return new object[] { new CatalogUnchanged(BoardId, new()), "plugins.unchanged", Array.Empty<object?>() };
    }

    [Theory]
    [MemberData(nameof(StatusCases))]
    public void StatusDiscriminatorRendersCurrentLanguageWithCapturedDateAndCode(object result, string key, object?[] args)
    {
        var state = ProbePresentation.Capture(result);
        string english = state.Render();
        foreach (string language in new[] { "es", "fr", "ru", "en" })
        {
            Texts.SetLanguage(language);
            Assert.Equal(Texts.T(key, args), state.Render());
            if (state.Kind == ProbePresentation.ResultKind.Deferred)
            {
                Assert.Equal(RetryAt, state.RetryAt);
                Assert.Equal("plugins.notFound {0} raw-reason", state.RawContent);
                Assert.Contains(state.RawContent, state.Render());
            }
            if (language != "en") Assert.NotEqual(english, state.Render());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExternalFailureTextIsNeverTranslatedOrInterpreted(bool catalog)
    {
        var failure = new FailureDetail("plugins.notFound", "Source returned unchanged. {0}\r\nraw failure / Français");
        object result = catalog ? new CatalogFailure(BoardId, failure) : new ThreadFailure(ThreadId, failure);
        var state = ProbePresentation.Capture(result);
        foreach (string language in new[] { "es", "fr", "ru", "en" })
        {
            Texts.SetLanguage(language);
            Assert.Equal(ProbePresentation.ResultKind.Failure, state.Kind);
            Assert.Equal(failure.Code + ": " + failure.Message, state.Render());
            Assert.Equal(state.RawContent, state.Render());
        }
    }

    [Fact]
    public void PresentationUsesReplacementCatalogEvenWhenLanguageCodeDoesNotChange()
    {
        var state = ProbePresentation.Capture(new ThreadNotFound(ThreadId, 404));
        Texts.SetLanguage("fr");
        string previous = state.Render();
        using TempDirectory directory = new();
        File.WriteAllText(Path.Combine(directory.Path, "fr.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1, code = "fr", nativeName = "Français test",
            strings = new Dictionary<string, string> { ["plugins.notFound"] = "REFRESH HTTP {0}" }
        }));
        Texts.Initialize(directory.Path);
        Assert.Equal("fr", Texts.Language);
        Assert.Equal("REFRESH HTTP 404", state.Render());
        Assert.NotEqual(previous, state.Render());
    }

    [Fact]
    public void CacheStoresTypedPresentationAndOnlyRawInputIsCapturedFromControls()
    {
        string source = ReadSource("PluginsPage.Details.cs");
        string state = Between(source, "private sealed class PluginToolViewState", "// Detached, display-only data");
        Assert.Contains("PluginProbePresentation? Result", state);
        Assert.DoesNotContain("string Result", state);
        string capture = Between(source, "private void CapturePluginProbeState()", "// Selection and rebuild");
        Assert.Contains("state.Url =", capture);
        Assert.DoesNotContain("state.Result =", capture);
        Assert.DoesNotContain("PluginProbeResult", capture);
        Assert.DoesNotContain("C<TextBox>(\"PluginProbeResult\").TextChanging", source);

        // Retained model fields are detached primitives/value types and strings,
        // never a worker result, collection, delegate or a captured source object.
        foreach (FieldInfo field in typeof(ProbePresentation).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
        {
            Type type = Nullable.GetUnderlyingType(field.FieldType) ?? field.FieldType;
            Assert.True(type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(DateTimeOffset), field.Name);
        }
        Assert.Throws<InvalidOperationException>(() => ProbePresentation.Capture(new object()));
    }

    [Fact]
    public void CompletionLanguageAndRestoreRerenderWithoutReprobeOrAuthorizationChanges()
    {
        string main = ReadSource("PluginsPage.axaml.cs");
        string callback = Between(main, "private void OnLanguageChanged(", "private void Localize()");
        Assert.Contains("Dispatcher.UIThread.Post(", callback);
        Assert.Contains("if (_ended) return;", callback);
        Assert.Contains("RenderError();", callback); // Preserve L8-01.
        Assert.Contains("RenderPluginProbeResult();", callback);
        Assert.DoesNotContain("ProbeAsync(", callback);
        Assert.DoesNotContain("_tested =", callback);
        Assert.DoesNotContain("_renderedKey =", callback);
        Assert.DoesNotContain("_selectedKey =", callback);

        string details = ReadSource("PluginsPage.Details.cs");
        string restore = Between(details, "private void RestorePluginToolState(", "private void RenderPluginProbeResult()");
        Assert.Contains("_pluginToolKey = entry.Key;", restore);
        Assert.Contains("C<TextBox>(\"PluginProbeUrl\").Text = state.Url;", restore);
        Assert.Contains("ExpansionState(entry.Key)", restore);
        Assert.Contains("RenderPluginProbeResult();", restore);
        Assert.DoesNotContain("ProbeAsync(", restore);
        Assert.DoesNotContain("_tested =", restore);
        string render = Between(details, "private void RenderPluginProbeResult()", "private void RenderTechnicalIdentity(");
        Assert.Contains("key != _selectedKey", render);
        Assert.Contains("ToolState(key).Result?.Render()", render);
        foreach (string forbidden in new[] { "ProbeAsync(", "_tested =", "_drafts", "ExpansionState(", "PluginProbeUrl" })
            Assert.DoesNotContain(forbidden, render);

        string testing = ReadSource("PluginsPage.Testing.cs");
        string probe = Between(testing, "internal async Task ProbeAsync()", "private void OnCancelClick(");
        int guard = probe.IndexOf("if (!Current(generation)) return;", StringComparison.Ordinal);
        int capture = probe.IndexOf("ToolState(tested.Key).Result = PluginProbePresentation.Capture(result);", StringComparison.Ordinal);
        Assert.True(guard >= 0 && capture > guard);
        Assert.Contains("RenderPluginProbeResult();", probe);
        Assert.Contains("if (result is ThreadFailure or CatalogFailure) Error(formatted);", probe);
        Assert.DoesNotContain("FormatProbe(", testing);
        string build = Between(testing, "internal async Task BuildAndTestAsync()", "private void RenderDiagnostics(");
        Assert.Contains("ToolState(entry.Key).Result = null;", build);
        Assert.Contains("RenderPluginProbeResult();", build);
    }

    private static string Between(string source, string start, string end)
    {
        int first = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(first >= 0, "Source anchor missing: " + start);
        int last = source.IndexOf(end, first + start.Length, StringComparison.Ordinal);
        Assert.True(last > first, "Source anchor missing: " + end);
        return source[first..last];
    }

    private static string ReadSource(string name, [CallerFilePath] string sourcePath = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!,
            "../../Desktop/Views/Plugins", name)));
}
