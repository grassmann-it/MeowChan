using System;
using System.Collections.Generic;
using System.Linq;

namespace MeowChan.Desktop.Commands;

/// <summary>
/// Which main pages stand directly in the footer and in which order (Settings › Appearance › Footer; F1 "footer setting"). An immutable value
/// over <see cref="MainPageOrder"/>: <see cref="Order"/> holds every main page once, <see cref="IsVisible"/> says whether its button is
/// shown directly. A hidden page is not gone: it stays in the footer's More menu, in the ⋯ menu and under its keyboard shortcut.
/// The layout belongs to a profile (<c>Config.FooterPages</c>); a work view can still move more pages under More, but this
/// setting never shows a button a work view hides (<see cref="MeowChan.Desktop.WorkViews.WorkViewPolicy.FooterDirect"/>).
/// </summary>
/// <remarks>
/// Stored form (<see cref="ToStored"/>): the page ids without the "navigation." prefix in footer order, a leading "-" marking a hidden
/// page, e.g. <c>downloads, archive, media, tagging, -booru, rules, sources, status</c>. An empty list is the default layout (today's
/// footer, Booru off). Reading is forgiving: unknown ids and repeats are ignored, a page the list does not know (one added by a later
/// version) takes its default place right behind the page that precedes it in the default order, and Downloads is always visible.
/// </remarks>
public sealed class FooterLayout : IEquatable<FooterLayout>
{
    private const string Prefix = "navigation.";
    private const string HiddenMark = "-";

    private readonly string[] _order;
    private readonly HashSet<string> _hidden;

    private FooterLayout(string[] order, HashSet<string> hidden)
    {
        _order = order;
        _hidden = hidden;
    }

    /// <summary>The footer as it shipped: every page in <see cref="MainPageOrder.CommandIds"/> order, Booru hidden.</summary>
    public static FooterLayout Default { get; } = Normalize(Array.Empty<(string, bool)>());

    /// <summary>Every main page once, in footer order (the hidden ones keep their place so that showing one again puts it back).</summary>
    public IReadOnlyList<string> Order => _order;
    /// <summary>The pages with a footer button of their own, from left to right.</summary>
    public IReadOnlyList<string> VisibleOrder => _order.Where(id => !_hidden.Contains(id)).ToArray();
    /// <summary>The pages that only More, the ⋯ menu and the shortcut reach, in footer order.</summary>
    public IReadOnlyList<string> HiddenOrder => _order.Where(_hidden.Contains).ToArray();
    /// <summary>The digit order of the main pages: the visible footer buttons from left to right take Ctrl/Cmd+1, 2, 3 ..., then the hidden pages.</summary>
    public IReadOnlyList<string> Numbering => VisibleOrder.Concat(HiddenOrder).ToArray();
    /// <summary>
    /// The digit order in a work view (Tom 15.11.): Ctrl/Cmd+1, 2, 3 ... count the footer buttons that are effectively shown - the person's choice AND the work
    /// view's allowance (<see cref="DirectOrder"/>) - from the left; then the pages the work view moved under More (the setting shows them), then the pages the
    /// setting hides, each group in footer order. The width never enters, so a page that only a narrow window pushed under More keeps its digit.
    /// <paramref name="isAvailable"/> removes a page that cannot be shown at all (Booru without its master setting) from the shown group.
    /// </summary>
    public IReadOnlyList<string> NumberingFor(MeowChan.Desktop.WorkViews.WorkView view, Func<string, bool>? isAvailable = null)
    {
        string[] shown = _order.Where(id => ShowsDirectly(id, view) && (isAvailable?.Invoke(id) ?? true)).ToArray();
        string[] reduced = _order.Where(id => !shown.Contains(id, StringComparer.Ordinal) && !_hidden.Contains(id)).ToArray();
        return shown.Concat(reduced).Concat(_order.Where(_hidden.Contains)).ToArray();
    }
    public bool IsVisible(string commandId) => Array.IndexOf(_order, commandId) >= 0 && !_hidden.Contains(commandId);
    /// <summary>
    /// Whether the page may have a button of its own in this work view: the person's choice AND the work view's allowance. The work view is the upper
    /// bound, so the setting hides a button but never shows one a work view moves under More (Barebones). Whether the page is available
    /// at all (Booru) and whether the width suffices is decided by the shell.
    /// </summary>
    public bool ShowsDirectly(string commandId, MeowChan.Desktop.WorkViews.WorkView view) =>
        IsVisible(commandId) && MeowChan.Desktop.WorkViews.WorkViewPolicy.FooterDirect(commandId, view);
    /// <summary>The pages with a footer button of their own in this work view, from left to right.</summary>
    public IReadOnlyList<string> DirectOrder(MeowChan.Desktop.WorkViews.WorkView view) => _order.Where(id => ShowsDirectly(id, view)).ToArray();
    public bool IsDefault => Equals(Default);
    public int IndexOf(string commandId) => Array.IndexOf(_order, commandId);

