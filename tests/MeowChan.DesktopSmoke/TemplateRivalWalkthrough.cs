using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace MeowChan.DesktopSmoke;

// Disposable interaction model only. No application services, rule stores or profile format.
internal sealed class TemplateRivalWalkthrough : StackPanel
{
    internal sealed record Board(string Id, string Types, int Megabytes, bool Enabled, string Filter);
    internal static readonly Board[] Initial = [new("a", "png + raw image/jpeg", 1, false, "compatible"),
        new("b", "jpg", 2, false, "compatible"), new("c", "gif", 3, true, "incompatible-v2")];
    private readonly string _variant;
    private readonly Dictionary<string, Board> _boards;
    internal IReadOnlyList<Board> InitialState { get; }
    private readonly HashSet<string> _bound = [];
    internal IReadOnlyList<Board> State => _boards.Values.OrderBy(board => board.Id).ToArray();
    internal readonly TextBox Prediction = new() { Name = "RivalPrediction", PlaceholderText = "Prediction before revealing the result" };
    internal readonly ComboBox Choice = new() { Name = "RivalChoice", ItemsSource = new[] { "Wallpaper jpg/png", "Video mp4/webm", "Custom webp + threshold=7" }, SelectedIndex = 0 };
    internal readonly CheckBox A = new() { Name = "RivalTargetA", Content = "Board a" };
    internal readonly CheckBox B = new() { Name = "RivalTargetB", Content = "Board b" };
    internal readonly CheckBox C = new() { Name = "RivalTargetC", Content = "Board c (prepared revision conflict for Bulk)" };
    internal readonly CheckBox ExcludeB = new() { Name = "RivalExcludeB", Content = "Exclude b from later linked changes" };
    internal readonly Button Confirm = new() { Name = "RivalConfirm", Content = "Confirm selected targets and reveal" };
    internal readonly Button Change = new() { Name = "RivalChange", Content = "Change linked Wallpaper to webp and reveal" };
    internal readonly Button Cancel = new() { Name = "RivalCancel", Content = "Cancel without changing boards" };
    internal readonly TextBlock Rows = new() { Name = "RivalRows", TextWrapping = TextWrapping.Wrap };
    internal readonly TextBlock PreviewRows = new() { Name = "RivalPreviewRows", TextWrapping = TextWrapping.Wrap };
    internal readonly TextBlock Result = new() { Name = "RivalResult", TextWrapping = TextWrapping.Wrap };
    internal string Outcome { get; private set; } = "not-run";

    internal TemplateRivalWalkthrough(string variant, IReadOnlyList<Board>? initial = null)
    {
        InitialState = (initial ?? Initial).ToArray(); _boards = InitialState.ToDictionary(board => board.Id);
        _variant = variant; Spacing = 10; Margin = new Avalonia.Thickness(16); HorizontalAlignment = HorizontalAlignment.Stretch;
        Children.Add(new TextBlock { Text = "URSA-19 limited " + variant + " walkthrough — harness, human judgment open", TextWrapping = TextWrapping.Wrap });
        Children.Add(new TextBlock { Text = "Same a/b/c starting values. Type a prediction, choose targets/option, then confirm or cancel. Bulk has a prepared c revision conflict; Preset offers fixed values only.", TextWrapping = TextWrapping.Wrap });
        Children.Add(Rows); Children.Add(Prediction); Children.Add(Choice);
        Children.Add(A); Children.Add(B); Children.Add(C); Children.Add(ExcludeB);
        Children.Add(PreviewRows); Children.Add(Confirm); Children.Add(Change); Children.Add(Cancel); Children.Add(Result);
        Change.IsVisible = ExcludeB.IsVisible = variant == "link";
        Choice.SelectionChanged += (_, _) => RenderPreview();
        foreach (CheckBox check in new[] { A, B, C, ExcludeB }) check.IsCheckedChanged += (_, _) => RenderPreview();
        Confirm.Click += (_, _) => Reveal(change: false);
        Change.Click += (_, _) => Reveal(change: true);
        Cancel.Click += (_, _) => { Outcome = "cancelled"; Result.Text = "Cancelled: all board values unchanged."; };
        Render();
    }
    private void Reveal(bool change)
    {
        if (string.IsNullOrWhiteSpace(Prediction.Text)) { Result.Text = "Record a prediction before revealing."; return; }
        if (_variant == "preset" && Choice.SelectedIndex == 2)
        {
            Outcome = "unsupported-by-design";
            Result.Text = "Fixed presets cannot express custom webp + threshold=7. An editable user-owned copy or separate editor is required; no board changed.";
            return;
        }
        string types = Choice.SelectedIndex == 1 ? "mp4/webm" : "jpg/png";
        var selected = new[] { ("a", A.IsChecked == true), ("b", B.IsChecked == true), ("c", C.IsChecked == true) }.Where(value => value.Item2).Select(value => value.Item1).ToArray();
        if (_variant == "link")
        {
            if (change)
            {
                if (ExcludeB.IsChecked == true) _bound.Remove("b");
                foreach (string id in _bound) _boards[id] = _boards[id] with { Types = "webp" };
                Result.Text = "Linked template changed. Updated: " + string.Join(",", _bound.Order()) + "; b retained its copied prior value when excluded.";
            }
            else
            {
                foreach (string id in selected) { _bound.Add(id); _boards[id] = _boards[id] with { Types = types }; }
                Result.Text = "Linked targets: " + string.Join(",", selected) + ". A later template change affects bound targets until explicitly excluded.";
            }
        }
        else
        {
            foreach (string id in selected.Where(id => _variant != "bulk" || id != "c")) _boards[id] = _boards[id] with { Types = types };
            Result.Text = _variant == "bulk" ? "a: success; b: success; c: revision conflict, unchanged." : "Fixed preset copied to selected boards.";
        }
        Outcome = "pass"; Render();
    }
    private void Render()
    {
        Rows.Text = string.Join(Environment.NewLine, State.Select(board =>
            $"{board.Id}: types={board.Types}; limit={board.Megabytes}MB; enabled={board.Enabled}; filter={board.Filter}"));
        RenderPreview();
    }
    private void RenderPreview()
    {
        string next = Choice.SelectedIndex == 1 ? "mp4/webm" : "jpg/png";
        PreviewRows.Text = "Confirm preview (limits/enabled/filter retained):" + Environment.NewLine + string.Join(Environment.NewLine,
            State.Select(board =>
            {
                bool selected = board.Id == "a" ? A.IsChecked == true : board.Id == "b" ? B.IsChecked == true : C.IsChecked == true;
                string result = !selected ? board.Types : _variant == "bulk" && board.Id == "c" ? board.Types + " (revision conflict)"
                    : _variant == "preset" && Choice.SelectedIndex == 2 ? board.Types + " (custom values unsupported)" : next;
                return board.Id + ": " + board.Types + " → " + result;
            }));
        if (_variant == "link" && _bound.Count > 0)
            PreviewRows.Text += Environment.NewLine + "Change linked template preview: " + string.Join(", ", State.Select(board =>
                board.Id + ": " + board.Types + " → " + (_bound.Contains(board.Id) && !(board.Id == "b" && ExcludeB.IsChecked == true) ? "webp" : board.Types)));
    }
}
