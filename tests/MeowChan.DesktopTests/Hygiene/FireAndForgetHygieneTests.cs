using System.Runtime.CompilerServices;
using System.Text;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests.Hygiene;

// Bug-class audit 16.11.2026, class 9 (fire-and-forget) and the settings latch of class 2.
//
// A task that is discarded (`_ = SomethingAsync(...)`) loses its failure: the key or click then simply does nothing, and the finalizer logs it
// long after the fact (UnhandledErrors.OnUnobservedTask). An `async void` without a catch skips the per-operation context and leaves busy
// flags set. Both are scanned over Desktop/**/*.cs and compared with the checked-in baseline hygiene-baseline.txt (today's debt):
//
//   * A discard or unguarded async void that is not in the baseline fails: start it through OpenActions.Start (or await it in a handler
//     that wraps it in OpenActions.RunAsync / try-catch with UnhandledErrors.TryRecover).
//   * A baseline entry that no longer occurs fails BaselineHasNoStaleEntries: the debt shrank, so regenerate the baseline. The list can
//     only get shorter.
//
// Regenerate after burning entries down (writes hygiene-baseline.txt in the source tree):
//   MEOWCHAN_HYGIENE_UPDATE=1 dotnet test tests/MeowChan.DesktopTests --filter FullyQualifiedName~FireAndForgetHygiene
public sealed class FireAndForgetHygieneTests
{
    private const string UpdateVariable = "MEOWCHAN_HYGIENE_UPDATE";
    private const string BaselineFile = "tests/MeowChan.DesktopTests/Hygiene/hygiene-baseline.txt";

    // Reviewed entries that stay in the baseline for a stated reason; printed into the baseline's header when it is regenerated.
    private static readonly string[] Reviewed =
    {
        "Desktop/GalleryVideoPlayer*.cs: every discard is a native command (CommandAsync reports its failure itself: FailAsync shows the unavailable status and raises",
        "  PlaybackFailed), a lifecycle task that owns its own catch (RestartAfterEndAsync, RefreshAudioStateAsync, SyncNativeRotationAsync, LoadScrubFrameAsync) or the",
        "  cleanup coordinator (RetireAsync, StopAsync: reported through the cleanup watch). The keys reach the player through GalleryViewer, which is burned down after",
        "  the shell and reader packages have merged.",
        "Desktop/ReaderInlineVideoSlot.cs: the remaining discards are lifecycle (release, viewport pause, live mute) and surface their failure in the slot's error line.",
        "Desktop/GalleryViewer.cs and Desktop/Views/Archive/ArchivePage.InlineVideo.cs: key-triggered, converted after the shell and reader packages have merged.",
    };

    [Fact]
    public void NoNewDiscardedTasks() => AssertNoNew(HygieneScanner.DiscardedTask,
        "A task is discarded (`_ = ...Async(`): its failure would reach nobody. Start it with OpenActions.Start(() => ..., \"operation.name\") " +
        "(logs and shows the failure in the error banner) and reset busy flags in a finally.");

    [Fact]
    public void NoNewUnguardedAsyncVoid() => AssertNoNew(HygieneScanner.UnguardedAsyncVoid,
        "An async void handler has neither a try nor UnhandledErrors.TryRecover nor OpenActions.RunAsync: its failure skips the operation context " +
        "and leaves busy flags set. Wrap the body in OpenActions.RunAsync(() => ..., \"operation.name\") and reset busy flags in a finally.");

    private static void AssertNoNew(string rule, string advice)
    {
        Dictionary<(string, string), int> baseline = Baseline();
        List<string> fresh = new();
        foreach (HygieneScanner.Finding finding in Scan().Where(f => f.Rule == rule))
        {
            baseline.TryGetValue((rule, finding.Key), out int allowed);
            if (finding.Count > allowed) fresh.Add($"  {finding.Key}: {finding.Count} now, {allowed} in hygiene-baseline.txt");
        }
        Assert.True(fresh.Count == 0, advice + "\n" + string.Join("\n", fresh));
    }