    /// <summary>The page ids of the stored form: "downloads" for "navigation.downloads".</summary>
    public static string StoredId(string commandId) => commandId.StartsWith(Prefix, StringComparison.Ordinal) ? commandId[Prefix.Length..] : commandId;
    /// <summary>The command id of a stored page id ("media" gives "navigation.media").</summary>
    public static string CommandId(string storedId) => storedId.StartsWith(Prefix, StringComparison.Ordinal) ? storedId : Prefix + storedId;

    /// <summary>Reads the stored list (see the remarks); null, an empty list or a list without a single known page give <see cref="Default"/>.</summary>
    public static FooterLayout FromStored(IEnumerable<string?>? stored)
    {
        List<(string Id, bool Hidden)> entries = new();
        foreach (string? raw in stored ?? Array.Empty<string?>())
        {
            string text = raw?.Trim() ?? string.Empty;
            bool hidden = text.StartsWith(HiddenMark, StringComparison.Ordinal);
            if (hidden) text = text[HiddenMark.Length..].Trim();
            if (text.Length == 0 || text.Length > 64) continue;
            entries.Add((CommandId(text), hidden));
        }
        return Normalize(entries);
    }

    /// <summary>The stored form of this layout; empty for the default layout (the file then carries no footer setting at all).</summary>
    public string[] ToStored() => IsDefault ? Array.Empty<string>()
        : _order.Select(id => (_hidden.Contains(id) ? HiddenMark : string.Empty) + StoredId(id)).ToArray();

    /// <summary>A copy with the page shown or hidden. Downloads cannot be hidden; an unknown id changes nothing.</summary>
    public FooterLayout WithVisible(string commandId, bool visible)
    {
        if (Array.IndexOf(_order, commandId) < 0 || !visible && !MainPageOrder.CanHideFromFooter(commandId)) return this;
        HashSet<string> hidden = new(_hidden, StringComparer.Ordinal);
        if (visible) hidden.Remove(commandId); else hidden.Add(commandId);
        return new FooterLayout((string[])_order.Clone(), hidden);
    }

    /// <summary>A copy with the page moved one place toward the start (-1) or the end (+1) of the footer; at the edge it changes nothing.</summary>
    public FooterLayout Move(string commandId, int delta)
    {
        int from = Array.IndexOf(_order, commandId), to = from + Math.Sign(delta);
        if (from < 0 || delta == 0 || to < 0 || to >= _order.Length) return this;
        string[] order = (string[])_order.Clone();
        (order[from], order[to]) = (order[to], order[from]);
        return new FooterLayout(order, new HashSet<string>(_hidden, StringComparer.Ordinal));
    }

    /// <summary>
    /// The small API for a hint that offers a page ("You use Booru - show it in the footer?"): shows or hides the page in a stored list and returns
    /// the new stored list. The shell saves it (<c>MainWindow.SetFooterPageVisibleAsync</c>). <paramref name="pageId"/> is the stored id
    /// ("booru") or the command id ("navigation.booru"); an unknown id returns the list unchanged.
    /// </summary>
    public static string[] SetPageVisible(IEnumerable<string?>? stored, string pageId, bool visible)
    {
        FooterLayout layout = FromStored(stored);
        string id = CommandId(pageId.Trim());
        return (MainPageOrder.Contains(id) ? layout.WithVisible(id, visible) : layout).ToStored();
    }

    private static FooterLayout Normalize(IReadOnlyList<(string Id, bool Hidden)> entries)
    {
        List<string> order = new();
        HashSet<string> hidden = new(StringComparer.Ordinal);
        foreach ((string id, bool isHidden) in entries)
        {
            if (!MainPageOrder.Contains(id) || order.Contains(id, StringComparer.Ordinal)) continue;
            order.Add(id);
            if (isHidden) hidden.Add(id);
        }
        IReadOnlyList<string> defaults = MainPageOrder.CommandIds;
        for (int index = 0; index < defaults.Count; index++)
        {
            string id = defaults[index];
            if (order.Contains(id, StringComparer.Ordinal)) continue;
            // A page the list does not know takes its default place: behind the nearest page before it (in the default order) that is
            // already placed, else at the front.
            int at = 0;
            for (int before = index - 1; before >= 0; before--)
            {
                int found = order.IndexOf(defaults[before]);
                if (found >= 0) { at = found + 1; break; }
            }
            order.Insert(at, id);
            if (MainPageOrder.DefaultHiddenPages.Contains(id, StringComparer.Ordinal)) hidden.Add(id);
        }
        foreach (string id in order.Where(id => !MainPageOrder.CanHideFromFooter(id)).ToArray()) hidden.Remove(id);
        return new FooterLayout(order.ToArray(), hidden);
    }

    public bool Equals(FooterLayout? other) => other != null && _order.SequenceEqual(other._order, StringComparer.Ordinal)
        && _hidden.SetEquals(other._hidden);
    public override bool Equals(object? obj) => Equals(obj as FooterLayout);
    public override int GetHashCode() => HashCode.Combine(string.Join('|', _order), _hidden.Count);
}
