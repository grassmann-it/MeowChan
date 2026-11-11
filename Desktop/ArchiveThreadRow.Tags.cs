using System.Collections.Generic;
using System.ComponentModel;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

internal sealed partial record ArchiveThreadRow
{
    /// <summary>Own thread tags as one display line; empty when the thread has none or tags are unavailable.</summary>
    public string TagsText { get; private set; } = string.Empty;
    public bool HasTags => TagsText.Length > 0;

    internal void SetTags(IReadOnlyList<string>? names)
    {
        string text = names is { Count: > 0 } ? Texts.T("tags.rowLabel", string.Join(", ", names)) : string.Empty;
        if (text == TagsText) return;
        TagsText = text;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TagsText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasTags)));
    }
}
