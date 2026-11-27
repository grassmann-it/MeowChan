using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MeowChan.Booru;
using MeowChan.Persistence;

namespace MeowChan.Desktop.Booru;

internal sealed class BooruMd5Lookup(Func<IReadOnlyList<BooruWorkbenchServices>> active,
    Func<BooruWorkbenchServices, string, Task> submit)
{
    internal IEnumerable<BooruWorkbenchServices> Sources(string query) => active().Where(source =>
        (source.Search.ProviderId == DanbooruPostReference.ProviderId || source.Source?.BooruProfile is not null)
        && BooruInput.Parse(BooruInputMode.Tags, query, source.Search.ProviderId, source.Source?.BooruProfile).IsValid);

    internal Task SearchAsync(BooruWorkbenchServices source, string query) => Sources(query).Contains(source)
        ? submit(source, query) : Task.CompletedTask;

    internal static string? Query(string? md5) => PostFilterRules.TryParseMd5(md5, out byte[] bytes)
        ? "md5:" + Convert.ToHexStringLower(bytes) : null;

    internal static bool IsMd5Query(string? query) =>
        DanbooruTagQuery.TryParse(query, out DanbooruTagQuery? parsed, out _) && parsed is { IsMd5Lookup: true };
}
