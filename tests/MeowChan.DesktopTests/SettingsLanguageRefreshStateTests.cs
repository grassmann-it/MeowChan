using System.Reflection;
using System.Runtime.CompilerServices;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class SettingsLanguageRefreshStateTests : IDisposable
{
    public SettingsLanguageRefreshStateTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LabelRefreshPreservesSemanticDraftRevisionAndPendingWait(bool simulateBindingClears)
    {
        // Exercise the production choice-refresh helper and CLR setters, not a source
        // string model. No Avalonia controls, dispatcher, filesystem probe or Save runs.
        var editor = CreateEditor();
        var notifications = new List<string?>();
        var slideshowVisibility = new List<bool>();
        editor.PropertyChanged += (_, e) =>
        {
            notifications.Add(e.PropertyName);
            if (e.PropertyName == nameof(SettingsEditor.IsGallerySlideshow))
                slideshowVisibility.Add(editor.IsGallerySlideshow);
            if (!simulateBindingClears) return;
            // ItemsSource replacement can cause synchronous two-way selection clears.
            switch (e.PropertyName)
            {
                case nameof(SettingsEditor.ThemeChoices): editor.SelectedTheme = null; break;
                case nameof(SettingsEditor.WorkspaceChoices): editor.SelectedWorkspaceMode = null; break;
                case nameof(SettingsEditor.LanguageChoices): editor.SelectedLanguage = null; break;
                case nameof(SettingsEditor.ReadingFontSizeChoices): editor.SelectedReadingFontSize = null; break;
                case nameof(SettingsEditor.GalleryPlaybackModeChoices): editor.SelectedGalleryPlaybackMode = null; break;
            }
        };
        using var wait = new CancellationTokenSource();
        Set(editor, "_destinationWait", wait);
        Set(editor, "_destinationStatus", DownloadDestinationStatus.BelowReserve);
        Set(editor, "_draftRevision", 37L);

        // Includes a same-code label refresh as well as actual EN/DE transitions.
        foreach (string language in new[] { "de", "en", "en", "de" })
        {
            Texts.SetLanguage(language);
            int visibilityStart = slideshowVisibility.Count;
            RefreshChoiceLabels(editor);

            // A transient ComboBox clear must not publish a hidden slideshow
            // field: that removes focus and collapses raw caret/selection in UI.
            Assert.Equal(new[] { true }, slideshowVisibility.Skip(visibilityStart));
            Assert.Equal(37L, Get<long>(editor, "_draftRevision"));
            Assert.False(wait.IsCancellationRequested);
            Assert.Same(wait, Get<CancellationTokenSource>(editor, "_destinationWait"));
            Assert.Equal(DownloadDestinationStatus.BelowReserve, Get<DownloadDestinationStatus?>(editor, "_destinationStatus"));
            Assert.False(Get<bool>(editor, "_refreshingLanguageLabels"));
            Assert.Equal("Dark", editor.SelectedTheme!.Value);
            Assert.Equal(Texts.T("theme.dark"), editor.SelectedTheme.Label);
            Assert.Equal("Advanced", editor.SelectedWorkspaceMode!.Value);
            Assert.Equal("Large", editor.SelectedReadingFontSize!.Value);
            Assert.Equal("Slideshow", editor.SelectedGalleryPlaybackMode!.Value);
            Assert.Equal("en", editor.SelectedLanguage!.Code); // unsaved choice is not active UI language
            // SetField deliberately retains an equal immutable choice on a same-code
            // refresh. Selection is value-based; reference identity is not the contract.
            Assert.Equal(editor.SelectedTheme, editor.ThemeChoices.Single(item => item.Value == "Dark"));
            Assert.Equal(editor.SelectedReadingFontSize, editor.ReadingFontSizeChoices.Single(item => item.Value == "Large"));
        }
        Assert.Contains(nameof(SettingsEditor.SelectedTheme), notifications);
        Assert.Contains(nameof(SettingsEditor.SelectedGalleryPlaybackMode), notifications);

        // Genuine input after refresh must still reject the old operation's revision.
        editor.SelectedTheme = editor.ThemeChoices.Single(item => item.Value == "Light");
        Assert.Equal(38L, Get<long>(editor, "_draftRevision"));
        Assert.True(wait.IsCancellationRequested);
        Assert.Null(Get<DownloadDestinationStatus?>(editor, "_destinationStatus"));

        // A genuine later mode change must still update the conditional editor.
        slideshowVisibility.Clear();
        editor.SelectedGalleryPlaybackMode = editor.GalleryPlaybackModeChoices.Single(item => item.Value == "Manual");
        Assert.False(editor.IsGallerySlideshow);
        Assert.Equal(new[] { false }, slideshowVisibility);
        Assert.Equal(39L, Get<long>(editor, "_draftRevision"));
    }

    [Fact]
    public void GuardRestoresOnFailureAndDoesNotSuppressUnrelatedRealInput()
    {
        var editor = CreateEditor();
        using var wait = new CancellationTokenSource();
        Set(editor, "_destinationWait", wait);
        editor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(SettingsEditor.ThemeChoices)) return;
            editor.DownloadPath = "changed-by-real-input";
            throw new InvalidOperationException("binding failure");
        };

        var error = Assert.Throws<TargetInvocationException>(() => RefreshChoiceLabels(editor));
        Assert.IsType<InvalidOperationException>(error.InnerException);
        Assert.False(Get<bool>(editor, "_refreshingLanguageLabels"));
        Assert.Equal(1L, Get<long>(editor, "_draftRevision"));
        Assert.True(wait.IsCancellationRequested);
    }

    private static SettingsEditor CreateEditor()
    {
        // Match existing CLR-only Settings tests; bypass InitializeComponent entirely.
        var editor = (SettingsEditor)RuntimeHelpers.GetUninitializedObject(typeof(SettingsEditor));
        Set(editor, "_config", new Config());
        Set(editor, "_selectedTheme", new SettingsChoice("Dark", "old theme label"));
        Set(editor, "_selectedWorkspaceMode", new SettingsChoice("Advanced", "old mode label"));
        Set(editor, "_selectedReadingFontSize", new SettingsChoice("Large", "old font label"));
        Set(editor, "_selectedGalleryPlaybackMode", new SettingsChoice("Slideshow", "old gallery label"));
        Set(editor, "_selectedLanguage", new LanguageInfo("en", "old language label"));
        return editor;
    }

    private static void RefreshChoiceLabels(SettingsEditor editor) =>
        typeof(SettingsEditor).GetMethod("RefreshChoiceLabels", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(editor, null);

    private static void Set(SettingsEditor editor, string fieldName, object? value) =>
        typeof(SettingsEditor).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(editor, value);

    private static T Get<T>(SettingsEditor editor, string fieldName) =>
        (T)typeof(SettingsEditor).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;
}
