using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MeowChan.Booru;

namespace MeowChan.Desktop;

/// <summary>Resolves one Add action without reading the clipboard for explicit input.</summary>
public static class AddInput
{
    /// <summary>
    /// Admits comma- or line-separated entries in order, including invalid entries and duplicates.
    /// The admission callback owns validation and per-entry error reporting; it must handle an
    /// entry's failure if the remaining entries should still be processed.
    /// </summary>
    public static async Task SubmitAsync(string? input, Func<Task<string?>> readClipboardAsync, Func<string, Task> admitAsync)
    {
        string? entries = string.IsNullOrWhiteSpace(input) ? await readClipboardAsync() : input;
        if (string.IsNullOrWhiteSpace(entries))
        {
            return;
        }

        foreach (string token in Split(entries))
        {
            await admitAsync(token);
        }
    }

    /// <summary>
    /// Splits at commas and line breaks. A Danbooru address is kept whole first: its query may hold commas of its own
    /// (<c>tags=rating:g,s</c>), which a plain comma split would cut apart.
    /// </summary>
    internal static IEnumerable<string> Split(string entries)
    {
        char[] separators = { ',', '\r', '\n' };
        const StringSplitOptions options = StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries;
        foreach (string line in entries.Split(new[] { '\r', '\n' }, options))
        {
            int position = 0;
            foreach ((int start, int length) in DanbooruAddress.FindSpans(line))
            {
                foreach (string token in line[position..start].Split(separators, options)) yield return token;
                yield return line.Substring(start, length);
                position = start + length;
            }
            foreach (string token in line[position..].Split(separators, options)) yield return token;
        }
    }
}