    [Fact]
    public void BaselineHasNoStaleEntries()
    {
        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1") WriteBaseline();
        Dictionary<(string, string), int> current = Scan().ToDictionary(f => (f.Rule, f.Key), f => f.Count);
        List<string> stale = new();
        foreach (((string rule, string key), int allowed) in Baseline())
        {
            current.TryGetValue((rule, key), out int actual);
            if (actual < allowed) stale.Add($"  {rule} {key}: baseline {allowed}, now {actual}");
        }
        Assert.True(stale.Count == 0,
            "hygiene-baseline.txt lists fire-and-forget debt that is fixed. Regenerate it so the debt ratchets down: "
            + $"{UpdateVariable}=1 dotnet test tests/MeowChan.DesktopTests --filter FullyQualifiedName~FireAndForgetHygiene\n" + string.Join("\n", stale));
    }

    [Fact]
    public void ScanSeesTheDesktopSources()
    {
        // Guards against the scan silently finding nothing (moved folders, a broken pattern).
        Assert.True(Scan().Where(f => f.Rule == HygieneScanner.DiscardedTask).Sum(f => f.Count) > 100);
        Assert.Contains(Scan(), f => f.Rule == HygieneScanner.UnguardedAsyncVoid);
    }

    [Fact]
    public void TheKeyTriggeredDiscardsOfTheQuickTagSurfaceAndTheInlineVideoSlotWereConverted()
    {
        Dictionary<(string, string), int> now = Scan().ToDictionary(f => (f.Rule, f.Key), f => f.Count);
        Assert.DoesNotContain(now.Keys, key => key.Item2.StartsWith("Desktop/Controls/QuickTagSurface.cs", StringComparison.Ordinal));
        // What remains in the slot is lifecycle (release, viewport pause, live mute): the key, click and activator paths start through OpenActions.
        string slot = Source("Desktop/ReaderInlineVideoSlot.cs");
        foreach (string expected in new[]
        {
            "OpenActions.Start(() => ExecuteAsync(action), \"reader.video.key\")",
            "OpenActions.Start(ToggleAsync, \"reader.video.toggle\")",
            "OpenActions.Start(() => PlayAsync(), \"reader.video.play\")",
        })
            Assert.Contains(expected, slot);
        Assert.DoesNotContain("_ = ExecuteAsync(", slot);
        Assert.DoesNotContain("_ = ToggleAsync(", slot);
    }

    // ---- the scanner itself ----

    [Theory]
    [InlineData("_ = LoadAsync(x);", 1)]
    [InlineData("_ = page?.Reader.OpenPostAsync(id, token);", 1)]
    [InlineData("_ = Run<int>.StartAsync<T>(x);", 1)]
    [InlineData("_ = StartAsync<T>(x);", 1)]
    [InlineData("var _ = FooAsync();", 1)] // still a discarded task
    [InlineData("x_ = FooAsync();", 0)]
    [InlineData("Task t = FooAsync();", 0)]
    [InlineData("_ = Foo(); // _ = BarAsync();", 0)]
    [InlineData("string s = \"_ = BarAsync(\";", 0)]
    [InlineData("OpenActions.Start(() => FooAsync(), \"x\");", 0)]
    [InlineData("button.Click += (_, _) => FooAsync();", 0)]
    [InlineData("Func<int, Task> f = _ => FooAsync();", 0)]
    public void TheDiscardPatternFindsDiscardedAsyncCalls(string code, int expected) =>
        Assert.Equal(expected, HygieneScanner.ScanFile("Desktop/X.cs", code).Where(f => f.Rule == HygieneScanner.DiscardedTask).Sum(f => f.Count));

    [Fact]
    public void AnAsyncVoidNeedsATryTryRecoverOrOpenActions()
    {
        const string source = """
            class C
            {
                async void Plain(object? s, EventArgs e) { await Task.Delay(1); busy = false; }
                async void Expr(object? s, EventArgs e) => await Task.Delay(1);
                async void Guarded(object? s, EventArgs e) { try { await Task.Delay(1); } finally { busy = false; } }
                async void Recovered(object? s, EventArgs e) { try { await Task.Delay(1); } catch (Exception x) when (UnhandledErrors.TryRecover(x, "a")) { } }
                async void Wrapped(object? s, EventArgs e) => await OpenActions.RunAsync(() => Task.Delay(1), "a");
                async void InString(object? s, EventArgs e) { var t = "try { }"; await Task.Delay(1); } // try
                async void Generic(object? s, Func<int, int> f) { await Task.Delay(f(1)); }
            }
            """;
        string[] unguarded = HygieneScanner.ScanFile("Desktop/C.cs", source).Where(f => f.Rule == HygieneScanner.UnguardedAsyncVoid).Select(f => f.Key).ToArray();
        Assert.Equal(new[] { "Desktop/C.cs::Plain", "Desktop/C.cs::Expr", "Desktop/C.cs::InString", "Desktop/C.cs::Generic" }, unguarded);
    }

