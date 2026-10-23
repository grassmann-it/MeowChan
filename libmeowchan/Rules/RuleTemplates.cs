using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace MeowChan.Rules;

/// <summary>A copyable set of own overrides. Null is absent; Inherit is an explicit value.</summary>
public sealed class RuleTemplate
{
    public Guid Id { get; }
    public string Name { get; }
    public MediaTypeSelection? Types { get; }
    public MediaSizeLimit? SizeLimit { get; }
    public MediaImageFilterRule? ImageFilter { get; }
    public RuleTemplate(Guid id, string name, MediaTypeSelection? types = null, MediaSizeLimit? sizeLimit = null, MediaImageFilterRule? imageFilter = null)
    {
        if (id == Guid.Empty) throw new ArgumentException("A template needs a stable UUID.", nameof(id));
        ArgumentNullException.ThrowIfNull(name); name = name.Trim();
        if (name.Length is < 1 or > 120 || name.Any(char.IsControl)) throw new ArgumentException("Template names must contain 1–120 printable characters.", nameof(name));
        if (types == null && sizeLimit == null && imageFilter == null) throw new ArgumentException("Select at least one field group.");
        if (imageFilter?.Parameters.Any(pair => pair.Key == null || pair.Value == null) == true)
            throw new ArgumentException("Filter parameters must not contain null.", nameof(imageFilter));
        Id = id; Name = name; Types = types; SizeLimit = sizeLimit; ImageFilter = imageFilter;
    }
}
public sealed class RuleTemplateSet
{
    public string Revision { get; }
    public IReadOnlyList<RuleTemplate> Templates { get; }
    public static RuleTemplateSet Empty { get; } = new("default", Array.Empty<RuleTemplate>());
    public RuleTemplateSet(string revision, IEnumerable<RuleTemplate> templates)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(revision); ArgumentNullException.ThrowIfNull(templates);
        RuleTemplate[] copy = templates.ToArray();
        if (copy.Any(t => t == null) || copy.Select(t => t.Id).Distinct().Count() != copy.Length
            || copy.Select(t => t.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != copy.Length)
            throw new ArgumentException("Template UUIDs and names must be unique.", nameof(templates));
        Revision = revision; Templates = new ReadOnlyCollection<RuleTemplate>(copy);
    }
}
public sealed record RuleTemplateLoadResult(RuleTemplateSet Catalog, string? Warning, bool Recovered);
