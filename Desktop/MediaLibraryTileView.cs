using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;

namespace MeowChan.Desktop;

/// <summary>Bindable row of the media page grid.</summary>
internal sealed record MediaLibraryTileRow(MediaLibraryEntry Entry, int Index)
{
    public string Name => Entry.Name ?? Entry.MediaId ?? string.Empty;
    public string ThreadLabel => Texts.T("media.tile.thread", string.IsNullOrWhiteSpace(Entry.ThreadTitle) ? "#" + Entry.Thread.Thread : Entry.ThreadTitle);
    public string Detail => Entry.Presence == MediaLibraryPresence.Missing ? Texts.T("media.tile.missing")
        : Entry.Unassigned ? Texts.T("media.tile.unassigned")
        : Entry.Length is long length ? (length / 1_000_000d).ToString("0.###", Texts.Culture) + " MB" : string.Empty;
}

/// <summary>
/// One tile: verified thumbnail for saved images, a placeholder otherwise. Thumbnails load only while
/// visible and never fetch anything from the network.
/// </summary>
internal sealed class MediaLibraryTileView : UserControl
{
    private static readonly SemaphoreSlim ThumbnailGate = new(2, 2);
    private readonly Image _image = new() { Stretch = Stretch.Uniform, MaxWidth = 170, MaxHeight = 125 };
    private readonly TextBlock _placeholder = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };
    private readonly TextBlock _name = new() { MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _thread = new() { MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11 };
    private readonly TextBlock _detail = new() { MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11 };
    private Bitmap? _bitmap;
    private CancellationTokenSource? _loading;
    private int _request;
    private bool _visible;

    /// <summary>Set by the page: loads a verified thumbnail for a saved entry, or null when none can be shown.</summary>
    internal Func<MediaLibraryEntry, CancellationToken, Task<Bitmap?>>? ThumbnailLoader { get; set; }

    public MediaLibraryTileView()
    {
        MaxWidth = 190; Height = 210;
        Grid preview = new() { Height = 125, MaxWidth = 170 };
        preview.Children.Add(_image); preview.Children.Add(_placeholder);
        Grid grid = new() { Margin = new Thickness(8), RowDefinitions = new RowDefinitions("125,Auto,Auto,Auto"), RowSpacing = 4 };
        grid.Children.Add(preview);
        grid.Children.Add(_name); Grid.SetRow(_name, 1);
        grid.Children.Add(_thread); Grid.SetRow(_thread, 2);
        grid.Children.Add(_detail); Grid.SetRow(_detail, 3);
        foreach (TextBlock text in new[] { _thread, _detail }) text.Classes.Add("workspace-meta");
        Content = grid;
        DataContextChanged += (_, _) => { Reset(); Present(); if (_visible) _ = LoadAsync(); };
        EffectiveViewportChanged += (_, e) =>
        {
            bool visible = IsEffectivelyVisible && new Rect(Bounds.Size).Intersects(e.EffectiveViewport);
            if (visible == _visible) return;
            _visible = visible;
            if (visible) _ = LoadAsync(); else Reset();
        };
        DetachedFromVisualTree += (_, _) => { _visible = false; Reset(); };
    }

    private void Present()
    {
        MediaLibraryTileRow? row = DataContext as MediaLibraryTileRow;
        _name.Text = row?.Name;
        _thread.Text = row?.ThreadLabel;
        _detail.Text = row?.Detail;
        _placeholder.Text = row?.Entry.Presence == MediaLibraryPresence.Missing ? Texts.T("media.tile.missing") : string.Empty;
        string description = row == null ? string.Empty : string.Join(Environment.NewLine, row.Name, row.ThreadLabel, row.Detail);
        ToolTip.SetTip(this, description);
        AutomationProperties.SetName(this, description);
    }

    private async Task LoadAsync()
    {
        if (DataContext is not MediaLibraryTileRow { Entry.Presence: MediaLibraryPresence.Present } row
            || ThumbnailLoader is not { } loader || _loading != null || _bitmap != null) return;
        int request = _request;
        using CancellationTokenSource loading = new();
        _loading = loading;
        _placeholder.Text = Texts.T("archive.loadingThumbnail");
        try
        {
            await ThumbnailGate.WaitAsync(loading.Token);
            Bitmap? bitmap;
            try { bitmap = await Task.Run(() => loader(row.Entry, loading.Token), loading.Token); }
            finally { ThumbnailGate.Release(); }
            if (request != _request || !_visible || !ReferenceEquals(DataContext, row)) { bitmap?.Dispose(); return; }
            _bitmap = bitmap;
            _image.Source = bitmap;
            _placeholder.Text = bitmap == null ? Texts.T("archive.previewUnavailable") : string.Empty;
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (request == _request) _placeholder.Text = Texts.T("archive.previewUnavailable"); }
        finally { if (ReferenceEquals(_loading, loading)) _loading = null; }
    }

    private void Reset()
    {
        ++_request;
        _loading?.Cancel(); _loading = null;
        _image.Source = null; _bitmap?.Dispose(); _bitmap = null;
        MediaLibraryTileRow? row = DataContext as MediaLibraryTileRow;
        _placeholder.Text = row?.Entry.Presence == MediaLibraryPresence.Missing ? Texts.T("media.tile.missing") : string.Empty;
    }
}
