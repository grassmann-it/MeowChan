using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using oChan.Desktop.Localization;
using System;

namespace oChan.Desktop;

/// <summary>Keeps profile and initialization failures visible even when MainWindow cannot be created.</summary>
public sealed class StartupErrorWindow : Window
{
    private readonly TextBlock _heading;
    private readonly Button _exitButton;
    private bool _closed;

    public StartupErrorWindow(string message)
    {
        Width = 560;
        Height = 360;
        MinWidth = 340;
        MinHeight = 240;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        this[!BackgroundProperty] = new DynamicResourceExtension("Workspace.Background");
        this[!ForegroundProperty] = new DynamicResourceExtension("Workspace.Text");

        _heading = new TextBlock
        {
            FontSize = 22,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        };
        SelectableTextBlock detail = new SelectableTextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(14)
        };
        detail[!SelectableTextBlock.ForegroundProperty] = new DynamicResourceExtension("Workspace.Error");
        ScrollViewer scroll = new ScrollViewer
        {
            Content = detail,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
        Border errorPanel = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 18),
            Child = scroll
        };
        errorPanel[!Border.BackgroundProperty] = new DynamicResourceExtension("Workspace.Panel");
        errorPanel[!Border.BorderBrushProperty] = new DynamicResourceExtension("Workspace.Border");
        _exitButton = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            MinWidth = 96,
            IsCancel = true
        };
        _exitButton.Click += (_, _) => Close();
        Grid layout = new Grid
        {
            Margin = new Thickness(24),
            RowDefinitions = new RowDefinitions("Auto,*,Auto")
        };
        layout.Children.Add(_heading);
        Grid.SetRow(errorPanel, 1);
        layout.Children.Add(errorPanel);
        Grid.SetRow(_exitButton, 2);
        layout.Children.Add(_exitButton);
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
