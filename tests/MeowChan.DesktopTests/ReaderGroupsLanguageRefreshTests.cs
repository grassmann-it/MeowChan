using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using MeowChan;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Persistence;

namespace MeowChan.DesktopTests;

// Production presentation models + detached controls and source wiring contracts.
// No native Window, profile IO, host searches or language-quality acceptance.
[Collection("Localization")]
public sealed class ReaderGroupsLanguageRefreshTests : IDisposable
{
    private readonly string _previousLanguage = Texts.Language;
    private static readonly string[] Languages = ["es", "fr", "ru", "de", "en"];

    public ReaderGroupsLanguageRefreshTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage(_previousLanguage); }

    [Fact]
    public void EveryDedicatedKeyIsBundledAndRetainsTheFormattingContract()
    {
        Assert.Empty(Texts.ValidateBuiltInCatalogs());
        Assert.Empty(Texts.Warnings);
        Assert.NotEmpty(ReaderGroupsCatalog.Entries);
        foreach (string language in new[] { "es", "fr", "ru" })
        {
            Assert.Contains("(Untested)", Texts.Languages.Single(item => item.Code == language).NativeName);
            using var stream = typeof(Texts).Assembly.GetManifestResourceStream(
                "MeowChan.Desktop.Localization.Packs." + language + ".json");
            Assert.NotNull(stream);
            using var document = JsonDocument.Parse(stream!);
            JsonElement strings = document.RootElement.GetProperty("strings");
            Texts.SetLanguage(language);
            foreach (var entry in ReaderGroupsCatalog.Entries)
            {
                Assert.True(strings.TryGetProperty(entry.Key, out var translation), entry.Key);
                string value = translation.GetString()!;
                Assert.False(string.IsNullOrWhiteSpace(value));
                Assert.NotEqual(entry.English, value);
                Assert.Equal(Placeholders(entry.English), Placeholders(value));
                Assert.Equal(value, Texts.T(entry.Key));
            }
        }
    }

    [Theory]
    [InlineData(true, false, "readerGroups.localPosts")]
    [InlineData(false, true, "readerGroups.watchedNoPosts")]
    [InlineData(false, false, "readerGroups.unavailableLocally")]
    public void CandidateRelabelsTheSameRowWithoutTranslatingIdentityOrTitle(bool local, bool watched, string key)
    {
        var item = new ReaderGroupCandidate(new("raw-source", "b", "123"), "readerGroups.saved {0} / 日本語", local, watched);
        var row = new ReaderGroupsDialog.MemberRow(item);
        row.Refresh();
        // Bound TextBlock assertions run on the real UI thread in
        // ReaderGroupsBindingLanguageChecks, not xUnit's arbitrary worker thread.
        int notifications = 0;
        row.PropertyChanged += (_, e) => { Assert.Equal("Display", e.PropertyName); notifications++; };
        string english = row.Display;
        foreach (string language in Languages)
        {
            Texts.SetLanguage(language);
            row.Refresh();
            Assert.Same(item, row.Item);
            Assert.Equal($"raw-source/b/123 · {item.Title} · {Texts.T(key)}", row.Display);
            Assert.Equal(row.Display, row.ToString());
            if (language != "en") Assert.NotEqual(english, row.Display);
        }
        Assert.Equal(Languages.Length, notifications);
        row.Refresh();
        Assert.Equal(Languages.Length, notifications); // unchanged labels do not churn the binding
    }

    [Fact]
    public void ProductionRelabelKeepsListSourcesSelectionOrderHiddenStateAndDirtyDraft()
    {
        ResourceId a = new("a", "b", "1"), b = new("a", "b", "2"), c = new("a", "b", "3");
        Guid groupId = Guid.NewGuid();
        ReaderGroupsDraft draft = new(new(1, 7, [new(groupId, "saved name", [c])]));
        var group = new ReaderGroupsDialog.GroupRow(draft.Groups[0]);
        draft.Rename(groupId, " raw user draft {0} "); // immutable record replaced after the row was built
        var rows = new[] {
            new ReaderGroupsDialog.MemberRow(new(a, "A", true)),
            new ReaderGroupsDialog.MemberRow(new(b, "B", false, true)),
            new ReaderGroupsDialog.MemberRow(new(c, "C", false)) };
        var groups = new ListBox { ItemsSource = new[] { group }, SelectedItem = group };
        var candidates = new ListBox { ItemsSource = rows, SelectionMode = SelectionMode.Multiple, IsVisible = false };
        var order = new List<ResourceId>();
        candidates.SelectionChanged += (_, _) =>
        {
            ResourceId[] chosen = candidates.SelectedItems!.Cast<ReaderGroupsDialog.MemberRow>().Select(row => row.Item.Id).ToArray();
            order.RemoveAll(id => !chosen.Contains(id));
            foreach (ResourceId id in chosen) if (!order.Contains(id)) order.Add(id);
        };
        candidates.SelectedItems!.Add(rows[2]);
        candidates.SelectedItems.Add(rows[0]);
        var selected = candidates.SelectedItems.Cast<object>().ToArray();
        Assert.Equal(new[] { c, a }, order); // admission order differs deliberately from row order
        var source = candidates.ItemsSource;
        var savedGroups = groups.ItemsSource;
        var draftSnapshot = draft.Snapshot;
        int selectionEvents = 0;
        candidates.SelectionChanged += (_, _) => selectionEvents++;
        groups.SelectionChanged += (_, _) => selectionEvents++;
        foreach (string language in Languages)
        {
            Texts.SetLanguage(language);
            ReaderGroupsDialog.RefreshRows(groups.Items.OfType<ReaderGroupsDialog.GroupRow>(),
                candidates.Items.OfType<ReaderGroupsDialog.MemberRow>(), draft);
            Assert.Same(source, candidates.ItemsSource);
            Assert.Same(savedGroups, groups.ItemsSource);
            Assert.Same(group, groups.SelectedItem);
            Assert.Equal(selected, candidates.SelectedItems.Cast<object>());
            Assert.Equal(new[] { c, a }, order);
            Assert.Equal(0, selectionEvents);
            Assert.False(candidates.IsVisible);
            Assert.True(draft.IsDirty);
            Assert.Equal(7, draft.Revision);
            Assert.True(ReaderGroupsValidation.SameGroups(draftSnapshot.Groups, draft.Groups));
            Assert.Equal(" raw user draft {0} ", draft.Groups[0].Name);
            Assert.Contains(" raw user draft {0} ", group.Display);
        }
    }

    [Fact]
    public void BlankNameIsRelabeledWithoutInventingOrCommittingAGroupName()
    {
        var original = new ReaderGroup(Guid.NewGuid(), "", []);
        var row = new ReaderGroupsDialog.GroupRow(original);
        foreach (string language in Languages)
        {
            Texts.SetLanguage(language);
            row.Refresh();
            Assert.StartsWith(Texts.T("readerGroups.newGroup") + " · 0 · ", row.Display);
            Assert.Equal(original.Id, row.Id);
            Assert.Equal("", original.Name);
        }
    }

    [Theory]
    [InlineData("readerGroups.added")]
    [InlineData("readerGroups.matches")]
    [InlineData("readerGroups.matchesNarrow")]
    public void CountNoticesRetainArgumentsAndUseCurrentLanguage(string key)
    {
        object?[] arguments = [23];
        var message = ReaderGroupsDialog.PresentationText.Counted(key, 23, arguments);
        arguments[0] = 999;
        foreach (string language in Languages)
        {
            Texts.SetLanguage(language);
            Assert.Equal(Texts.Count(key, 23), message.Render());
            Assert.DoesNotContain("999", message.Render());
        }
    }

    [Theory]
    [InlineData("readerGroups.hint")]
    [InlineData("readerGroups.saved")]
    [InlineData("readerGroups.conflict")]
    [InlineData("readerGroups.alreadyMembers")]
    public void PersistentHostNoticesAreRenderedAgainNotCleared(string key)
    {
        var message = ReaderGroupsDialog.PresentationText.Localized(key);
        string english = message.Render();
        foreach (string language in Languages)
        {
            Texts.SetLanguage(language);
            Assert.Equal(Texts.T(key), message.Render());
            Assert.False(string.IsNullOrWhiteSpace(message.Render()));
            if (language != "en") Assert.NotEqual(english, message.Render());
        }
    }

    [Fact]
    public void MissingMembersPromptRetainsExactCountAndIdentities()
    {
        const string detail = "source/b/1\nsource/b/2 {0}";
        var message = ReaderGroupsDialog.PresentationText.Counted("readerGroups.missingMembers", 2, 2, detail);
        foreach (string language in Languages)
        {
            Texts.SetLanguage(language);
            Assert.Equal(Texts.Count("readerGroups.missingMembers", 2, 2, detail), message.Render());
            Assert.EndsWith("\n" + detail, message.Render());
        }
    }

    [Theory]
    [InlineData("readerGroups.loadFailed")]
    [InlineData("readerGroups.saveFailed")]
    [InlineData("readerGroups.noReadable")]
    public void HostWrappersDoNotFormatOrTranslateRawDiagnostics(string key)
    {
        const string raw = "readerGroups.saved {0}\r\n/raw/файл.json";
        var message = ReaderGroupsDialog.PresentationText.Localized(key, raw);
        foreach (string language in Languages)
        {
            Texts.SetLanguage(language);
            Assert.Equal(Texts.T(key, raw), message.Render());
            Assert.Contains(raw, message.Render());
        }
    }

    [Theory]
    [InlineData("readerGroups.futureVersion")]
    [InlineData("readerGroups.recoveryAvailable")]
    [InlineData("readerGroups.unavailable")]
    public void LoadFailureRetainsRawPathAndDiagnostic(string key)
    {
        const string path = "/fixture/readerGroups.hint {1}.json", error = "readerGroups.saved {0}";
        var message = ReaderGroupsDialog.PresentationText.Localized(key, path, error);
        foreach (string language in Languages)
        {
            Texts.SetLanguage(language);
            Assert.Equal(Texts.T(key, path, error), message.Render());
            Assert.EndsWith("\n" + path + "\n" + error, message.Render());
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("readerGroups.saved")]
    [InlineData("Groups saved.")]
    [InlineData("{0}\r\nprovider text Español / Français / Русский")]
    public void RawMessagesAndReturnNoticesAreNeverReverseMapped(string raw)
    {
        var message = ReaderGroupsDialog.PresentationText.Raw(raw);
        foreach (string language in Languages)
        {
            Texts.SetLanguage(language);
            Assert.Equal(raw, message.Render());
        }
    }

    [Fact]
    public async Task PromptInvalidatesSynchronouslyOnWorkerBeforeItsDismissalRuns()
    {
        Action? pendingDismissal = null;
        bool dismissed = false;
        using var lifetime = new ReaderGroupsPrompt.LanguageLifetime(() => pendingDismissal = () => dismissed = true);
        Assert.True(lifetime.IsCurrent);
        await Task.Run(() => Texts.SetLanguage("fr"));
        Assert.False(lifetime.IsCurrent);
        Assert.False(dismissed);
        Assert.NotNull(pendingDismissal);
        pendingDismissal!();
        Assert.True(dismissed);
        Texts.SetLanguage("en");
        Assert.False(lifetime.IsCurrent); // switching back must not revive old consent
    }

    [Fact]
    public void SameCodeCatalogReloadAlsoInvalidatesPromptAndDisposalUnsubscribes()
    {
        int calls = 0;
        var lifetime = new ReaderGroupsPrompt.LanguageLifetime(() => calls++);
        try
        {
            Texts.Initialize(); // same language code, new catalog snapshot
            Assert.False(lifetime.IsCurrent);
            Assert.Equal(1, calls);
        }
        finally { lifetime.Dispose(); }
        Texts.SetLanguage("es");
        Assert.Equal(1, calls);
    }

    [Fact]
    public void LanguageWiringDoesNotSearchResolveRebindDraftOrTouchInputSelection()
    {
        string source = ReadSource();
        string refresh = Between(source, "private void LanguageChanged(", "internal bool CanExecute(");
        Assert.Contains("Dispatcher.UIThread.Post(", refresh);
        Assert.Contains("if (_closed) return;", refresh);
        Assert.Contains("saved.Scroll.Offset = saved.Offset", refresh);
        Assert.Contains("UpdateLayout();", refresh);
        Assert.Contains("RefreshRows(", refresh);
        Assert.Contains("RenderPresentation();", refresh);
        Assert.Contains("AutomationProperties.SetName", refresh);
        foreach (string forbidden in new[] { "SearchAsync(", "ResolveMembersAsync(", "Render();", "ItemsSource =",
            "SelectedItem =", "SelectedItems.", "_candidateSelectionOrder.", "_draft =", "_draft.Discard", "_draft.Accept",
            "CaretIndex =", "SelectionStart =", "SelectionEnd =", "GroupSearch\").Text =", "GroupName\").Text =", ".Focus()" })
            Assert.DoesNotContain(forbidden, refresh);
        Assert.DoesNotContain("private static string L(", source);
        Assert.Contains("Texts.Changed -= LanguageChanged;", source);
        Assert.Contains("new Binding(nameof(DisplayRow.Display)) { Source = row }", source);
    }

    [Fact]
    public void PromptWiringDefaultsToCancelAndLanguageChangeCanOnlyDismiss()
    {
        string source = ReadSource();
        string prompt = source[source.IndexOf("internal static class ReaderGroupsPrompt", StringComparison.Ordinal)..];
        Assert.Contains("using LanguageLifetime language = new", prompt);
        Assert.Contains("if (!closed) prompt.Close(false)", prompt);
        Assert.Contains("prompt.Close(language.IsCurrent)", prompt);
        Assert.Contains("return accepted && language.IsCurrent", prompt);
        Assert.Contains("prompt.Opened += (_, _) => no.Focus();", prompt);
        Assert.Contains("Texts.Changed += Changed;", prompt);
        Assert.Contains("Texts.Changed -= Changed;", prompt);
        Assert.Contains("Interlocked.Exchange(ref _invalidated, 1);", prompt);
        Assert.DoesNotContain("prompt.Close(true)", prompt);
    }

    private static string[] Placeholders(string value) => Regex.Matches(value, @"\{\d+\}")
        .Select(match => match.Value).Distinct().Order(StringComparer.Ordinal).ToArray();

    private static string Between(string source, string start, string end)
    {
        int first = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(first >= 0, "Missing source anchor: " + start);
        int last = source.IndexOf(end, first + start.Length, StringComparison.Ordinal);
        Assert.True(last > first, "Missing source anchor: " + end);
        return source[first..last];
    }

    private static string ReadSource([CallerFilePath] string path = "") => File.ReadAllText(Path.GetFullPath(
        Path.Combine(Path.GetDirectoryName(path)!, "../../Desktop/Views/Collections/ReaderGroupsDialog.axaml.cs")));
}
