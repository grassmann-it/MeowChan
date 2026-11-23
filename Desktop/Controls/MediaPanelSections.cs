using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;

namespace MeowChan.Desktop.Controls;

internal sealed class MediaPanelSections : StackPanel
{
    private readonly ContentControl[] _sections = [new(), new(), new(), new()];
    private readonly Border[] _rules = [Rule(), Rule(), Rule()];

    protected override Type StyleKeyOverride => typeof(StackPanel);

    internal MediaPanelSections()
    {
        Spacing = WorkspaceMetrics.MediaPanelSectionGap;
        for (int i = 0; i < _sections.Length; ++i)
        {
            if (i > 0) Children.Add(_rules[i - 1]);
            Children.Add(_sections[i]);
            _sections[i].IsVisible = false;
        }
    }

    internal Control? Rating { get => _sections[0].Content as Control; set => Set(0, value); }
    internal Control? Tags { get => _sections[1].Content as Control; set => Set(1, value); }
    internal Control? Info { get => _sections[2].Content as Control; set => Set(2, value); }
    internal Control? Actions { get => _sections[3].Content as Control; set => Set(3, value); }

    private void Set(int index, Control? content)
    {
        _sections[index].Content = content;
        _sections[index].IsVisible = content != null;
        bool priorVisible = _sections[0].IsVisible;
        for (int i = 1; i < _sections.Length; ++i)
        {
            _rules[i - 1].IsVisible = priorVisible && _sections[i].IsVisible;
            priorVisible |= _sections[i].IsVisible;
        }
    }

    private static Border Rule() => new()
    {
        Name = "MediaPanelDivider", Height = 1, IsHitTestVisible = false, IsVisible = false,
        [!Border.BackgroundProperty] = new DynamicResourceExtension("Workspace.Border")
    };
}
