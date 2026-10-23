using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;

namespace MeowChan.DesktopSmoke;

/// <summary>A disposable bounded control walkthrough, not an alternative product or archive format.</summary>
internal sealed class PostBookmarkRivalWalkthrough : Window
{
    private static readonly string[] Variants = ["Reference + one note", "Snapshot V1 + one note", "Two independent notes", "Delete-coupled reference + note"];
    private static readonly string[] Tasks = ["Read original after source V2", "Edit second motive independently", "Delete source P"];
    private readonly ComboBox _variant = new() { ItemsSource = Variants, SelectedIndex = 0 };
    private readonly ComboBox _task = new() { ItemsSource = Tasks, SelectedIndex = 0 };
    private readonly ComboBox _target = new() { ItemsSource = new[] { "P = 4chan /g/11100 >>11101", "Q = 4chan /vg/11100 >>11101" }, SelectedIndex = 0 };
    private readonly ComboBox _card = new() { ItemsSource = new[] { "Combined note / first card", "Second independent card" }, SelectedIndex = 0 };
    private readonly TextBox _prediction = new() { PlaceholderText = "Prediction before revealing result", AcceptsReturn = true, MinHeight = 55 };
    private readonly TextBox _draft = new() { Text = "Reason B revised independently", AcceptsReturn = true, MinHeight = 55 };
    private readonly TextBlock _preview = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly TextBlock _before = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly TextBlock _result = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly Button _confirm = new() { Content = "Confirm / reveal" }, _abort = new() { Content = "Abort" };
    private readonly Action<object> _log;
    private bool _sourceAvailable = true, _noteAvailable = true;
    private string _first = "Reason A: investigate original wording.", _second = "Reason B: use in another project.";
    private int _turn;
    internal string LastResult { get; private set; } = "not-run";
    private string State => $"sourceP={(_sourceAvailable ? "V2" : "missing")}; ownNote={(_noteAvailable ? _first + " | " + _second : "absent")}; Q=unchanged";

