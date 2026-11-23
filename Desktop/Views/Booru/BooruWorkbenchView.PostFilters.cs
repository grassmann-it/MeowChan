using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MeowChan.Desktop.Localization;
using MeowChan.PluginSdk;

namespace MeowChan.Desktop.Booru;

internal sealed partial class BooruWorkbenchView
{
    private readonly ComboBox _filterRating = new() { Name = "BooruFilterRating" };
    private readonly ComboBox _filterMedia = new() { Name = "BooruFilterMedia" };
    private readonly CheckBox _filterScoreEnabled = new() { Name = "BooruFilterScoreEnabled" };
    private readonly NumericUpDown _filterScore = new()
    {
        Name = "BooruFilterScore", Minimum = MeowChan.Config.BooruPostFilterScoreMinimum,
        Maximum = MeowChan.Config.BooruPostFilterScoreMaximum, Increment = 1, FormatString = "0", Width = 150,
        ParsingNumberStyle = System.Globalization.NumberStyles.Integer, ClipValueToMinMax = true
    };
    private readonly Button _filterReset = new() { Name = "BooruFilterReset" };
    private readonly TextBlock _filterTitle = new();
    private readonly TextBlock _filterHint = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _filterCount = new() { Name = "BooruFilterCount" };
    private readonly TextBlock _filterEmpty = new() { Name = "BooruFilterEmpty", TextWrapping = TextWrapping.Wrap };
    private readonly Expander _filterTags = new() { Name = "BooruFilterTags" };
    private readonly WrapPanel _filterTagItems = new();
    private readonly TextBlock _filterTagsHint = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Dictionary<string, CheckBox> _filterTagChecks = new(StringComparer.Ordinal);
    private string? _filterTagsKey;
    private readonly TextBlock _filterRatingLabel = new();
    private readonly TextBlock _filterMediaLabel = new();

    internal Func<int>? PostFilterScoreDefault { get; set; }

