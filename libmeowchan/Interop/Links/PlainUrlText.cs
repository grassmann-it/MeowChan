using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;

namespace MeowChan.Interop.Links;

internal enum LinkIssueCode
{
    ByteLimit, InvalidUtf8, LineLimit, LineLength, InvalidUrl, Credentials,
    SafetyUnknown, Duplicate, ClassificationUnavailable
}
internal enum PassiveLinkKind { Unknown, Source, Post, Media }
internal readonly record struct LinkIssue(int Line, LinkIssueCode Code);
// No automatic acquisition capability is represented by this data contract.
internal sealed record PassiveLink(int Line, string Text, PassiveLinkKind Kind)
{
    internal bool RequiresExplicitConfirmation => true;
}
internal sealed record LinkTextResult(IReadOnlyList<PassiveLink> Links, IReadOnlyList<LinkIssue> Issues);
internal sealed record LinkTextExport(byte[] Utf8, IReadOnlyList<LinkIssue> Issues);

/// <summary>
/// Bounded, inert URL text. The safety predicate is a trusted, source-specific public-URL
/// attestation, NOT an imported policy or a secret-name denylist. Without one nothing is
/// retained. In particular, arbitrary query field names, paths and fragments are not
/// presumed public. It must approve the complete original URL without stripping fields.
/// Classifiers are pure parser seams only; classification never grants admission.
/// </summary>
internal sealed partial class PlainUrlText
{
    internal const int MaxBytes = 1_048_576;
    internal const int MaxLines = 4096;
    internal const int MaxLineCharacters = 8192;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly Func<string, bool> publicUrl;
    private readonly Func<string, PassiveLinkKind>? classify;

    internal PlainUrlText(Func<string, bool>? attestPublicUrl = null,
        Func<string, PassiveLinkKind>? classifier = null)
    {
        publicUrl = attestPublicUrl ?? (_ => false);
        classify = classifier;
    }

    internal LinkTextResult Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaxBytes) return Failure(0, LinkIssueCode.ByteLimit);
        string text;
        try { text = Utf8.GetString(bytes); }
        catch (DecoderFallbackException) { return Failure(0, LinkIssueCode.InvalidUtf8); }
        // A single initial UTF-8 BOM is framing, not part of the first URL.
        if (text.StartsWith('\uFEFF')) text = text[1..];
        List<PassiveLink> links = new();
        List<LinkIssue> issues = new();
        HashSet<string> seen = new(StringComparer.Ordinal);
        int start = 0, line = 1;
        for (int i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && text[i] != '\n' && text[i] != '\r') continue;
            if (i == text.Length && start == i) break; // terminal newline is not an empty record
            if (line > MaxLines) return Failure(line, LinkIssueCode.LineLimit);
            string value = text.Substring(start, i - start);
            if (value.Length > MaxLineCharacters) issues.Add(new(line, LinkIssueCode.LineLength));
            else Inspect(value, line, seen, links, issues);
            if (i < text.Length && text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            start = i + 1;
            line++;
        }
        return new(links.AsReadOnly(), issues.AsReadOnly());
    }

    // Export uses exactly the same validation, safety and duplicate semantics as import.
    // Input is already bounded bytes, not an unbounded enumerable or an implicit file read.
    internal LinkTextExport Export(ReadOnlySpan<byte> selectedUrlText)
    {
        LinkTextResult result = Parse(selectedUrlText);
        string text = result.Links.Count == 0 ? "" : string.Join("\n", result.Links.Select(x => x.Text)) + "\n";
        // CR-only input and a missing final terminator can expand output by one byte.
        if (Utf8.GetByteCount(text) > MaxBytes)
            return new(Array.Empty<byte>(), new[] { new LinkIssue(0, LinkIssueCode.ByteLimit) });
        return new(Utf8.GetBytes(text), result.Issues);
    }

    private void Inspect(string value, int line, HashSet<string> seen,
        List<PassiveLink> links, List<LinkIssue> issues)
    {
        // No trimming, directive interpretation, comma tokenization or URI rewriting.
        if (value.Length == 0 || value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
            || value.Contains('\\') || !ValidEscapes(value)
            || !(value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || string.IsNullOrEmpty(uri.Host))
        { issues.Add(new(line, LinkIssueCode.InvalidUrl)); return; }
        int authorityEnd = value.IndexOfAny(new[] { '/', '?', '#' }, value.IndexOf("://", StringComparison.Ordinal) + 3);
        string authority = authorityEnd < 0 ? value : value[..authorityEnd];
        if (uri.UserInfo.Length != 0 || authority.Contains('@'))
        { issues.Add(new(line, LinkIssueCode.Credentials)); return; }
        bool safe;
        try { safe = publicUrl(value); }
        catch (Exception) { safe = false; } // Never propagate collaborator messages containing secrets.
        if (!safe) { issues.Add(new(line, LinkIssueCode.SafetyUnknown)); return; }
        if (!seen.Add(value)) { issues.Add(new(line, LinkIssueCode.Duplicate)); return; }
        PassiveLinkKind kind = PassiveLinkKind.Unknown;
        if (classify != null)
        {
            try { kind = classify(value); if (!Enum.IsDefined(kind)) kind = PassiveLinkKind.Unknown; }
            catch (Exception) { issues.Add(new(line, LinkIssueCode.ClassificationUnavailable)); }
        }
        links.Add(new(line, value, kind));
    }

    private static bool ValidEscapes(string value)
    {
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] != '%') continue;
            if (i + 2 >= value.Length || !Uri.IsHexDigit(value[i + 1]) || !Uri.IsHexDigit(value[i + 2])) return false;
            int octet = Convert.ToInt32(value.Substring(i + 1, 2), 16);
            if (octet < 32 || octet == 127) return false;
            i += 2;
        }
        return true;
    }

    private static LinkTextResult Failure(int line, LinkIssueCode code) =>
        new(Array.Empty<PassiveLink>(), new ReadOnlyCollection<LinkIssue>(new[] { new LinkIssue(line, code) }));
}
