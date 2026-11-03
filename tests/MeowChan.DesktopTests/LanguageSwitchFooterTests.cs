using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class LanguageSwitchFooterTests : IDisposable
{
    public LanguageSwitchFooterTests() => Reset();
    public void Dispose() => Reset();
    private static void Reset() { Texts.Initialize(); Texts.SetLanguage("en"); }

    [Theory]
    [InlineData("es", "Avanzado")]
    [InlineData("fr", "Avancé")]
    [InlineData("ru", "Расширенный")]
    public void ClosedSelectionBoxRebuildsItsTemplateAfterALanguageSwitch(string language, string expected)
    {
        ComboBox modes = new();
        MainWindow.RefreshWorkspaceModeSelectionPresentation(modes);
        ComboBoxItem selected = new() { Tag = "Advanced", Content = "Advanced" };
        ContentPresenter selectionBox = new() { Content = selected, ContentTemplate = modes.SelectionBoxItemTemplate };
        TextBlock cached = Render(selectionBox);

        Assert.Equal("Advanced", cached.Text);
        Texts.SetLanguage(language);
        selected.Content = expected;
        Assert.Equal("Advanced", cached.Text); // The selected item object did not change.

        MainWindow.RefreshWorkspaceModeSelectionPresentation(modes);
        selectionBox.ContentTemplate = modes.SelectionBoxItemTemplate;
        TextBlock refreshed = Render(selectionBox);
        Assert.Equal(expected, refreshed.Text);
        Assert.NotSame(cached, refreshed);
    }

    [Theory]
    [InlineData("es", "Avanzado")]
    [InlineData("fr", "Avancé")]
    [InlineData("ru", "Расширенный")]
    public void OpenSelectionBoxTemplateReadsTheLocalizedModeTitleAfterALanguageSwitch(string language, string expected)
    {
        ComboBox modes = new();
        MainWindow.RefreshWorkspaceModeSelectionPresentation(modes);
        StackPanel details = new();
        TextBlock title = new() { Text = "Advanced" };
        details.Children.Add(title);
        ContentPresenter selectionBox = new() { Content = details, ContentTemplate = modes.SelectionBoxItemTemplate };
        TextBlock cached = Render(selectionBox);

        Assert.Equal("Advanced", cached.Text);
        Texts.SetLanguage(language);
        title.Text = expected;
        Assert.Equal("Advanced", cached.Text);

        MainWindow.RefreshWorkspaceModeSelectionPresentation(modes);
        selectionBox.ContentTemplate = modes.SelectionBoxItemTemplate;
        TextBlock refreshed = Render(selectionBox);
        Assert.Equal(expected, refreshed.Text);
        Assert.NotSame(cached, refreshed);
    }

    private static TextBlock Render(ContentPresenter presenter)
    {
        presenter.UpdateChild();
        return Assert.IsType<TextBlock>(presenter.Child);
    }
}
