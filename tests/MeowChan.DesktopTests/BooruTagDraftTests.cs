using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.Raw;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class BooruTagDraftTests
{
    [Fact]
    public void ExclusionAlsoValidatesTheResultingByteLength()
    {
        string tag = new('é', 128);
        Assert.True(BooruSourceTagSearch.Plan([tag]).IsValid);
        BooruTagDraftEdit edit = BooruTagDraft.Edit(BooruInputMode.Tags, "rating:e", tag, BooruTagDraftAction.Exclude);
        Assert.Null(edit.Replacement);
        Assert.Equal(BooruInputIssue.TooLong, edit.Issue);
    }

    [Fact]
    public void LiveChipEditsUseTheProviderGrammarAndKeepInvalidDrafts() => Run(() =>
    {
        using Fixture fixture = new();
        fixture.Seed();
        foreach (var (draft, tag, expected, valid) in new[]
        {
            ("1boy rating:e", "solo", "1boy rating:e solo", true),
            ("1boy\tsolo", "solo", "1boy\tsolo", true),
            ("1boy\r\n-rating:g,s", "solo", "1boy -rating:g,s solo", true),
            ("1Boy", "1boy", "1Boy", true),
            ("1boy -sky", "solo", "1boy -sky", false),
            ("1boy rating:e -sky", "solo", "1boy rating:e -sky", false),
            ("1boy -SOLO", "solo", "1boy SOLO", true),
            ("-SOLO solo", "solo", "SOLO", true),
            ("1boy rating:x", "solo", "1boy rating:x", false),
            ("1boy +sky", "solo", "1boy +sky", false),
            ("1boy solo sky", "solo", "1boy solo sky", false),
            ("\u00a0", "solo", "\u00a0", false),
            ("", "name_(series)", "", false)
        })
        {
            fixture.SetDraft(draft);
            fixture.Click(fixture.Chip(tag));
            Assert.Equal(expected, fixture.Input.Text ?? string.Empty);
            Assert.Equal(expected, fixture.View.Model.Search.View.Input);
            Assert.Equal(!valid, fixture.Note.Classes.Contains("workspace-error"));
            if (valid) Assert.Equal(BooruInputKind.Tags, fixture.View.Model.Search.View.Parsed.Kind);
        }

        fixture.SetDraft("SoLo rating:e");
        fixture.UseMenu(fixture.Chip("solo"), BooruWorkbenchView.MenuTagRemove);
        Assert.Equal("rating:e", fixture.Input.Text);
        fixture.SetDraft("-SOLO");
        fixture.UseMenu(fixture.Chip("solo"), BooruWorkbenchView.MenuTagRemove);
        Assert.True(string.IsNullOrEmpty(fixture.Input.Text));
        Assert.Equal(BooruInputKind.Empty, fixture.View.Model.Search.View.Parsed.Kind);

        fixture.Seed(mode: BooruInputMode.Post, draft: "0");
        fixture.Click(fixture.Chip("solo"));
        Assert.Equal("0", fixture.Input.Text);
        Assert.Contains(Texts.T("booru.ui.issue.PostId"), fixture.Note.Text);
        fixture.Seed(mode: BooruInputMode.Post, draft: "101");
        fixture.Click(fixture.Chip("solo"));
        Assert.Equal(BooruInputMode.Tags, fixture.View.Model.Search.View.Mode);
        Assert.Equal("solo", fixture.Input.Text);
        Assert.Equal("101", fixture.View.Model.Search.View.ExecutedPostId);
        Assert.Equal(0, fixture.Harness.ProviderRequests);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ModifiersAndPopupEditOnlyTheOriginAndOnlyExplicitSearchSends(bool separate) => Run(() =>
    {
        using Fixture fixture = new(separate);
        fixture.Seed(draft: "other");
        Assert.True(fixture.View.TryShowQueryInNewTab("1Boy"));
        int origin = fixture.View.SearchTabs.ActiveId;
        fixture.Seed(draft: "1Boy");
        fixture.Window.SetRenderScaling(1.5);
        fixture.Settle();

        Assert.True(fixture.Input.Focus());
        fixture.Click(fixture.Chip("solo"), RawInputModifiers.Shift);
        Assert.Equal("1Boy -solo", fixture.Input.Text);
        Assert.True(fixture.Input.Focus());
        fixture.Click(fixture.Chip("1boy"), RawInputModifiers.Alt);
        Assert.Equal("-1Boy -solo", fixture.Input.Text);

        Button solo = fixture.Chip("solo");
        Assert.True(solo.Focus(NavigationMethod.Tab));
        fixture.Key(PhysicalKey.Space);
        Assert.Equal("-1Boy solo", fixture.Input.Text);
        Assert.Same(solo, fixture.Window.FocusManager!.GetFocusedElement());
        fixture.UseMenu(solo, BooruWorkbenchView.MenuTagExclude);
        Assert.Equal("-1Boy -solo", fixture.Input.Text);
        ContextMenu pointerMenu = fixture.OpenMenu(solo, pointer: true);
        BooruResultActionsTests.Click(BooruResultActionsTests.Item(pointerMenu, "tags.copy-name"));
        Assert.Equal("solo", BooruComponentDispatcher.Wait(fixture.Window.Clipboard!.TryGetTextAsync()));
        Assert.Equal("-1Boy -solo", fixture.Input.Text);

        ContextMenu popup = fixture.OpenMenu(solo);
        MenuItem stale = BooruResultActionsTests.Item(popup, BooruWorkbenchView.MenuTagRemove);
        fixture.Select(BooruSearchTabs.PrimaryId);
        Assert.Equal("other", fixture.Input.Text);
        Assert.False(popup.IsOpen);
        Assert.False(stale.Command!.CanExecute(null));
        Assert.False(fixture.Note.IsVisible);
        Assert.Equal("-1Boy -solo", fixture.View.SearchTabs.ViewOf(origin)!.Input);
        fixture.Select(origin);
        Assert.Equal("-1Boy -solo", fixture.Input.Text);
        Assert.False(stale.Command.CanExecute(null));
        fixture.UseMenu(fixture.Chip("solo"), BooruWorkbenchView.MenuTagRemove);
        Assert.Equal("-1Boy", fixture.Input.Text);

        popup = fixture.OpenMenu(fixture.Chip("1boy"));
        stale = BooruResultActionsTests.Item(popup, BooruWorkbenchView.MenuTagRemove);
        Assert.True(fixture.View.SearchTabs.Close(origin));
        fixture.Settle();
        Assert.Null(fixture.View.SearchTabs.ViewOf(origin));
        Assert.False(popup.IsOpen);
        Assert.False(stale.Command!.CanExecute(null));
        Assert.Equal("other", fixture.Input.Text);
        Assert.Equal(0, fixture.Harness.ProviderRequests);
        Assert.Equal(0, fixture.Harness.Previews.Requests);
        Assert.Empty(fixture.Harness.Previews.SampleRequests);

        fixture.Harness.Serve(pages: 1, perPage: 1);
        if (separate)
        {
            Assert.True(fixture.Input.Focus());
            fixture.Key(PhysicalKey.Enter);
        }
        else fixture.Click(fixture.Find<Button>(BooruWorkbenchView.SubmitName));
        Assert.Equal("other", Assert.Single(fixture.Harness.Search.Searches).Query);
    });

    [Fact]
    public void NamesThemeColorsAndMenuFollowTypingAndLanguageInTheArrangedPage() => Run(() =>
    {
        using Fixture fixture = new();
        fixture.Seed();
        fixture.Window.SetRenderScaling(1.5);
        Assert.True(fixture.Input.Focus());
        fixture.Key(PhysicalKey.Digit2, RawInputModifiers.Alt);
        Assert.Same(fixture.Input, fixture.Window.FocusManager!.GetFocusedElement());
        Assert.True(string.IsNullOrEmpty(fixture.Input.Text));
        fixture.Window.KeyTextInput("猫");
        fixture.Settle();
        Assert.Equal("猫", fixture.Input.Text);

        string[] languages = ["en", "de", .. Texts.BundledPackCodes()];
        foreach (string language in languages)
        {
            Texts.SetLanguage(language);
            fixture.SetDraft(string.Empty);
            Button chip = fixture.Chip("solo");
            ChipOutline outline = Assert.IsType<ChipOutline>(chip.Content);
            Rect before = fixture.Bounds(chip);
            var idleStroke = outline.Stroke;
            Border categoryMark = Assert.Single(outline.GetVisualDescendants().OfType<Border>(),
                mark => mark.Classes.Contains(WorkspaceChips.CategoryMark));
            var categoryBrush = categoryMark.Background;

            Assert.True(fixture.Input.Focus());
            fixture.Window.KeyTextInput("SoLo");
            fixture.Settle();
            Assert.Same(chip, fixture.Chip("solo"));
            Assert.False(outline.IsDashed);
            Assert.NotEqual(idleStroke, outline.Stroke);
            string category = Texts.T("booru.ui.tagCategory.General");
            string tagAndCategory = Texts.T("media.breakdown.summary", category, "solo");
            Assert.Equal(Texts.T("booru.ui.tags.alreadyInSearch", tagAndCategory), AutomationProperties.GetName(chip));

            fixture.Input.SelectAll();
            fixture.Window.KeyTextInput("-sOlO");
            fixture.Settle();
            Assert.True(outline.IsDashed);
            Assert.NotEqual(idleStroke, outline.Stroke);
            Assert.Same(categoryBrush, categoryMark.Background);
            Assert.Equal(Texts.T("media.filter.combo.excluded", tagAndCategory), AutomationProperties.GetName(chip));
            Assert.Equal(AutomationProperties.GetName(chip), ToolTip.GetTip(chip));
            Rect after = fixture.Bounds(chip);
            Assert.InRange(Math.Abs(after.Center.X - before.Center.X), 0, 1);
            Assert.InRange(Math.Abs(after.Center.Y - before.Center.Y), 0, 1);
            Assert.InRange(Math.Abs(after.Width - before.Width), 0, 1);

            ContextMenu menu = fixture.OpenMenu(chip);
            MenuItem remove = BooruResultActionsTests.Item(menu, BooruWorkbenchView.MenuTagRemove);
            Assert.Equal(Texts.T("action.remove"), AutomationProperties.GetName(remove));
            Assert.True(remove.Bounds.Width > 0 && remove.Bounds.Height > 0);
            BooruResultActionsTests.Click(remove);
            Assert.True(string.IsNullOrEmpty(fixture.Input.Text));
        }
        Assert.Equal(0, fixture.Harness.ProviderRequests);

        EffectiveShortcutBindings mac = EffectiveShortcutBindings.Build(platform: ShortcutPlatform.MacOS);
        Assert.True(mac.Matches(new KeyEventArgs { Key = Avalonia.Input.Key.F10, KeyModifiers = KeyModifiers.Shift },
            "menu.context-open", "booru"));
        KeyEventArgs optionEnter = new() { Key = Avalonia.Input.Key.Enter, KeyModifiers = KeyModifiers.Alt };
        Assert.False(BooruWorkbenchView.MatchesSearchShortcut(optionEnter, BooruWorkbenchView.SubmitCommand, mac));
        Assert.False(BooruWorkbenchView.MatchesSearchShortcut(optionEnter, BooruWorkbenchView.AlternateSubmitCommand, mac));
    });

    private static void Run(Action test) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        Texts.Initialize();
        string previousLanguage = Texts.Language;
        Texts.SetLanguage("en");
        try { test(); }
        finally { Texts.SetLanguage(previousLanguage); }
    });

    private sealed class Fixture : IDisposable
    {
        private readonly BooruPage? _page;
        private readonly BooruPostView _post = BooruUiFixtures.Post("101", preview: false,
            tags: ["1boy", "solo", "sky", "name_(series)"]);

        internal BooruUiHarness Harness { get; } = new();
        internal Window Window { get; }
        internal BooruWorkbenchView View { get; }
        internal TextBox Input => Find<TextBox>(BooruWorkbenchView.InputName);
        internal TextBlock Note => Find<TextBlock>(BooruWorkbenchView.LinkNoteName);

        internal Fixture(bool separate = false)
        {
            if (separate) Window = new BooruWorkbenchWindow(Harness.Services);
            else
            {
                _page = new BooruPage(Harness.Services);
                Window = new Window { Content = _page };
            }
            Window.RequestedThemeVariant = ThemeVariant.Dark;
            Window.Show();
            Window.Activate();
            Window.PlatformImpl!.Resize(new Size(1152, 700), WindowResizeReason.User);
            View = _page?.View ?? Assert.Single(Window.GetVisualDescendants().OfType<BooruWorkbenchView>());
            View.NewSearchTarget = () => Config.BooruSearchTargetCurrentTab;
            View.RestoreResultView(BooruResultView.List, Desktop.MediaTileSize.Medium);
            Settle();
        }

        internal void Seed(BooruInputMode mode = BooruInputMode.Tags, string draft = "")
        {
            // A restored result is rendered through the production list without requiring an initial provider request.
            var search = View.Model.Search;
            search.Restore(search.View with
            {
                Mode = mode, Input = draft, Parsed = BooruInput.Parse(mode, draft), Status = BooruSearchStatus.Results,
                ExecutedQuery = mode == BooruInputMode.Tags ? "sky" : null,
                ExecutedPostId = mode == BooruInputMode.Post ? "101" : null,
                Rows = [new BooruResultRowView(_post)]
            }, View.SearchTabs.ActiveId);
            Settle();
        }

        internal void SetDraft(string text)
        {
            Input.Text = text;
            Settle();
            Assert.Equal(text, View.Model.Search.View.Input);
        }

        internal void Select(int id)
        {
            TabControl tabs = Find<TabControl>(BooruWorkbenchView.TabsName);
            tabs.SelectedItem = Assert.Single(tabs.Items.OfType<TabItem>(), tab => Equals(id, tab.Tag));
            Settle();
            Assert.Equal(id, View.SearchTabs.ActiveId);
        }

        internal T Find<T>(string name) where T : Control =>
            Assert.Single(View.GetVisualDescendants().OfType<T>(), control => control.Name == name);

        internal Button Chip(string tag) => Find<Button>(BooruWorkbenchView.ResultTagPrefix + "101:" + tag);

        internal Rect Bounds(Control control) =>
            new(control.TranslatePoint(default, Window) ?? throw new InvalidOperationException(control.Name), control.Bounds.Size);

        internal void Click(Control control, RawInputModifiers modifiers = RawInputModifiers.None, MouseButton button = MouseButton.Left)
        {
            Rect bounds = Bounds(control);
            Assert.True(bounds.Width > 0 && bounds.Height > 0 && new Rect(Window.ClientSize).Contains(bounds.Center));
            Window.MouseDown(bounds.Center, button, modifiers);
            Window.MouseUp(bounds.Center, button, modifiers);
            Settle();
        }

        internal void Key(PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
        {
            Window.KeyPressQwerty(key, modifiers);
            Window.KeyReleaseQwerty(key, modifiers);
            Settle();
        }

        internal ContextMenu OpenMenu(Button chip, bool pointer = false)
        {
            if (pointer) Click(chip, button: MouseButton.Right);
            else
            {
                Assert.True(chip.Focus(NavigationMethod.Tab));
                Key(PhysicalKey.F10, RawInputModifiers.Shift);
            }
            ContextMenu menu = Assert.IsType<ContextMenu>(chip.ContextMenu);
            Assert.True(menu.IsOpen);
            return menu;
        }

        internal void UseMenu(Button chip, string id)
        {
            BooruResultActionsTests.Click(BooruResultActionsTests.Item(OpenMenu(chip), id));
            Settle();
        }

        internal void Settle()
        {
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
        }

        public void Dispose()
        {
            Window.Close();
            _page?.Dispose();
            Settle();
        }
    }
}
