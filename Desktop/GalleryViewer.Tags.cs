using System;
using System.Collections.Generic;
using Avalonia.Automation;
using Avalonia.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;

namespace MeowChan.Desktop;

// Own tags of the shown media version. The viewer only shows state and raises a request for the
// exact entry; the owner resolves the target, opens the dialog and reports the result back.
internal sealed partial class GalleryViewer
{
    private Button _tagsButton = null!;
    private IReadOnlyList<string>? _tagNames;
    private string? _tagsUnavailableReason;
    internal event Action<GalleryEntry>? TagsRequested;
    internal Button TagsButton => _tagsButton;

    private void CreateTagsButton(Panel media) =>
        _tagsButton = Command(media, "GalleryTags", Texts.T("tags.media.button"), RequestTags);

    private void RequestTags()
    {
        if (_entry is { } entry && _tagNames != null && CanRequestFavorite)
        {
            PauseSlideshow();
            TagsRequested?.Invoke(entry);
        }
    }

    /// <summary>
    /// Tag names on the shown medium, or null with a reason when it cannot be tagged or the index is
    /// unavailable. Until the owner reports, the button stays disabled rather than guessing.
    /// </summary>
    internal void SetMediaTags(IReadOnlyList<string>? names, string? unavailableReason = null)
    {
        _tagNames = names;
        _tagsUnavailableReason = names == null ? unavailableReason : null;
        UpdateTagsButton();
    }

    private void ResetMediaTags()
    {
        _tagNames = null;
        _tagsUnavailableReason = null;
        UpdateTagsButton();
    }

    private void UpdateTagsButton()
    {
        if (_tagsButton == null) return;
        int count = _tagNames?.Count ?? 0;
        string label = count > 0 ? Texts.T("tags.media.buttonCount", count) : Texts.T("tags.media.button");
        _tagsButton.Content = label;
        string tip = _tagNames == null ? _tagsUnavailableReason ?? Texts.T("tags.media.button")
            : count > 0 ? Texts.T("tags.rowLabel", string.Join(", ", _tagNames)) : Texts.T("tags.media.hint");
        ToolTip.SetTip(_tagsButton, tip);
        AutomationProperties.SetName(_tagsButton, label);
        AutomationProperties.SetHelpText(_tagsButton, tip);
        _tagsButton.IsEnabled = _entry != null && _tagNames != null;
    }
}