    [Fact]
    public void TheSanitizerBlanksCommentsAndLiteralsButKeepsLengthAndLines()
    {
        const string source = "a // _ = XAsync();\n/* _ = YAsync(); */ b \"_ = ZAsync(\" @\"q\"\"_ = WAsync(\" $\"{c}\" 'x' '\\'' \"\"\"raw \" _ = VAsync(\"\"\" d";
        string sanitized = HygieneScanner.Sanitize(source);
        Assert.Equal(source.Length, sanitized.Length);
        Assert.Equal(source.Count(c => c == '\n'), sanitized.Count(c => c == '\n'));
        Assert.DoesNotContain("Async", sanitized);
        Assert.Contains("a ", sanitized);
        Assert.Contains(" b ", sanitized);
        Assert.EndsWith(" d", sanitized);
    }

    // ---- class 2: the settings search's Enter latch ----

    [Fact]
    public void ADeactivatedWindowEndsAHeldEnterPressOfTheSettingsSearch()
    {
        SettingsSearchEnterGuard guard = new();
        Assert.False(guard.SwallowKeyDown(Avalonia.Input.Key.Enter));
        guard.Arm();
        Assert.True(guard.SwallowKeyDown(Avalonia.Input.Key.Enter)); // Alt-Tab with Enter held: the key-up goes to another window
        guard.OnWindowDeactivated(null, EventArgs.Empty);
        Assert.False(guard.IsArmed);
        Assert.False(guard.SwallowKeyDown(Avalonia.Input.Key.Enter)); // the next Enter is a fresh press and acts
    }

    [Fact]
    public void TheSettingsEditorFollowsItsWindowWhileAttached()
    {
        string search = Source("Desktop/Views/Settings/SettingsEditor.Search.cs");
        Assert.Contains("AttachedToVisualTree += (_, _) => _searchEnterGuard.Follow(TopLevel.GetTopLevel(this) as Window);", search);
        Assert.Contains("DetachedFromVisualTree += (_, _) => { _searchEnterGuard.Follow(null); _searchEnterGuard.Reset(); };", search);
        string guard = Source("Desktop/SettingsSearchEnterGuard.cs");
        Assert.Contains("_window.Deactivated += OnWindowDeactivated;", guard);
        Assert.Contains("_window.Deactivated -= OnWindowDeactivated;", guard);
    }

    // ---- plumbing ----

    private static IReadOnlyList<HygieneScanner.Finding>? _scan;
    private static IReadOnlyList<HygieneScanner.Finding> Scan() => _scan ??= HygieneScanner.ScanRepository(Root());

    private static Dictionary<(string, string), int> Baseline()
    {
        Dictionary<(string, string), int> result = new();
        foreach (string line in File.ReadAllLines(Path.Combine(Root(), BaselineFile)))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            string[] parts = line.Split('\t');
            Assert.True(parts.Length == 3, "Malformed baseline line: " + line);
            result[(parts[0], parts[1])] = int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
        }
        return result;
    }

    private static void WriteBaseline()
    {
        StringBuilder sb = new();
        sb.Append("# Fire-and-forget hygiene baseline (bug-class audit 16.11.2026, class 9): today's accepted debt.\n");
        sb.Append("# rule<TAB>file or file::method<TAB>count. Generated; do not edit by hand. The tests fail on anything not listed here and on entries\n");
        sb.Append("# that no longer occur. Regenerate after burning entries down:\n");
        sb.Append("#   MEOWCHAN_HYGIENE_UPDATE=1 dotnet test tests/MeowChan.DesktopTests --filter FullyQualifiedName~FireAndForgetHygiene\n");
        sb.Append("# discarded-task: `_ = ...Async(` whose failure reaches nobody. async-void-unguarded: an async void without try, TryRecover or OpenActions.\n");
        sb.Append("# Reviewed entries that stay for a reason:\n");
        foreach (string note in Reviewed) sb.Append("#   ").Append(note).Append('\n');
        foreach (HygieneScanner.Finding f in Scan())
            sb.Append(f.Rule).Append('\t').Append(f.Key).Append('\t').Append(f.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        File.WriteAllText(Path.Combine(Root(), BaselineFile), sb.ToString());
    }

    private static string Root([CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../../.."));

    private static string Source(string relative) => File.ReadAllText(Path.Combine(Root(), relative));
}
