using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MeowChan.Admission;

namespace MeowChan.Desktop;

/// <summary>UI-thread-owned, opt-in passive suggestion state. Never admits URLs or persists clipboard contents.</summary>
internal sealed class ClipboardUrlMonitor
{
    internal const int MaximumTextLength = 64 * 1024;
    internal const int MaximumUrls = 32;
    private static readonly Regex Urls = new(@"https?://[^\s<>""',;]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Queue<string> _seenOrder = new();
    private bool _enabled, _polling;
    private long _generation;
    private string? _lastTextHash, _pendingHash;
    internal IReadOnlyList<string> Pending { get; private set; } = Array.Empty<string>();

    internal void SetEnabled(bool enabled)
    {
        if (_enabled == enabled) return;
        _enabled = enabled;
        ++_generation;
        Pending = Array.Empty<string>();
        _pendingHash = _lastTextHash = null;
        _seen.Clear(); _seenOrder.Clear();
    }

    internal void Dismiss()
    {
        ++_generation;
        if (_pendingHash is string hash && _seen.Add(hash))
        {
            _seenOrder.Enqueue(hash);
            if (_seenOrder.Count > 64) _seen.Remove(_seenOrder.Dequeue());
        }
        Pending = Array.Empty<string>(); _pendingHash = null;
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
        if (!_enabled || _polling || !canScan()) return;
        _polling = true;
        long generation = _generation;
        bool Current() => _enabled && generation == _generation && canScan();
        try
        {
            string? text = await readClipboard();
            if (!Current()) return;
            if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumTextLength)
            {
                Pending = Array.Empty<string>(); _pendingHash = _lastTextHash = null;
                return;
            }
            string hash = Hash(text + "\n" + string.Join('\n', supportedHosts.Order(StringComparer.OrdinalIgnoreCase)));
            if (hash == _lastTextHash) return;
            // A newer clipboard never leaves an older suggestion actionable during classification.
            Pending = Array.Empty<string>(); _pendingHash = null;
            HashSet<string> hosts = new(supportedHosts, StringComparer.OrdinalIgnoreCase);
            List<string> accepted = new();
            foreach (string url in ExtractCandidates(text, hosts))
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
            if (accepted.Count == 0) return;
            string batchHash = Hash(string.Join('\n', accepted));
            if (_seen.Contains(batchHash)) return;
            Pending = Array.AsReadOnly(accepted.ToArray()); _pendingHash = batchHash;
        }
        finally { _polling = false; }
    }

    internal static IReadOnlyList<string> ExtractCandidates(string text, IReadOnlyCollection<string> supportedHosts)
    {
        if (text.Length > MaximumTextLength) return Array.Empty<string>();
        HashSet<string> hosts = new(supportedHosts, StringComparer.OrdinalIgnoreCase);
        HashSet<string> distinct = new(StringComparer.Ordinal);
        List<string> result = new();
        int visited = 0;
        foreach (Match match in Urls.Matches(text))
        {
            if (++visited > MaximumUrls) break;
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
