using System;
using Avalonia;

namespace MeowChan.Desktop;

/// <summary>What the media sidebar shows once a load was not ready after <see cref="ReaderMediaSidebarRules.PlaceholderDelayMs"/>.</summary>
internal enum ReaderMediaLoadingLook
{
    /// <summary>Nothing changes: the load is stale or already answered, or the picture shown is this medium's (a decode at another size swaps in place).</summary>
    Keep,
    /// <summary>The previous medium's picture stays, dimmed, under the loading text: the frame does not flash empty. Only while loading; a final text (a post without media, an error) empties the frame.</summary>
    DimPrevious,
    /// <summary>Nothing is shown: the placeholder carries the loading text.</summary>
    Placeholder,
}

/// <summary>One load of the sidebar: its token, whether it is for the medium already shown, and whether that picture already covers the frame.</summary>
internal readonly record struct ReaderMediaLoadTicket(int Request, bool SameMedium, bool UpToDate);

/// <summary>
/// The load state of the media sidebar (the reader's and the Media page's panel), without a window: which load is the latest (older ones are ignored
/// when they finish), which medium the picture on screen belongs to and how large it was decoded. It keeps the picture from flashing (Tom, 16.11.2026):
/// <list type="bullet">
/// <item>the same medium shown again whose picture covers the frame needs no load at all (<see cref="ReaderMediaLoadTicket.UpToDate"/>);</item>
/// <item>a decode of the shown medium at another size never puts the loading placeholder in between: the new picture swaps in place;</item>
/// <item>a medium that is not ready after the delay dims the previous picture under the loading text, the empty placeholder only stands in when no
/// picture is shown;</item>
/// <item>once the latest load showed its picture (or its final text), the delay has nothing more to do: the neighbours it then preloads never turn the
/// shown picture back into "Loading…".</item>
/// </list>
/// The view reports what it shows (<see cref="Showed"/>, <see cref="Dimmed"/>, <see cref="Cleared"/>); the host asks with <see cref="Begin"/>,
/// <see cref="IsCurrent"/> and <see cref="LoadingLook"/>. UI thread only.
/// </summary>
internal sealed class ReaderMediaSidebarLoadState
{
    private int _request;
    private string? _wanted;
    private bool _awaiting;

    /// <summary>The medium whose picture is shown at full opacity; null while nothing or only a dimmed trace of an earlier medium is shown.</summary>
    internal string? ShownKey { get; private set; }
    /// <summary>A picture is on screen (the shown medium's, or the dimmed trace of the previous one).</summary>
    internal bool HasPicture { get; private set; }
    /// <summary>The decoded size of the picture on screen.</summary>
    internal PixelSize ShownPixels { get; private set; }
    /// <summary>The source size of the picture on screen when it is known (an image; a video's poster has none).</summary>
    internal PixelSize? ShownSource { get; private set; }
    /// <summary>The latest load has neither shown its picture nor a final text yet.</summary>
    internal bool Awaiting => _awaiting;
    /// <summary>The token of the latest load.</summary>
    internal int Request => _request;

    /// <summary>
    /// A new load of <paramref name="key"/> for a frame of <paramref name="needed"/> physical pixels; every earlier load is stale from now on. A load of the
    /// medium already shown is <see cref="ReaderMediaLoadTicket.SameMedium"/>; it is <see cref="ReaderMediaLoadTicket.UpToDate"/> (nothing to decode) while
    /// the shown picture is still sharp enough for the frame (<see cref="ReaderMediaSidebarRules.NeedsLargerDecode"/>: a frame a few pixels larger, as layout
    /// jitter makes it, asks nothing).
    /// </summary>
    internal ReaderMediaLoadTicket Begin(string? key, (double Width, double Height) needed)
    {
        _request = unchecked(_request + 1);
        bool same = key != null && HasPicture && string.Equals(key, ShownKey, StringComparison.Ordinal);
        bool upToDate = same && !ReaderMediaSidebarRules.NeedsLargerDecode(ShownPixels, needed, ShownSource);
        _wanted = key;
        _awaiting = key != null && !upToDate;
        return new ReaderMediaLoadTicket(_request, same, upToDate);
    }

    /// <summary>Every running load is stale (another medium, a hidden sidebar); what is shown stays as it is.</summary>
    internal void Cancel()
    {
        _request = unchecked(_request + 1);
        _wanted = null;
        _awaiting = false;
    }

    /// <summary>Whether this load is still the latest (a stale one gives its result back instead of showing it).</summary>
    internal bool IsCurrent(int request) => request == _request;

    /// <summary>The look once <see cref="ReaderMediaSidebarRules.PlaceholderDelayMs"/> passed for this load (see <see cref="ReaderMediaLoadingLook"/>).</summary>
    internal ReaderMediaLoadingLook LoadingLook(int request)
    {
        if (!IsCurrent(request) || !_awaiting) return ReaderMediaLoadingLook.Keep;
        if (HasPicture && ShownKey != null && string.Equals(ShownKey, _wanted, StringComparison.Ordinal)) return ReaderMediaLoadingLook.Keep;
        return HasPicture ? ReaderMediaLoadingLook.DimPrevious : ReaderMediaLoadingLook.Placeholder;
    }

    /// <summary>The current request has a final answer, including a failed size upgrade that keeps its valid preview.</summary>
    internal void Finished(int request)
    {
        if (IsCurrent(request)) _awaiting = false;
    }

    /// <summary>The view shows a picture of <paramref name="key"/> (null: a medium without identity, never reused). It answers the latest load.</summary>
    internal void Showed(string? key, PixelSize pixels, PixelSize? source)
    {
        HasPicture = true;
        ShownKey = key;
        ShownPixels = pixels;
        ShownSource = source;
        _awaiting = false;
    }

    /// <summary>The view dimmed its picture under the loading text of another medium: the picture is only a trace now.</summary>
    internal void Dimmed()
    {
        ShownKey = null;
    }

    /// <summary>The view shows no picture (the placeholder with a text, a post without media, the empty state, a picture given back).</summary>
    internal void Cleared(bool answered = true)
    {
        HasPicture = false;
        ShownKey = null;
        ShownPixels = default;
        ShownSource = null;
        if (answered) _awaiting = false;
    }
}
