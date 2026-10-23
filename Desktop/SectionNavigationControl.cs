using System;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

/// <summary>Explicitly opened contents; compact selection shares the same navigation request.</summary>
public sealed class SectionNavigationControl : Expander
{
    protected override Type StyleKeyOverride => typeof(Expander);
    private readonly WrapPanel _links = new();
    private readonly ComboBox _selection = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _go = new();
    private readonly Grid _compact = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
    private readonly TextBlock _notice = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private SectionNavigationController? _controller;

    public SectionNavigationControl()
    {
        Focusable = true;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _compact.Children.Add(_selection);
        Grid.SetColumn(_go, 1);
        _compact.Children.Add(_go);
        Content = new StackPanel { Spacing = 6, Children = { _links, _compact, _notice } };
        Commands.CommandBindings.BindExisting(this, "sections.open-navigation");
        Commands.CommandBindings.BindExisting(_go, "sections.go-to");
        _go.IsEnabled = false;
        _selection.SelectionChanged += (_, _) => _go.IsEnabled = _selection.SelectedItem is SectionDescriptor;
        _go.Click += (_, _) => { if (_selection.SelectedItem is SectionDescriptor section) Navigate(section.Id); };
        SizeChanged += (_, _) => UpdateWidth();
        AutomationProperties.SetLiveSetting(_notice, AutomationLiveSetting.Polite);
    }

    public void Connect(SectionNavigationController controller)
    {
        if (_controller != null) _controller.TargetUnavailable -= OnUnavailable;
        _controller = controller;
        _controller.TargetUnavailable += OnUnavailable;
        Refresh();
    }

    public void Refresh()
    {
        string? selected = (_selection.SelectedItem as SectionDescriptor)?.Id;
        string? focusedLink = _links.Children.OfType<Button>().FirstOrDefault(button => button.IsKeyboardFocusWithin)?.Tag as string;
        Header = Texts.T("sections.open");
        AutomationProperties.SetName(this, Texts.T("sections.open"));
        AutomationProperties.SetName(_selection, Texts.T("sections.choose"));
        _go.Content = Texts.T("sections.go");
        _links.Children.Clear();
        foreach (SectionDescriptor section in _controller?.Sections ?? Array.Empty<SectionDescriptor>())
        {
            var button = new Button { Content = new TextBlock { Text = section.Title, TextWrapping = TextWrapping.Wrap },
                Margin = new Thickness(0, 0, 8, 6), Tag = section.Id };
            AutomationProperties.SetName(button, section.Title);
            Commands.CommandBindings.BindExisting(button, "sections.go-to");
            button.Click += (_, _) => Navigate(section.Id);
            _links.Children.Add(button);
        }
        _selection.ItemsSource = _controller?.Sections;
        _selection.SelectedItem = _controller?.Sections.FirstOrDefault(section => section.Id == selected);
        // TextSearch uses ToString; an explicit template prevents IDs/delegates being announced.
        _selection.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<SectionDescriptor>((item, _) =>
            new TextBlock { Text = item?.Title, TextWrapping = TextWrapping.Wrap });
        _notice.IsVisible = false;
        IsVisible = (_controller?.Sections.Count ?? 0) > 1;
        UpdateWidth();
        if (focusedLink != null && IsExpanded)
            _links.Children.OfType<Button>().FirstOrDefault(button => Equals(button.Tag, focusedLink))?.Focus();
    }

    public void OpenNavigation() { if (_controller?.IsAvailable != true) return; IsExpanded = true; Focus(); }
    public string? SelectedSectionId => _links.Children.OfType<Button>()
        .FirstOrDefault(button => button.IsKeyboardFocusWithin)?.Tag as string
        ?? (_selection.SelectedItem as SectionDescriptor)?.Id;
    public void Navigate(string id)
    {
        if (_controller == null) return;
        // Collapsing first changes layout; controller resolves geometry after the layout pass.
        IsExpanded = false;
        _controller.NavigateTo(id);
    }
    private void OnUnavailable() { IsExpanded = true; _notice.Text = Texts.T("sections.unavailable"); _notice.IsVisible = true; if (_compact.IsVisible) _selection.Focus(); else Focus(); }
    private void UpdateWidth()
    {
        bool compact = Bounds.Width < 560;
        string? focusedId = _links.Children.OfType<Button>().FirstOrDefault(button => button.IsKeyboardFocusWithin)?.Tag as string;
        bool compactFocused = _compact.IsKeyboardFocusWithin;
        if (compact && focusedId != null)
            _selection.SelectedItem = _controller?.Sections.FirstOrDefault(section => section.Id == focusedId);
        _compact.IsVisible = compact; _links.IsVisible = !compact;
        if (!IsExpanded) return;
        if (compact && focusedId != null) _selection.Focus();
        else if (!compact && compactFocused)
        {
            string? selected = (_selection.SelectedItem as SectionDescriptor)?.Id;
            Button? target = _links.Children.OfType<Button>().FirstOrDefault(button => Equals(button.Tag, selected));
            if (target != null) target.Focus(); else Focus();
        }
    }
}
