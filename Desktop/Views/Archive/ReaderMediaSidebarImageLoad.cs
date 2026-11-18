using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

// A result owns one bitmap lease until the current request hands it to the view. A stale result gives that lease back, even if its
// producer ignored cancellation. The decoder/cache and their size limits remain the host's; this owns only presentation lifetime.
internal abstract record ReaderMediaSidebarContent
{
    internal sealed record Picture(Bitmap Bitmap, Action<Bitmap> Release, bool Playable, PixelSize? Source = null) : ReaderMediaSidebarContent;
    internal sealed record Unavailable(string TextKey) : ReaderMediaSidebarContent;
}

/// <summary>
/// One request's presentation, shared by the reader sidebar and Media panel. Its loading timer ends when this medium is answered, not
/// when neighbour preloads finish. Every callback belongs to this request; an old completion or queued timer cannot change the next one.
/// UI thread only, including continuations of <see cref="RunAsync"/>.
/// </summary>
internal sealed class ReaderMediaSidebarImageLoad
{
    private readonly ReaderMediaSidebar _view;
    private readonly string _key;
    private readonly ReaderMediaLoadTicket _ticket;
    private readonly GalleryFitPreferences _fit;
    private readonly Func<bool> _available;
    private readonly CancellationToken _token;
    private DispatcherTimer? _placeholder;
    private bool _finished;

    internal ReaderMediaSidebarImageLoad(ReaderMediaSidebar view, string key, ReaderMediaLoadTicket ticket,
        GalleryFitPreferences fit, Func<bool> available, CancellationToken token)
    {
        _view = view;
        _key = key;
        _ticket = ticket;
        _fit = fit;
        _available = available;
        _token = token;
    }

    private bool Current => _available() && _view.Load.IsCurrent(_ticket.Request);

    // The timer and deterministic delayed-completion tests use the same callback. It never clears a ready same-medium preview.
    internal void ShowLoading()
    {
        if (!_finished && Current && !_token.IsCancellationRequested)
            _view.ShowLoading(_view.Load.LoadingLook(_ticket.Request), Texts.T("reader.sidebar.loading"));
    }

    private void Finish()
    {
        _finished = true;
        _placeholder?.Stop();
        _view.Load.Finished(_ticket.Request);
    }

    private void Unavailable(string textKey)
    {
        if (!Current) return;
        Finish();
        // Resolution, decoding and poster failures all obey the same rule: keep a valid preview of THIS medium, never another post's.
        if (!string.Equals(_view.Load.ShownKey, _key, StringComparison.Ordinal))
            _view.ShowPlaceholder(Texts.T(textKey));
    }

    internal async Task RunAsync(Func<CancellationToken, Task<ReaderMediaSidebarContent>> load,
        Action<ReaderMediaSidebarContent.Picture>? presented = null, Func<Task>? preload = null)
    {
        if (!Current) return;
        if (!_ticket.SameMedium)
        {
            // The header now describes the new medium. An older picture must not masquerade as it, even during the placeholder delay.
            if (_view.Load.HasPicture) ShowLoading();
            _placeholder = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ReaderMediaSidebarRules.PlaceholderDelayMs) };
            _placeholder.Tick += (_, _) => { _placeholder.Stop(); ShowLoading(); };
            _placeholder.Start();
        }
        bool shown = false;
        try
        {
            ReaderMediaSidebarContent content = await load(_token);
            if (content is ReaderMediaSidebarContent.Picture picture)
            {
                if (!Current || _token.IsCancellationRequested)
                {
                    picture.Release(picture.Bitmap);
                    // Cancellation by the producer without a newer request is a finite unavailable result, not permanent Loading.
                    Unavailable("reader.sidebar.unavailable");
                    return;
                }
                Finish();
                _view.ShowPicture(picture.Bitmap, picture.Release, picture.Playable,
                    picture.Source is { } source ? ReaderMediaSidebarRules.MaximumDisplay(source.Width, _fit) : double.PositiveInfinity,
                    picture.Source is { } size ? ReaderMediaSidebarRules.MaximumDisplay(size.Height, _fit) : double.PositiveInfinity,
                    _key, picture.Source);
                presented?.Invoke(picture);
                shown = true;
            }
            else if (content is ReaderMediaSidebarContent.Unavailable missing) Unavailable(missing.TextKey);
        }
        catch (OperationCanceledException) { Unavailable("reader.sidebar.unavailable"); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Serilog.Log.Warning(error, "Showing a media sidebar item failed");
            Unavailable("reader.sidebar.unavailable");
        }
        finally { _placeholder?.Stop(); }

        // Presentation is already complete. Neighbour work may be slow, cancelled or fail; none of it owns the shown image or loader.
        if (shown && Current && !_token.IsCancellationRequested && preload != null)
        {
            try { await preload(); }
            catch (OperationCanceledException) { }
            catch (Exception error) when (error is not OutOfMemoryException)
            { Serilog.Log.Debug(error, "The media sidebar's neighbours could not be preloaded"); }
        }
    }
}
