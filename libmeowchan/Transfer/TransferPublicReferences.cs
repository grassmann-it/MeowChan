using System;
using System.Collections.Generic;
using System.Net;
using System.Text.RegularExpressions;

namespace MeowChan.Transfer;

/// <summary>Original-text public grammar; not live routing, fetching, or an arbitrary URL attestation.</summary>
internal static class TransferPublicReferences
{
    private static readonly Regex Page = Pattern(@"https://(?:boards\.4chan\.org/[a-z0-9_]{1,256}/(?:thread/[0-9]{1,256}(?:#p[0-9]{1,256})?)?|8kun\.top/[a-z0-9_]{1,256}/(?:res/[0-9]{1,256}\.html(?:#[0-9]{1,256})?)?)");
    // Matches the original public URL shape emitted by plugins/Official/FourChan/FourChan.cs.
    private static readonly Regex Media = Pattern(@"https://i\.4cdn\.org/[a-z0-9_]{1,256}/[0-9]{1,256}s?\.(?:jpg|jpeg|png|gif|webm|mp4)");
    // Existing EightKun producer constructs this exact public path from its bounded id/extension.
    // This classifies the inert reference only; no DNS/request/permission or availability claim.
    private static readonly Regex EightKunMedia = Pattern(@"https://nerv\.8kun\.top/file_store/[A-Za-z0-9_-]{1,256}\.[A-Za-z0-9]{1,32}");
    private static readonly Regex Anchor = Pattern(@"#(?:p)?[0-9]{1,256}");
    private static readonly Regex Tag = Pattern("(?<closing>/)?(?<name>[a-zA-Z][a-zA-Z0-9]*)(?<attributes>(?:\\s+[a-zA-Z][a-zA-Z0-9-]*=(?:\"[^\"]*\"|'[^']*'))*)\\s*(?<self>/)?");
    private static readonly Regex Attribute = new("\\s+(?<name>[a-zA-Z][a-zA-Z0-9-]*)=(?:\"(?<double>[^\"]*)\"|'(?<single>[^']*)')", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly HashSet<string> Formatting = new(StringComparer.Ordinal)
        { "b", "strong", "i", "em", "u", "s", "del", "span", "p", "div", "blockquote", "br", "wbr", "a", "small", "pre", "code" };
    private static readonly HashSet<string> Classes = new(StringComparer.Ordinal)
        { "quote", "quotelink", "deadlink", "spoiler", "greentext" };
    private static Regex Pattern(string value) => new("\\A(?:" + value + ")\\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    internal static bool IsPublicUrl(string value) => Page.IsMatch(value) || Media.IsMatch(value) || EightKunMedia.IsMatch(value);
    internal static bool IsThreadPage(string value, ResourceId id) => id.Source switch
    {
        "4chan" => value == $"https://boards.4chan.org/{id.Board}/thread/{id.Thread}" && Page.IsMatch(value),
        "8kun" => value == $"https://8kun.top/{id.Board}/res/{id.Thread}.html" && Page.IsMatch(value),
        _ => false
    };

    internal static bool IsInertHtml(string value)
    {
        int index = 0;
        while (index < value.Length)
        {
            if (value[index++] != '<') continue;
            int start = index;
            char quote = '\0';
            while (index < value.Length)
            {
                char current = value[index];
                if (quote != '\0') { if (current == quote) quote = '\0'; }
                else if (current is '\'' or '"') quote = current;
                else if (current == '>') break;
                index++;
            }
            if (index == value.Length) return false;
            Match tag = Tag.Match(value[start..index++]);
            if (!tag.Success) return false;
            string name = tag.Groups["name"].Value.ToLowerInvariant();
            if (!Formatting.Contains(name)) return false;
            string attributes = tag.Groups["attributes"].Value;
            if (tag.Groups["closing"].Success)
            {
                if (attributes.Length != 0 || tag.Groups["self"].Success) return false;
                continue;
            }
            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (Match attribute in Attribute.Matches(attributes))
            {
                string key = attribute.Groups["name"].Value.ToLowerInvariant();
                if (!seen.Add(key)) return false;
                string raw = attribute.Groups["double"].Success ? attribute.Groups["double"].Value : attribute.Groups["single"].Value;
                string decoded = WebUtility.HtmlDecode(raw);
                if (key == "href" && name == "a")
                {
                    if (!Anchor.IsMatch(decoded) && !IsPublicUrl(decoded)) return false;
                }
                else if (key == "class")
                {
                    string[] classes = decoded.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (classes.Length == 0 || Array.Exists(classes, item => !Classes.Contains(item))) return false;
                }
                else return false;
            }
        }
        return true;
    }
}
