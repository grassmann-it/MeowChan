using System;
using System.Collections.Generic;
using Avalonia.Automation;
using Avalonia.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;

namespace MeowChan.Desktop;

// Own tags of the shown media version. The viewer only shows state and raises a request for the
// exact entry; the owner opens the tag panel (GalleryViewer.TagPanel.cs) for it and reports the result back.
internal sealed partial class GalleryViewer
{
    private Button _tagsButton = null!;
    private IReadOnlyList<string>? _tagNames;
    private string? _tagsUnavailableReason;
    internal event Action<GalleryEntry>? TagsRequested;
    internal Button TagsButton => _tagsButton;

    // A row of the "⋯" menu; its label is the live tag count, so it has no static command label.
    private void CreateTagsButton(Panel menu)
    {
        _tagsButton = new Button { Name = "GalleryTags" };
        _tagsButton.Click += (_, _) => RequestTags();
        AddOverflowItem(_tagsButton, "media.tags.edit", host: menu);
        UpdateTagsButton();
    }

    private void RequestTags()
    {
        if (_tagPanelOpen) { CloseTagPanel(); return; } // the same command that opened the panel closes it
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

    // The row's label is the live tag count; on the toolbar it is the button's name and tooltip (the visible text stays "Tags").
    private string TagsLabel()
    {
        int count = _tagNames?.Count ?? 0;
        return count > 0 ? Texts.T("tags.media.buttonCount", count) : Texts.T("tags.media.button");
    }

    private void UpdateTagsButton()
    {
        if (_tagsButton == null) return;
        string label = TagsLabel();
        int count = _tagNames?.Count ?? 0;
        SetButtonLabel(_tagsButton, label);
        string tip = _tagNames == null ? _tagsUnavailableReason ?? Texts.T("tags.media.button")
            : count > 0 ? Texts.T("tags.rowLabel", string.Join(", ", _tagNames)) : Texts.T("tags.media.hint");
        ToolTip.SetTip(_tagsButton, tip);
        AutomationProperties.SetName(_tagsButton, label);
        AutomationProperties.SetHelpText(_tagsButton, tip);
        _tagsButton.IsEnabled = _entry != null && _tagNames != null;
    }
}
