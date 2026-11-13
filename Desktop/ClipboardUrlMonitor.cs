using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MeowChan.Admission;

namespace MeowChan.Desktop;

/// <summary>What a poll had to leave out; the window shows it as a localized sentence that names the setting.</summary>
internal enum ClipboardNotice
{
    None,
    /// <summary>The text held more URLs than <see cref="ClipboardUrlMonitor.UrlLimit"/>; only the first ones were checked.</summary>
    UrlsCut,
    /// <summary>The text was larger than <see cref="ClipboardUrlMonitor.TextLimit"/> and was not searched at all.</summary>
    TextTooLong
}

/// <summary>UI-thread-owned, opt-in passive suggestion state. Never admits URLs or persists clipboard contents.</summary>
internal sealed class ClipboardUrlMonitor
{
    // Defaults of the two clipboard settings (Config.ClipboardTextLimitKiB, Config.ClipboardUrlLimit); a poll uses the
    // instance limits below, which Configure sets from the settings. Reads never persist or log the text.
    internal const int MaximumTextLength = 64 * 1024;
    internal const int MaximumUrls = 32;
    internal const int MinimumUrlLimit = 1;
    internal const int MaximumUrlLimit = 500;
    internal const int MinimumTextLimitKiB = 4;
    internal const int MaximumTextLimitKiB = 4096;
    private static readonly Regex Urls = new(@"https?://[^\s<>""',;]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Queue<string> _seenOrder = new();
    private bool _enabled, _polling, _startupBaselinePending;
    private long _generation;
    private string? _lastTextHash, _pendingHash, _supportedHostsHash, _startupBaselineTextHash;
    internal IReadOnlyList<string> Pending { get; private set; } = Array.Empty<string>();
    /// <summary>Most URLs of one text that are checked (the "Clipboard: URLs checked per read" setting).</summary>
    internal int UrlLimit { get; private set; } = MaximumUrls;
    /// <summary>Largest text, in UTF-16 units, that is searched at all (the "Clipboard: text searched (KiB)" setting).</summary>
    internal int TextLimit { get; private set; } = MaximumTextLength;
    /// <summary>What the last poll had to leave out, until the clipboard changes or the user dismisses the suggestion.</summary>
    internal ClipboardNotice Notice { get; private set; }
    private bool _oversized;

    /// <summary>Applies the two clipboard settings; the next poll uses them. Out-of-range values are a programming error.</summary>
    internal void Configure(int urlLimit, int textLimitBytes)
    {
        if (urlLimit is < MinimumUrlLimit or > MaximumUrlLimit) throw new ArgumentOutOfRangeException(nameof(urlLimit));
        if (textLimitBytes < MinimumTextLimitKiB * 1024 || textLimitBytes > MaximumTextLimitKiB * 1024) throw new ArgumentOutOfRangeException(nameof(textLimitBytes));
        UrlLimit = urlLimit;
        TextLimit = textLimitBytes;
    }

    /// <summary>The localized sentence for <see cref="Notice"/>, naming the setting and its page; null when nothing was left out.</summary>
    internal string? NoticeText() => Notice switch
    {
        ClipboardNotice.UrlsCut => Localization.Texts.T("clipboard.monitor.cutUrls", UrlLimit, Localization.Texts.T("budgets.clipboardUrls")),
        ClipboardNotice.TextTooLong => Localization.Texts.T("clipboard.monitor.cutText", TextLimit / 1024, Localization.Texts.T("budgets.clipboardText")),
        _ => null
    };

    internal void SetEnabled(bool enabled, bool baselineOnly = false)
    {
        if (_enabled == enabled) return;
        _enabled = enabled;
        // Only the controller's initial saved ON requests a baseline. Re-opt-in offers current text.
        _startupBaselinePending = enabled && baselineOnly;
        _supportedHostsHash = null;
        ++_generation;
        Pending = Array.Empty<string>();
        Notice = ClipboardNotice.None; _oversized = false;
        _pendingHash = _lastTextHash = _startupBaselineTextHash = null;
        _seen.Clear(); _seenOrder.Clear();
    }

    internal void SetSupportedHosts(IReadOnlyCollection<string> supportedHosts)
    {
        if (!_enabled) return;
        string hash = Hash(string.Join('\n', supportedHosts.Select(host => host.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)));
        if (hash == _supportedHostsHash) return;
        _supportedHostsHash = hash;
        ++_generation;
        Pending = Array.Empty<string>();
        Notice = ClipboardNotice.None;
        _pendingHash = _lastTextHash = null;
        // Invalidate host-bound work, not the completed startup text digest. Delayed
        // provider discovery must not turn unchanged startup contents into an offer.
    }

    internal void Dismiss()
    {
        ++_generation;
        if (_pendingHash is string hash && _seen.Add(hash))
        {
            _seenOrder.Enqueue(hash);
            if (_seenOrder.Count > 64) _seen.Remove(_seenOrder.Dequeue());
        }
        // A dismissed notice stays away until the clipboard changes (an oversized text stays "seen" while it stays oversized).
        Pending = Array.Empty<string>(); _pendingHash = null; Notice = ClipboardNotice.None;
    }

    internal IReadOnlyList<string> TakePending()
    {
        var frozen = Pending;
        Dismiss();
        return frozen;
    }

    internal async Task PollAsync(Func<bool> canScan, Func<Task<string?>> readClipboard,
        IReadOnlyCollection<string> supportedHosts, Func<string, Task<BoardAdmissionTarget?>> classify)
    {
        if (!_enabled) return;
        // Snapshot the declared hosts before awaiting; host refreshes can invalidate even a busy poll.
        HashSet<string> hosts = new(supportedHosts, StringComparer.OrdinalIgnoreCase);
        SetSupportedHosts(hosts);
        if (_polling) return;
        long generation = _generation;
        if (!canScan() || !_enabled || generation != _generation) return;
        _polling = true;
        bool baselineOnly = _startupBaselinePending;
        // The controller also refreshes hosts in canScan. Check the generation afterwards.
        bool Current() => _enabled && canScan() && generation == _generation;
        try
        {
            string? text = await readClipboard();
            if (!Current()) return;
            // Check length before hashing, whitespace scanning or candidate extraction.
            if (text != null && text.Length > TextLimit)
            {
                Pending = Array.Empty<string>(); _pendingHash = _lastTextHash = null;
                if (baselineOnly) ++_generation; // Retry a baseline, never classify after an oversized read.
                // Say once per oversized text that nothing was searched; a startup baseline is ignored silently as always.
                else if (!_oversized) { _oversized = true; Notice = ClipboardNotice.TextTooLong; }
                return;
            }
            _oversized = false;
            if (Notice == ClipboardNotice.TextTooLong) Notice = ClipboardNotice.None;
            if (baselineOnly)
            {
                _startupBaselineTextHash = Hash(text ?? string.Empty);
                _lastTextHash = Hash((text ?? string.Empty) + "\n" + _supportedHostsHash);
                _startupBaselinePending = false;
                // No extraction, classification, seen-batch entry or actionable pending URLs.
                return;
            }
            if (_startupBaselineTextHash is string startupHash)
            {
                if (Hash(text ?? string.Empty) == startupHash)
                {
                    // Separate raw-text identity from the normal host-bound cache.
                    // No extraction/classification even after a host-set expansion.
                    _lastTextHash = Hash((text ?? string.Empty) + "\n" + _supportedHostsHash);
                    return;
                }
                // A different bounded read ends startup suppression. Recopied
                // original text later is ordinary input, not permanently blocked.
                _startupBaselineTextHash = null;
            }
            if (string.IsNullOrWhiteSpace(text))
            {
                Pending = Array.Empty<string>(); _pendingHash = _lastTextHash = null; Notice = ClipboardNotice.None;
                return;
            }
            string hash = Hash(text + "\n" + _supportedHostsHash);
            if (hash == _lastTextHash) return;
            // A newer clipboard never leaves an older suggestion actionable during classification.
            Pending = Array.Empty<string>(); _pendingHash = null; Notice = ClipboardNotice.None;
            List<string> accepted = new();
            IReadOnlyList<string> candidates = ExtractCandidates(text, hosts, UrlLimit, TextLimit, out bool truncated);
            foreach (string url in candidates)
            {
                if (!Current()) return;
                BoardAdmissionTarget? target = await classify(url);
                if (!Current()) return;
                // A provider may canonicalize to a different URL. Its result must still be a
                // single supported URL, never arbitrary text to display or submit on consent.
                if (target is { AlreadyWatching: false }
                    && TryNormalizeSupportedUrl(target.Url, hosts, out string normalized)
                    && !accepted.Contains(normalized, StringComparer.Ordinal))
                    accepted.Add(normalized);
            }
            _lastTextHash = hash;
            // Skipped URLs are reported even when none of the checked ones was supported: the user may expect the later ones.
            if (accepted.Count == 0) { if (truncated) Notice = ClipboardNotice.UrlsCut; return; }
            string batchHash = Hash(string.Join('\n', accepted));
            if (_seen.Contains(batchHash)) return;
            Pending = Array.AsReadOnly(accepted.ToArray()); _pendingHash = batchHash;
            if (truncated) Notice = ClipboardNotice.UrlsCut;
        }
        catch
        {
            if (baselineOnly && generation == _generation)
            {
                ++_generation;
                Pending = Array.Empty<string>(); _pendingHash = _lastTextHash = null;
            }
            // A failed baseline remains pending. The controller handles access errors without logging contents.
            throw;
        }
        finally { _polling = false; }
    }

    internal static IReadOnlyList<string> ExtractCandidates(string text, IReadOnlyCollection<string> supportedHosts) =>
        ExtractCandidates(text, supportedHosts, MaximumUrls, MaximumTextLength, out _);

    /// <summary>
    /// The supported URLs among the first <paramref name="maximumUrls"/> URL-shaped matches of a text no longer than
    /// <paramref name="maximumTextLength"/>. <paramref name="truncated"/> is true when a further match existed.
    /// </summary>
    internal static IReadOnlyList<string> ExtractCandidates(string text, IReadOnlyCollection<string> supportedHosts,
        int maximumUrls, int maximumTextLength, out bool truncated)
    {
        truncated = false;
        if (text.Length > maximumTextLength) return Array.Empty<string>();
        HashSet<string> hosts = new(supportedHosts, StringComparer.OrdinalIgnoreCase);
        HashSet<string> distinct = new(StringComparer.Ordinal);
        List<string> result = new();
        int visited = 0;
        foreach (Match match in Urls.Matches(text))
        {
            if (++visited > maximumUrls) { truncated = true; break; }
            string value = match.Value.TrimEnd('.', ',', ';', ')', ']', '}');
            if (!TryNormalizeSupportedUrl(value, hosts, out string normalized)) continue;
            if (distinct.Add(normalized)) result.Add(normalized);
        }
        return Array.AsReadOnly(result.ToArray());
    }

    private static bool TryNormalizeSupportedUrl(string? value, HashSet<string> hosts, out string normalized)
    {
        normalized = string.Empty;
        // Uri can normalize away control characters. Reject them before parsing so a
        // classified target cannot smuggle a second input line into the frozen batch.
        if (string.IsNullOrEmpty(value) || value.Length > 4096
            || value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character))
            || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo) || !hosts.Contains(uri.IdnHost)) return false;
        normalized = uri.AbsoluteUri;
        return normalized.Length <= 4096;
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
