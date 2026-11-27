using System;
using System.Collections.Generic;

namespace MeowChan.Desktop.Booru;

/// <summary>Copies the tag terms of an executed query without carrying provider-specific metatags across.</summary>
internal sealed record BooruQueryPortability(string Query, string RemovedTerms)
{
    internal static BooruQueryPortability ForOtherSource(string executedQuery)
    {
        List<string> tags = new();
        List<string> removed = new();
        foreach (string term in executedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (term.Contains(':')) removed.Add(term);
            else tags.Add(term);
        }
        return new(string.Join(' ', tags), string.Join(' ', removed));
    }
}
