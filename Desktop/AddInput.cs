using System;
using System.Threading.Tasks;

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

        string[] tokens = entries.Split(new[] { ',', '\r', '\n' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (string token in tokens)
        {
            await admitAsync(token);
        }
    }
}
