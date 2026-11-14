using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.MediaActions;
using MeowChan.Gallery;

namespace MeowChan.Desktop;

// A saved file the gallery has no viewer for (a PDF, an archive, a Flash file) is not a dead end: the plate that says so
// carries the two ways forward, the system's default program and the file's folder. Both go through the shell, which
// owns the launcher; the viewer only reports the request for the exact file shown.
internal sealed partial class GalleryViewer
{
    private Border _fileOnlyPlate = null!;
    private TextBlock _fileOnlyText = null!;
    private Button _openExternalButton = null!;
    private Button _fileOnlyFolderButton = null!;

    internal event Action<GalleryEntry>? OpenExternalRequested;
    internal event Action<GalleryEntry>? CopyPathRequested;

    internal bool FileOnlyShown => _fileOnlyPlate.IsVisible;
    internal string FileOnlyTextValue => _fileOnlyText.Text ?? string.Empty;
    internal Button OpenExternalButton => _openExternalButton;
    internal Button FileOnlyFolderButton => _fileOnlyFolderButton;

    private void InitializeFileOnlyPlate()
    {
        _fileOnlyText = new TextBlock
        {
            Name = "GalleryFileOnlyText", TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("Workspace.Text")
        };
        _openExternalButton = CommandBindings.CreateButton("media.open-external",
            () => { if (_entry != null) OpenExternalRequested?.Invoke(_entry); }, CanOpenExternal);
        _openExternalButton.Name = "GalleryOpenExternal";
        _fileOnlyFolderButton = CommandBindings.CreateButton("media.show-in-folder",
            () => { if (_entry != null) ShowInFolderRequested?.Invoke(_entry); }, () => _entry != null && ShowInFolderRequested != null);
        _fileOnlyFolderButton.Name = "GalleryFileOnlyFolder";
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        buttons.Children.Add(_openExternalButton);
        buttons.Children.Add(_fileOnlyFolderButton);
        StackPanel content = new() { Spacing = 8 };
        content.Children.Add(_fileOnlyText);
        content.Children.Add(buttons);
        _fileOnlyPlate = new Border
        {
            Name = "GalleryFileOnly", Classes = { "workspace-media-caption" }, Child = content, IsVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 650
        };
        _viewport.Children.Add(_fileOnlyPlate);
    }

    private bool CanOpenExternal() => _entry != null && OpenExternalRequested != null
        && MediaFileActions.CanOpenWithDefaultProgram(_entry.Asset.Path);

    private void ShowFileOnlyPlate(GalleryEntry entry)
    {
        _status.Text = string.Empty;
        _status.IsVisible = false;
        RefreshFileOnlyText(entry);
        _fileOnlyPlate.IsVisible = true;
    }

    private void HideFileOnlyPlate()
    {
        if (_fileOnlyPlate != null) _fileOnlyPlate.IsVisible = false;
    }

    private void RefreshFileOnlyText() { if (_entry != null && _fileOnlyPlate.IsVisible) RefreshFileOnlyText(_entry); }

    private void RefreshFileOnlyText(GalleryEntry entry)
    {
        string text = MediaFileActions.FileOnlyText(entry);
        // A type that could run as a program is only ever shown in its folder, and the plate says so in words.
        if (!MediaFileActions.CanOpenWithDefaultProgram(entry.Asset.Path)) text += Environment.NewLine + Texts.T("gallery.fileOnly.blocked");
        _fileOnlyText.Text = text;
        Refresh(_openExternalButton);
        Refresh(_fileOnlyFolderButton);
        static void Refresh(Button button) { if (button.Command is DelegateCommand command) CommandBindings.Refresh(button); }
    }
}
