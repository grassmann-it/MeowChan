using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using MeowChan.Desktop.Booru;

namespace MeowChan.DesktopTests;

// BP1 source/API contracts. No application, native window, bitmap decoder, profile or provider.
// Actual attach/detach and late-result checks live in BooruWorkbenchViewChecks (DesktopSmoke).
public sealed class BooruWorkbenchViewContractTests
{
    [Fact]
    public void EveryWindowSelectorRemainsACompileTimeAliasOfTheViewSelector()
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        FieldInfo[] viewNames = typeof(BooruWorkbenchView).GetFields(flags).Where(field => field.IsLiteral && field.FieldType == typeof(string)).ToArray();
        FieldInfo[] windowNames = typeof(BooruWorkbenchWindow).GetFields(flags).Where(field => field.IsLiteral && field.FieldType == typeof(string)).ToArray();
        Assert.Equal(36, viewNames.Length); // all 35 original top-level selectors plus CatalogStorageName
        Assert.Equal(viewNames.Select(field => field.Name).Order(), windowNames.Select(field => field.Name).Order());
        foreach (FieldInfo field in viewNames)
        {
            Assert.Equal(field.GetRawConstantValue(), windowNames.Single(alias => alias.Name == field.Name).GetRawConstantValue());
            Assert.Contains($"internal const string {field.Name} = BooruWorkbenchView.{field.Name};", WindowSource());
        }
    }

    [Fact]
    public void WindowRetainsItsCallersApiAndOnlyOwnsDialogChrome()
    {
        Assert.True(typeof(UserControl).IsAssignableFrom(typeof(BooruWorkbenchView)));
        Assert.True(typeof(IDisposable).IsAssignableFrom(typeof(BooruWorkbenchView)));
        string host = WindowSource();
        foreach (string required in new[] { "BooruWorkbenchWindow(BooruWorkbenchServices services)", "BooruWorkbenchWindow(BooruWorkbenchModel model, bool ownsModel)",
            "Model => _view.Model", "ShowInMedia { get => _view.ShowInMedia; set => _view.ShowInMedia = value; }",
            "IsPostSavedInMedia { get => _view.IsPostSavedInMedia; set => _view.IsPostSavedInMedia = value; }",
            "ApplyWorkView(WorkView view, bool viewChanged) => _view.ApplyWorkView(view, viewChanged)",
            "ShowQuery(string query) => _view.ShowQuery(query)", "AddTagToSearch(string tag) => _view.AddTagToSearch(tag)",
            "DialogWindow.Configure(this, DialogSize.XL, modal: false);", "DialogWindow.Frame(this, _view, modal: false);",
            "DialogFrame.Build(", "DialogFooter.Build(_close, status: notes)",
            "DialogEscape.Attach(this, tryCancelLocal: CancelEscapeLocally);", "Closed += (_, _) => Dispose()", "_view.Dispose();" })
            Assert.Contains(required, host);
        foreach (string forbidden in new[] { "Search.SubmitAsync", "Watchers.Changed", "Search.Changed", "DispatcherTimer", "Bitmap", "SKCodec", "Texts.Changed",
            "DialogFit.OnOpen(this)", "WindowChrome.Frame(this, _view)", "WindowChromeKind.Dialog", "Width = 1000", "MinWidth = 600" })
            Assert.DoesNotContain(forbidden, host);
        string view = ViewSource();
        foreach (string forbidden in new[] { "WindowChrome.", "DialogFrame.", "DialogFit.", "Close();", "SourceMediaIntegrityPermissions" })
            Assert.DoesNotContain(forbidden, view);
    }

    [Fact]
    public void HostFrameContainsTheViewRatherThanLeavingItOutsideTheVisualTree()
    {
        string view = ViewSource();
        Assert.Contains("Content = buildLayout is null ? BuildLayout(Header, Body, Footer) : buildLayout(Header, Body, Footer);", view);
        Assert.Contains("Header = BuildHeader();", view);
        Assert.Contains("Body = BuildBody();", view);
        Assert.Contains("Footer = BuildFooter();", view);
        Assert.DoesNotContain("_close", view);
        Assert.Contains("BuildFooter(footer), DialogFrame.ErrorStrip(new TextBlock())", WindowSource());
        Assert.Contains("_view.ChromeChanged += OnChromeChanged;", WindowSource());
        Assert.Contains("_view.ChromeChanged -= OnChromeChanged;", WindowSource());
    }

    [Fact]
    public void ActivationReadsOnlyTheLocalWatcherListAndNeverRepeatsItOnReattachment()
    {
        string view = ViewSource();
        string activate = Between(view, "internal Task ActivateAsync()", "protected override void OnAttachedToVisualTree(");
        Assert.Contains("if (_closed) return Task.CompletedTask;", activate);
        Assert.Contains("FocusSearchInput();", activate);
        Assert.Contains("if (_attached) _clock.Start();", activate);
        Assert.Contains("return _initialization ??= _model.InitializeAsync();", activate);
        string attach = Between(view, "protected override void OnAttachedToVisualTree(", "protected override void OnDetachedFromVisualTree(");
        Assert.Contains("Render(force: true);", attach);
        Assert.Contains("if (_activated) _clock.Start();", attach);
        Assert.DoesNotContain("InitializeAsync(", attach);
        Assert.DoesNotContain("SubmitAsync(", activate + attach);
        Assert.DoesNotContain("RefreshAsync(", activate + attach);
    }

    [Fact]
    public void TransientDetachStopsOnlyTheClockAndFinalDisposeUnsubscribesAndRespectsOwnership()
    {
        string view = ViewSource();
        string detach = Between(view, "protected override void OnDetachedFromVisualTree(", "internal BooruWorkbenchModel Model");
        Assert.Contains("_attached = false;", detach);
        Assert.Contains("_clock.Stop();", detach);
        Assert.DoesNotContain("Dispose(", detach);
        Assert.DoesNotContain("Changed -=", detach);
        Assert.DoesNotContain("Cancel(", detach);
        string dispose = view[view.IndexOf("public void Dispose()", StringComparison.Ordinal)..];
        foreach (string required in new[] { "if (_closed) return;", "_closed = true;", "_clock.Stop();", "_clock.Tick -= OnClockTick;", "ChromeChanged = null;",
            "Texts.Changed -= OnTextsChanged;", "_model.Search.Changed -= OnModelChanged;", "_model.Watchers.Changed -= OnModelChanged;",
            "foreach (CachedBitmap cached in _bitmaps.Values) cached.Bitmap.Dispose();", "_bitmaps.Clear();", "if (_ownsModel) _model.Dispose();" })
            Assert.Contains(required, dispose);
        string changed = Between(view, "private void OnModelChanged()", "private void OnTextsChanged(");
        Assert.Contains("Dispatcher.UIThread.Post(() =>", changed);
        Assert.Contains("if (!_closed) Render(force: false);", changed);
        // Queued updates stay valid through a temporary detach, but not after final disposal.
        Assert.DoesNotContain("_attached", changed);
    }

    [Fact]
    public void QueryClearAndEscapeDoNotSubmitAndKeepTheOriginalSafetyOrdering()
    {
        string host = WindowSource();
        Assert.Contains("DialogEscape.Attach(this, tryCancelLocal: CancelEscapeLocally);", host);
        Assert.Contains("private bool CancelEscapeLocally() =>", host);
        Assert.Contains("ShortcutService.IsCapturing || _view.IsModeDropDownOpen || _view.TryCancelRemoval();", host);
        Assert.DoesNotContain("private void OnKeyDown", host);
        Assert.DoesNotContain("Key.Escape", host);

        // The field owns the first Escape while it has text; the shared dialog policy receives the empty-field level.
        string searchBox = ControlSource("SearchBox.cs");
        Assert.Contains("if (HasText) { ClearAndFocus(); e.Handled = true; return; }", searchBox);
        string clear = Between(ViewSource(), "internal bool TryClearQuery()", "// ---- render");
        Assert.Contains("_input.ClearAndFocus();", clear);
        Assert.Contains("_model.Search.SetInput(_input.Text);", clear);
        Assert.Contains("!ReferenceEquals(_tabs.SelectedItem, _watchersTab)", clear);
        Assert.Contains("_model.Watchers.CancelRemoval(asking.WatcherId);", clear);
        foreach (string forbidden in new[] { "SubmitAsync(", "RetryAsync(", "LoadOriginalAsync(", "CheckNowAsync(", "Search.Cancel(" })
            Assert.DoesNotContain(forbidden, clear);
    }

    [Fact]
    public void RequestButtonsKeepDelegatingToTheExistingGuardedModels()
    {
        string view = ViewSource();
        foreach (string required in new[] { "_resetCooldown.Click += async (_, _) => await _model.Watchers.ResetProviderCooldownAsync();",
            "_submit.IsEnabled = model.CanSubmit;", "_retry.IsEnabled = !paused;", "load.IsEnabled = row.CanLoad;",
            "_cancel.Click += (_, _) => _model.Search.Cancel();", "yes.IsEnabled = !row.Busy;",
            "yes.Click += async (_, _) => await model.ConfirmRemovalAsync(watcher.WatcherId);",
            "BooruWatcherBlock checkBlock = model.CheckBlock(row);", "model.ResumeBlock(row)", "model.PauseBlock(row)",
            "model.RemoveBlock(row)", "model.RestartBlock(row)", "block == BooruWatcherBlock.None" })
            Assert.Contains(required, view);
    }

    [Fact]
    public void FocusRecoveryAndPreviewBoundsSurviveTheNewVisualOwner()
    {
        string view = ViewSource();
        Assert.Contains("TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement()", view);
        Assert.Contains("ReferenceEquals(focused, TopLevel.GetTopLevel(this))", view);
        Assert.Contains("TopLevel.GetTopLevel(this)?.Clipboard", view);
        Assert.Contains("if (_closed) return;\n            Control? target", view.Replace("\r\n", "\n"));
        Assert.Contains("bytes.Length > MaximumPreviewBytes", view);
        Assert.Contains("(long)codec.Info.Width * codec.Info.Height > 40_000_000", view);
        Assert.Contains("if (cached is not null) cached.Bitmap.Dispose();", view);
        Assert.Contains("_bitmaps[id].Bitmap.Dispose();", view);
    }

    private static string Between(string source, string start, string end)
    {
        int first = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(first >= 0, "Missing start anchor: " + start);
        int last = source.IndexOf(end, first + start.Length, StringComparison.Ordinal);
        Assert.True(last > first, "Missing end anchor: " + end);
        return source[first..last];
    }

    private static string WindowSource() => Source("BooruWorkbenchWindow.cs");
    private static string ViewSource() => Source("BooruWorkbenchView.cs");
    private static string ControlSource(string file, [CallerFilePath] string path = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../../Desktop/Controls", file)));
    private static string Source(string file, [CallerFilePath] string path = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../../Desktop/Views/Booru", file)));
}
