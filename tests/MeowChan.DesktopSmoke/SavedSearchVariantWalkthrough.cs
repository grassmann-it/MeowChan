using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;

namespace MeowChan.DesktopSmoke;

/// <summary>Bounded interactive fixture reveal, not a second query/store implementation.</summary>
internal static class SavedSearchVariantWalkthrough
{
    private static readonly string[] Variants = ["Opt-in startup", "Live binding", "Relative seven days", "Editor template"];
    private static readonly string[] Operations = ["Edit filter to B", "Load saved target", "Transition T0 to T1", "Start with missing saved target", "Anchor 26 October", "Anchor 2 November", "Search editor"];
    private sealed record State(string Saved, string Draft, string Applied, string Page, string[] Ids, string? Dates = null);

    internal static async Task RunAsync(MainWindow owner, string profile, string fixtureDigest, string[] t0, string[] t1,
        Action<string, string, object?> log)
    {
        string savedFile = Path.Combine(profile, "archive-searches.json");
        string beforeHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(savedFile)));
        var variant = new ComboBox { Name = "WalkthroughVariant", ItemsSource = Variants, HorizontalAlignment = HorizontalAlignment.Stretch };
        var operation = new ComboBox { Name = "WalkthroughOperation", ItemsSource = Operations, HorizontalAlignment = HorizontalAlignment.Stretch };
        var prediction = new TextBox { Name = "WalkthroughPrediction", PlaceholderText = "Prediction before Confirm", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var confirm = new Button { Name = "WalkthroughConfirm", Content = "Confirm / reveal fixture transition" };
        var cancel = new Button { Name = "WalkthroughCancel", Content = "Cancel / preserve current state" };
        var output = new TextBlock { Name = "WalkthroughState", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var window = new Window
        {
            Title = "URSA-18 — bounded fixture alternatives (harness observation)", Width = Math.Min(700, owner.ClientSize.Width),
            Height = Math.Min(560, owner.ClientSize.Height), WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = new StackPanel { Margin = new Thickness(16), Spacing = 8, Children =
            {
                new TextBlock { Text = "Choose variant and operation, write a prediction, then Confirm or Cancel. This reveals predetermined fixture states only; it never writes the product profile.", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                variant, operation, prediction, new WrapPanel { Children = { confirm, cancel } }, output
            } } }
        };
        State Initial() => new("A: Board + text", "UNSEARCHED B", "A: Board + text", "Archive", t0);
        State state = Initial(); bool currentT1 = false; int transitions = 0;
        string Snapshot() => JsonSerializer.Serialize(state);
        void Render() => output.Text = "Fixture " + fixtureDigest + "\n" + Snapshot();
        void Event(string action, string expected) => log(action, expected, new
        {
            variant = variant.SelectedItem, operation = operation.SelectedItem, observerKind = "harness", input = "in-process control event",
            fixtureDigest, prediction = prediction.Text, state, focus = window.FocusManager?.GetFocusedElement()?.ToString(), persistedHash = beforeHash
        });
        variant.SelectionChanged += (_, _) => { state = Initial(); currentT1 = false; Render(); Event("observed variant reset", "Identical T0, applied A, unsearched B"); };
        operation.SelectionChanged += (_, _) => Event("observed operation choice", "Choice alone does not reveal/change the fixture");
        cancel.Click += (_, _) => { Event("observed Cancel", "Dirty draft and all fixture state unchanged"); Render(); };
        confirm.Click += (_, _) =>
        {
            if (variant.SelectedIndex < 0 || operation.SelectedIndex < 0 || string.IsNullOrWhiteSpace(prediction.Text))
                throw new InvalidOperationException("A variant, operation and pre-action prediction are required.");
            int v = variant.SelectedIndex, op = operation.SelectedIndex;
            string[] currentIds = currentT1 ? t1 : t0;
            state = op switch
            {
                0 when v == 1 => new("B: edited filter", "EDITED B", "B: edited filter", "Archive", []),
                0 => state with { Draft = "EDITED B" },
                1 when v == 3 => state with { Draft = "A: Board + text" },
                1 when v == 0 => new("A: Board + text", "A: Board + text", "A: Board + text", "Archive", t1),
                1 => state with { Draft = state.Saved, Applied = state.Saved, Ids = currentIds },
                2 => state with { Ids = v == 1 ? (state.Applied.StartsWith("B:", StringComparison.Ordinal) ? [] : t1) : state.Ids },
                3 when v == 0 => new("missing saved target", state.Draft, "none", "Downloads (missing target explained)", []),
                4 when v == 2 => new("relative: last 7 local days", state.Draft, "date fixture", "Archive",
                    ["savedfixture/d/00", "savedfixture/d/01", "savedfixture/d/02", "savedfixture/d/03"], "2026-10-20 through 2026-10-26, Europe/Berlin"),
                5 when v == 2 => new("relative: last 7 local days", state.Draft, "date fixture", "Archive",
                    ["savedfixture/d/04"], "2026-10-27 through 2026-11-02, Europe/Berlin"),
                6 when v == 3 => state with { Applied = state.Draft, Ids = currentIds },
                _ => throw new InvalidOperationException("Operation is outside this bounded variant's walkthrough.")
            };
            if (op == 2 || op == 1 && v == 0) currentT1 = true;
            transitions++; Render(); Event("observed Confirm", "Deterministic result revealed after explicit choice and confirmation");
        };
        window.Show(owner);
        try
        {
            await Program.WaitUntilAsync(() => window.IsVisible && confirm.IsEffectivelyVisible);
            async Task ResetAsync(int v)
            {
                log("select variant", "Restore identical T0 and raw B before variant " + Variants[v], new { fixtureDigest, control = variant.Name });
                variant.Focus(NavigationMethod.Tab); variant.SelectedIndex = -1; variant.SelectedIndex = v;
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                Check(state.Draft == "UNSEARCHED B" && state.Ids.SequenceEqual(t0), "Variant did not reset fixture.");
            }
            async Task ActAsync(int op, string expected, bool abort = false)
            {
                log("select operation", "Selection alone preserves state; next expected: " + expected, new { fixtureDigest, control = operation.Name, operation = Operations[op], state });
                string beforeChoice = Snapshot(); operation.Focus(NavigationMethod.Tab); operation.SelectedIndex = op;
                Check(Snapshot() == beforeChoice, "Choice alone changed fixture state.");
                log("edit prediction", expected, new { fixtureDigest, control = prediction.Name, before = prediction.Text, after = expected });
                prediction.Focus(NavigationMethod.Tab); prediction.Text = expected;
                Button action = abort ? cancel : confirm;
                log(abort ? "click Cancel" : "click Confirm", expected, new { fixtureDigest, control = action.Name, state });
                action.Focus(NavigationMethod.Tab); SmokeActionClick.Execute(action);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                if (abort) Check(Snapshot() == beforeChoice, "Cancel changed fixture state.");
            }
            // Real Cancel/Confirm and edits for each alternative; measured actions come from events,
            // never from a hardcoded action-count score. Each task starts from the same T0.
            for (int v = 0; v < Variants.Length; v++)
            {
                await ResetAsync(v);
                await ActAsync(1, "Cancel loading A preserves raw UNSEARCHED B, displayed T0, stored A", abort: true);
                await ActAsync(0, v == 1 ? "Live edit changes stored/applied model to B and displays zero matches" : "Editing changes only raw B; stored A and displayed T0 remain");
                Check(v == 1 ? state.Saved == "B: edited filter" && state.Ids.Length == 0 : state.Saved == "A: Board + text" && state.Ids.SequenceEqual(t0), "Saved-versus-edited reveal differs from prediction.");
                await ResetAsync(v);
                if (v == 0)
                {
                    await ActAsync(1, "Opt-in startup applies A against T1: remove b00, add b65, raw becomes A");
                    Check(state.Ids.SequenceEqual(t1) && state.Draft == "A: Board + text", "Startup reveal differs from T1.");
                    await ResetAsync(v);
                    await ActAsync(3, "Missing startup target keeps Downloads with explanation and preserves raw B");
                    Check(state.Page.StartsWith("Downloads", StringComparison.Ordinal) && state.Draft == "UNSEARCHED B", "Missing startup target reveal lost raw draft.");
                }
                else if (v == 1)
                {
                    await ActAsync(2, "Live T0→T1 updates displayed membership without another Apply; saved A stays A");
                    Check(state.Ids.SequenceEqual(t1) && state.Saved == "A: Board + text", "Live update reveal differs from T1.");
                }
                else if (v == 2)
                {
                    await ActAsync(4, "Anchor 26 Oct gives inclusive 20–26 Oct Europe/Berlin: d00,d01,d02,d03");
                    Check(state.Dates == "2026-10-20 through 2026-10-26, Europe/Berlin" && state.Ids.Length == 4, "First relative boundary reveal differs.");
                    await ActAsync(5, "Anchor 2 Nov moves to inclusive 27 Oct–2 Nov Europe/Berlin: d04 only");
                    Check(state.Dates == "2026-10-27 through 2026-11-02, Europe/Berlin" && state.Ids.SequenceEqual(new[] { "savedfixture/d/04" }), "Second relative boundary reveal differs.");
                }
                else
                {
                    await ActAsync(2, "Archive moves to T1; editor-only variant still displays prior T0 until Search");
                    Check(state.Ids.SequenceEqual(t0), "Editor template silently reran search.");
                    await ActAsync(1, "Loading template replaces dirty B only after explicit Confirm; displayed T0 remains");
                    Check(state.Draft == "A: Board + text" && state.Ids.SequenceEqual(t0), "Template load reran search.");
                    await ActAsync(6, "Separate Search applies loaded draft A against current T1");
                    Check(state.Applied == "A: Board + text" && state.Ids.SequenceEqual(t1), "Editor Search reveal differs from T1.");
                }
                Program.CaptureWindow(window, "smoke-saved-search-variant-" + v + ".png");
            }
            Check(beforeHash == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(savedFile))), "Variant walkthrough wrote the product saved definitions.");
            log("walkthrough complete", "Interaction reveal completed; human preference/native input/AT/IME remain open", new
            { fixtureDigest, transitions, persistedBefore = beforeHash, persistedAfter = beforeHash, interpretation = "retain-provisionally; preference undecided", nativeAtImeOpen = true, humanJudgmentOpen = true });
        }
        finally { window.Close(); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
