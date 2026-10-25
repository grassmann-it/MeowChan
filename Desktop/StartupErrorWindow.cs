using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using MeowChan.Desktop.Localization;
using System;
using System.IO;

namespace MeowChan.Desktop;

/// <summary>Keeps profile and initialization failures visible even when MainWindow cannot be created.</summary>
public sealed class StartupErrorWindow : Window
{
    private readonly TextBlock _heading;
    private readonly Button _exitButton;
    private bool _closed;

    public StartupErrorWindow(string message)
    {
        using Stream iconStream = AssetLoader.Open(new Uri("avares://MeowChan/Assets/meowchan.png"));
        Icon = new WindowIcon(iconStream);
        Width = 560;
        Height = 360;
        MinWidth = 340;
        MinHeight = 240;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        this[!BackgroundProperty] = new DynamicResourceExtension("Workspace.Background");
        this[!ForegroundProperty] = new DynamicResourceExtension("Workspace.Text");

        _heading = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(16, 16, 16, 12)
        };
        _heading.Classes.Add("page-title");
        SelectableTextBlock detail = new SelectableTextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap
        };
        detail.Classes.Add("workspace-error");
        ScrollViewer scroll = new ScrollViewer
        {
            Content = detail,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
        Border errorPanel = new Border
        {
            Margin = new Thickness(16, 0, 16, 16),
            Child = scroll
        };
        errorPanel.Classes.Add("workspace-section");
        _exitButton = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            MinWidth = 96,
            IsCancel = true
        };
        _exitButton.Click += (_, _) => Close();
        Border footer = new() { Child = _exitButton };
        footer.Classes.Add("workspace-dialog-footer");
        Grid layout = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto")
        };
        layout.Children.Add(_heading);
        Grid.SetRow(errorPanel, 1);
        layout.Children.Add(errorPanel);
        Grid.SetRow(footer, 2);
        layout.Children.Add(footer);
        Content = layout;

        // Texts has built-in English before Initialize, including for profile startup failures.
        ApplyTexts();
        Texts.Changed += OnTextsChanged;
        Closed += (_, _) =>
        {
            _closed = true;
            Texts.Changed -= OnTextsChanged;
        };
    }

    private void ApplyTexts()
    {
        Title = Texts.T("startup.title");
        _heading.Text = Title;
        _exitButton.Content = Texts.T("action.exit");
    }

    private void OnTextsChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_closed)
                ApplyTexts();
        });
    }
}