    private PostBookmarkRivalWalkthrough(Action<object> log)
    {
        _log = log; Title = "URSA20 bounded fixture walkthrough — harness, no human judgment";
        Width = 720; Height = 700;
        StackPanel fields = new() { Margin = new Thickness(16), Spacing = 8, Children =
        {
            new TextBlock { Text = "Disposable P/V1/V2 + two motives. No live product notes; no timing/AT comparison.", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
            new TextBlock { Text = "Variant" }, _variant, new TextBlock { Text = "Task" }, _task,
            new TextBlock { Text = "Exact target" }, _target, new TextBlock { Text = "Note card" }, _card,
            _before, _preview, _prediction, _draft,
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _confirm, _abort } }, _result
        } };
        Content = new ScrollViewer { Content = fields };
        _confirm.Click += (_, _) => Reveal(abort: false);
        _abort.Click += (_, _) => Reveal(abort: true);
        _variant.SelectionChanged += (_, _) => UpdatePreview();
        _task.SelectionChanged += (_, _) => UpdatePreview();
        Reset();
    }

    private void Reset()
    {
        _sourceAvailable = _noteAvailable = true;
        _first = "Reason A: investigate original wording."; _second = "Reason B: use in another project.";
        _prediction.Text = ""; _result.Text = ""; LastResult = "not-run"; _before.Text = State; UpdatePreview();
    }

    private void UpdatePreview() => _preview.Text = _task.SelectedIndex == 2
        ? _variant.SelectedIndex == 3 ? "Preview: confirm removes source P AND both own motives; Abort retains all."
            : "Preview: confirm removes source P; own motives remain. Abort retains all."
        : _task.SelectedIndex == 1 ? "Preview: only the two-note variant has an independent second-card key."
            : "Preview: snapshot exposes the saved V1 copy; a reference exposes current V2.";

    private void Reveal(bool abort)
    {
        if (string.IsNullOrWhiteSpace(_prediction.Text))
        { LastResult = "prediction-required"; _result.Text = "Enter a prediction before revealing a result."; return; }
        string before = State;
        // The observer selected these actual controls and entered a prediction before this event.
        _log(new { task = Tasks[_task.SelectedIndex], variant = Variants[_variant.SelectedIndex], observer_kind = "harness",
            input_method = "in-process ComboBox.SelectedIndex/TextBox.Text then actual Button.Click event",
            turn = ++_turn, phase = "prediction-before-result", start_state = before,
            actual_actions = new[] { "select Variant.SelectedIndex=" + _variant.SelectedIndex, "select Task.SelectedIndex=" + _task.SelectedIndex,
                "select ExactTarget.SelectedIndex=" + _target.SelectedIndex, "select NoteCard.SelectedIndex=" + _card.SelectedIndex, "edit prediction TextBox.Text" },
            prediction = _prediction.Text, preview = _preview.Text, selected_target = _target.SelectedIndex == 0 ? "P" : "Q",
            selected_card = _card.SelectedIndex, raw_draft = _draft.Text, requested_action = abort ? "abort" : "confirm",
            focus_before = FocusManager?.GetFocusedElement()?.GetType().Name });
        if (abort) LastResult = "aborted-no-change";
        else if (_target.SelectedIndex != 0) LastResult = "target-Q-not-the-requested-P-no-change";
        else if (_task.SelectedIndex == 0)
            LastResult = _variant.SelectedIndex == 1 ? "original-V1-preserved" : "unsupported-original-copy-current-source-V2";
        else if (_task.SelectedIndex == 1)
        {
            if (_variant.SelectedIndex != 2 || _card.SelectedIndex != 1)
                LastResult = "unsupported-independent-second-note-key";
            else { _second = _draft.Text ?? ""; LastResult = "second-card-edited-first-card-unchanged"; }
        }
        else
        {
            _sourceAvailable = false;
            if (_variant.SelectedIndex == 3) _noteAvailable = false;
            LastResult = _noteAvailable ? "source-missing-own-work-retained" : "source-and-own-work-removed-after-confirm";
        }
        _result.Text = LastResult + "\n" + State;
        _log(new { task = Tasks[_task.SelectedIndex], variant = Variants[_variant.SelectedIndex], phase = "result-after-control-event",
            result = LastResult.StartsWith("unsupported", StringComparison.Ordinal) ? "unsupported-by-design" : "pass",
            revealed_result = LastResult, before, after = State,
            actual_actions = new[] { abort ? "Abort Button.Click" : "Confirm Button.Click" },
            focus_after = FocusManager?.GetFocusedElement()?.GetType().Name,
            storage_scope = "in-memory disposable fixture only; product bytes untouched", human_judgment_open = true, lifetime_choice_open = true });
    }

    internal static async Task RunOriginalAsync(Window owner, Action<object> log)
    {
        PostBookmarkRivalWalkthrough host = new(log); host.Show(owner);
        try
        {
            // Snapshot precedes current UI; the later note/deletion walkthrough follows it.
            await host.ChooseAsync(variant: 1, task: 0, card: 0, target: 0,
                "Snapshot should reveal the stored V1 quotation, while live P is V2.", abort: false, expected: "original-V1-preserved");
            host.Reset();
            await host.ChooseAsync(variant: 0, task: 0, card: 0, target: 1,
                "Q's similar ID is a different full identity; it must not satisfy P's task.", abort: false, expected: "target-Q-not-the-requested-P-no-change");
            await host.ChooseAsync(variant: 0, task: 0, card: 0, target: 0,
                "Reference exposes current V2 and cannot recover the original V1 quotation.", abort: false, expected: "unsupported-original-copy-current-source-V2");
        }
        finally { host.Close(); }
    }

    internal static async Task RunNotesAndDeletionAsync(Window owner, Action<object> log)
    {
        PostBookmarkRivalWalkthrough host = new(log); host.Show(owner);
        try
        {
            await host.ChooseAsync(variant: 0, task: 1, card: 1, target: 0,
                "One combined note has no second independent note identity.", abort: false, expected: "unsupported-independent-second-note-key");
            host.Reset();
            await host.ChooseAsync(variant: 2, task: 1, card: 1, target: 0,
                "Cancel editing card2 must keep both original motives.", abort: true, expected: "aborted-no-change");
            await host.ChooseAsync(variant: 2, task: 1, card: 1, target: 0,
                "Confirm edits only card2; first motive remains byte-identical.", abort: false, expected: "second-card-edited-first-card-unchanged");
            if (host._first != "Reason A: investigate original wording." || host._second != "Reason B revised independently")
                throw new InvalidOperationException("Two-note fixture changed the wrong motive.");
            host.Reset();
            await host.ChooseAsync(variant: 3, task: 2, card: 0, target: 0,
                "Abort coupled deletion retains source and own notes.", abort: true, expected: "aborted-no-change");
            if (!host._sourceAvailable || !host._noteAvailable) throw new InvalidOperationException("Abort deleted fixture data.");
            await host.ChooseAsync(variant: 3, task: 2, card: 0, target: 0,
                "Confirm coupled deletion makes original own work unavailable as well as P.", abort: false, expected: "source-and-own-work-removed-after-confirm");
            if (host._noteAvailable) throw new InvalidOperationException("Coupled deletion did not expose its retention tradeoff.");
            host.Reset();
            await host.ChooseAsync(variant: 0, task: 2, card: 0, target: 0,
                "Reference deletes P's source but keeps both motives for later editing.", abort: false, expected: "source-missing-own-work-retained");
            if (!host._noteAvailable || host._sourceAvailable) throw new InvalidOperationException("Reference retention fixture is wrong.");
        }
        finally { host.Close(); }
    }

    private async Task ChooseAsync(int variant, int task, int card, int target, string prediction, bool abort, string expected)
    {
        _variant.SelectedIndex = variant; _task.SelectedIndex = task; _target.SelectedIndex = target; _card.SelectedIndex = card;
        _prediction.Text = prediction; // A recorded harness prediction, never a fabricated human response.
        Button action = abort ? _abort : _confirm; action.Focus();
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Loaded);
        action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (LastResult != expected) throw new InvalidOperationException("Walkthrough result differs from pre-action prediction: " + LastResult);
    }
}
