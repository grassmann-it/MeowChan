using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Data;
using Avalonia.Threading;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

// The production template factory and Avalonia binding are exercised in process.
// This fixture deliberately does not impersonate MainWindow's language callback.
[Collection("Localization")]
public sealed class PluginFooterLanguageTransitionBoundaryTests
{
    [Theory]
    [InlineData("Barebones", false)]
    [InlineData("Simple", false)]
    [InlineData("Advanced", false)]
    [InlineData("Barebones", true)]
    [InlineData("Simple", true)]
    [InlineData("Advanced", true)]
    public void MaterializedSelectionTracksTemplateBindingAcrossConsecutiveLanguages(string mode, bool details) =>
        OnOwnedDispatcher(() =>
        {
            ComboBox modes = new();
            ComboBoxItem selected = new() { Tag = mode };
            StackPanel panel = new();
            TextBlock title = new();
            panel.Children.Add(title);
            // Content is retained throughout; no selection reset or presenter replacement
            // is allowed to accidentally repair an old-language child.
            ContentPresenter presenter = new() { Content = details ? panel : selected };
            using var binding = presenter.Bind(ContentPresenter.ContentTemplateProperty,
                new Binding(nameof(ComboBox.SelectionBoxItemTemplate)) { Source = modes });
            string key = "mode." + mode.ToLowerInvariant();
            title.Text = Texts.T(key);
            selected.Content = title.Text;
            MainWindow.RefreshWorkspaceModeSelectionPresentation(modes);
            TextBlock child = Render(presenter);
            Assert.Equal(Texts.T(key), child.Text);
            object content = presenter.Content!;

            // ES, FR and DE legitimately share "Simple". Revisit RU between
            // those languages so each strict stale-label negative control has
            // distinct adjacent labels for all three modes, without changing packs.
            foreach (string language in new[] { "ru", "fr", "ru", "de" })
            {
                string oldLabel = child.Text!;
                Texts.SetLanguage(language);
                Assert.Equal(language, Texts.Language);
                title.Text = Texts.T(key);
                selected.Content = title.Text;
                Assert.NotEqual(oldLabel, title.Text);
                Assert.Equal(oldLabel, child.Text); // Materialized negative control.
                var oldTemplate = modes.SelectionBoxItemTemplate;

                MainWindow.RefreshWorkspaceModeSelectionPresentation(modes);

                Assert.NotSame(oldTemplate, modes.SelectionBoxItemTemplate);
                Assert.Same(modes.SelectionBoxItemTemplate, presenter.ContentTemplate);
                TextBlock next = Render(presenter);
                Assert.NotSame(child, next);
                Assert.Same(content, presenter.Content);
                Assert.Equal(mode, selected.Tag);
                Assert.Equal(Texts.T(key), next.Text);
                Assert.NotEqual(oldLabel, next.Text);
                child = next;
            }
        });

    [Fact]
    public void DetailToCompactTransitionsDoNotReuseThePreviousLanguageChild() =>
        OnOwnedDispatcher(() =>
        {
            ComboBox modes = new();
            ComboBoxItem selected = new() { Tag = "Advanced" };
            ContentPresenter presenter = new();
            using var binding = presenter.Bind(ContentPresenter.ContentTemplateProperty,
                new Binding(nameof(ComboBox.SelectionBoxItemTemplate)) { Source = modes });
            TextBlock? previous = null;
            foreach (string language in new[] { "es", "fr", "ru", "de" })
            {
                Texts.SetLanguage(language);
                string expected = Texts.T("mode.advanced");
                foreach (bool details in new[] { true, false, true })
                {
                    selected.Content = expected;
                    presenter.Content = details
                        ? new StackPanel { Children = { new TextBlock { Text = expected },
                            new TextBlock { Text = Texts.T("workview.description.advanced") } } }
                        : selected;
                    MainWindow.RefreshWorkspaceModeSelectionPresentation(modes);
                    TextBlock current = Render(presenter);
                    Assert.Equal(expected, current.Text);
                    Assert.NotSame(previous, current);
                    previous = current;
                }
            }
        });

    // Avalonia 12.1.4 controls use CurrentDispatcher, but ValueStore's binding
    // completion still verifies the process-primary UIThread. Merely creating a
    // dedicated CurrentDispatcher does not fix disposal when another xUnit worker
    // created the primary first. Localization is a DisableParallelization collection:
    // temporarily align the primary slot, without resetting any existing dispatcher,
    // draining its queue, or changing the runner. Fail closed if the pinned seam moves.
    private static void OnOwnedDispatcher(Action test)
    {
        FieldInfo primarySlot = typeof(Dispatcher).GetField("s_uiThread",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Avalonia 12.1.4 primary dispatcher slot is missing.");
        object? previousPrimary = primarySlot.GetValue(null);
        Dispatcher owner = Dispatcher.CurrentDispatcher;
        string previousLanguage = Texts.Language;
        try
        {
            primarySlot.SetValue(null, owner);
            owner.Invoke(() =>
            {
                owner.VerifyAccess();
                Assert.Same(owner, Dispatcher.UIThread);
                try
                {
                    Texts.Initialize();
                    Texts.SetLanguage("es");
                    // All controls, binding creation, transitions, assertions AND the
                    // using-var disposal remain inside this synchronous invocation.
                    test();
                }
                finally
                {
                    owner.VerifyAccess();
                    Texts.Initialize();
                    Texts.SetLanguage(previousLanguage);
                }
            }, DispatcherPriority.Send);
        }
        finally
        {
            primarySlot.SetValue(null, previousPrimary);
        }
    }

    private static TextBlock Render(ContentPresenter presenter)
    {
        presenter.Dispatcher.VerifyAccess();
        Assert.Same(Dispatcher.UIThread, presenter.Dispatcher);
        presenter.UpdateChild();
        return Assert.IsType<TextBlock>(presenter.Child);
    }
}