    private StackPanel BuildPostFilters()
    {
        _filterRating.ItemsSource = new[] { new ComboBoxItem() }.Concat(Enum.GetValues<BooruRating>().Select(value => new ComboBoxItem { Tag = value })).ToArray();
        _filterMedia.ItemsSource = new[] { new ComboBoxItem() }.Concat(Enum.GetValues<BooruPostMediaType>().Select(value => new ComboBoxItem { Tag = value })).ToArray();
        _filterRating.SelectionChanged += (_, _) =>
        {
            if (!_applying) _model.Search.SetPostFilter(_model.Search.View.PostFilter with { Rating = (_filterRating.SelectedItem as ComboBoxItem)?.Tag as BooruRating? });
        };
        _filterMedia.SelectionChanged += (_, _) =>
        {
            if (!_applying) _model.Search.SetPostFilter(_model.Search.View.PostFilter with { MediaType = (_filterMedia.SelectedItem as ComboBoxItem)?.Tag as BooruPostMediaType? });
        };
        _filterScoreEnabled.Click += (_, _) => ApplyScoreFilter();
        _filterScore.ValueChanged += (_, _) => { if (!_applying && _filterScoreEnabled.IsChecked == true) ApplyScoreFilter(); };
        _filterReset.Click += (_, _) => _model.Search.ResetPostFilter();
        AutomationProperties.SetLiveSetting(_filterCount, AutomationLiveSetting.Polite);
        _filterTags.Content = new StackPanel { Spacing = 8, Children =
        {
            _filterTagsHint, new ScrollViewer { MaxHeight = 160, Content = _filterTagItems }
        } };
        return new StackPanel { Name = "BooruPostFilters", Spacing = 8, Children =
        {
            _filterTitle,
            new WrapPanel { Children =
            {
                FilterField(_filterRatingLabel, _filterRating), FilterField(_filterMediaLabel, _filterMedia),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 12, 8), Children = { _filterScoreEnabled, _filterScore } },
                _filterReset
            } },
            _filterTags, _filterHint, _filterCount, _filterEmpty
        } };
    }

    private static StackPanel FilterField(TextBlock label, Control input) => new StackPanel
    {
        Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 12, 8), Children = { label, input }
    };

    private void ApplyScoreFilter()
    {
        if (_applying) return;
        if (_filterScoreEnabled.IsChecked != true)
        {
            _model.Search.SetPostFilter(_model.Search.View.PostFilter with { MinimumScore = null });
            return;
        }
        if (_filterScore.Value is not { } value || value != decimal.Truncate(value)) return;
        _model.Search.SetPostFilter(_model.Search.View.PostFilter with { MinimumScore = _filterScoreEnabled.IsChecked == true ? (int)value : null });
    }

    /// <summary>The source snapshot stays untouched; only card/tile construction receives the visible rows.</summary>
    private BooruSearchView RenderPostFilters(BooruSearchView view)
    {
        BooruPostFilter filter = view.PostFilter;
        _filterTitle.Text = Texts.T("booru.filter.title");
        _filterHint.Text = Texts.T("booru.filter.hint");
        _filterRatingLabel.Text = Texts.T("booru.filter.rating");
        _filterMediaLabel.Text = Texts.T("booru.filter.media");
        AutomationProperties.SetName(_filterRating, _filterRatingLabel.Text);
        AutomationProperties.SetName(_filterMedia, _filterMediaLabel.Text);
        foreach (ComboBoxItem item in _filterRating.Items.Cast<ComboBoxItem>())
            item.Content = item.Tag is BooruRating rating ? BooruText.Rating(rating) : Texts.T("booru.filter.all");
        foreach (ComboBoxItem item in _filterMedia.Items.Cast<ComboBoxItem>())
            item.Content = Texts.T(item.Tag switch
            {
                BooruPostMediaType.Image => "media.filter.kind.image", BooruPostMediaType.Animation => "booru.filter.animation",
                BooruPostMediaType.Video => "media.filter.kind.video", BooruPostMediaType.Audio => "booru.filter.audio",
                BooruPostMediaType.Unknown => "booru.ui.rating.Unknown", _ => "booru.filter.all"
            });
        _filterRating.SelectedItem = _filterRating.Items.Cast<ComboBoxItem>().First(item => Equals(item.Tag, filter.Rating));
        _filterMedia.SelectedItem = _filterMedia.Items.Cast<ComboBoxItem>().First(item => Equals(item.Tag, filter.MediaType));
        _filterScoreEnabled.Content = Texts.T("booru.filter.score");
        _filterScoreEnabled.IsChecked = filter.MinimumScore is not null;
        _filterScore.Value = filter.MinimumScore ?? Math.Clamp(PostFilterScoreDefault?.Invoke() ?? MeowChan.Config.BooruPostFilterScoreDefault,
            MeowChan.Config.BooruPostFilterScoreMinimum, MeowChan.Config.BooruPostFilterScoreMaximum);
        _filterScore.IsEnabled = filter.MinimumScore is not null;
        AutomationProperties.SetName(_filterScore, Texts.T("booru.filter.score"));
        _filterReset.Content = Texts.T("media.filter.reset");
        _filterReset.IsEnabled = filter.IsActive;
        _filterTags.Header = Texts.T("booru.filter.tags");
        _filterTagsHint.Text = Texts.T("booru.filter.tagsHint");
        string[] tags = view.Rows.SelectMany(row => row.Post.Tags).Select(tag => tag.Name).Concat(filter.Tags.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        string tagsKey = Texts.Language + "\n" + string.Join('\n', tags);
        if (_filterTagsKey != tagsKey)
        {
            _filterTagsKey = tagsKey;
            _filterTagItems.Children.Clear();
            _filterTagChecks.Clear();
            foreach (string tag in tags)
            {
                CheckBox check = new() { Content = tag, IsThreeState = true, Margin = new Thickness(0, 0, 12, 8) };
                AutomationProperties.SetName(check, tag);
                AutomationProperties.SetHelpText(check, _filterTagsHint.Text);
                check.Click += (_, _) => { if (!_applying) _model.Search.SetPostFilterTag(tag, check.IsChecked); };
                _filterTagChecks.Add(tag, check);
                _filterTagItems.Children.Add(check);
            }
        }
        foreach ((string tag, CheckBox check) in _filterTagChecks)
            check.IsChecked = filter.Tags.TryGetValue(tag, out bool include) ? include : null;
        var visible = view.VisibleRows;
        _filterCount.Text = Texts.T("booru.filter.count", visible.Length, view.Rows.Length);
        _filterEmpty.Text = Texts.T("booru.filter.empty");
        _filterEmpty.IsVisible = !view.Rows.IsEmpty && visible.IsEmpty;
        return view with { Rows = visible };
    }
}
