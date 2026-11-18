using System.Collections.Generic;
using System.Linq;

namespace MeowChan.Desktop.Booru;

/// <summary>
/// Which Danbooru search addresses of one Add input are used. The Danbooru window has one search field, so an input with
/// several search or site addresses opens it with the last one; the earlier ones are reported once, never one by one.
/// Post addresses are not searches and are all loaded.
/// </summary>
internal sealed record BooruAddressBatch(int? SearchIndex, IReadOnlyList<int> Ignored)
{
    /// <summary>Plans an input from the entry indexes of its search and site addresses (post addresses are not listed).</summary>
    internal static BooruAddressBatch Plan(IEnumerable<int> searchIndexes)
    {
        int[] searches = searchIndexes.Distinct().OrderBy(index => index).ToArray();
        return searches.Length == 0
            ? new BooruAddressBatch(null, [])
            : new BooruAddressBatch(searches[^1], searches[..^1]);
    }
}
