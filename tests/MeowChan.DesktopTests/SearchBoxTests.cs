using System.ComponentModel;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>The shared search field: Escape semantics, clear button, names, debounce on a fake clock, Primary+F for Media.</summary>
[Collection("Localization")]
public sealed class SearchBoxTests : IDisposable
{
    public SearchBoxTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private sealed class ManualTime : TimeProvider
    {
        private long _now;
        private readonly List<ManualTimer> _timers = [];
        public override long GetTimestamp() => _now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ManualTimer timer = new(this, callback, state);
            _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
        internal void Advance(TimeSpan duration)
        {
            _now += duration.Ticks;
            foreach (ManualTimer timer in _timers.Where(t => t.Due is long due && due <= _now).ToArray()) { timer.Due = null; timer.Deliver(); }
        }
        // An OS callback that was already queued when the timer was replaced.
        internal Action StaleDelivery() => _timers[0].Deliver;
        private sealed class ManualTimer(ManualTime owner, TimerCallback callback, object? state) : ITimer
        {
            internal long? Due;
            public bool Change(TimeSpan dueTime, TimeSpan period) { Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime.Ticks; return true; }
            internal void Deliver() => callback(state);
            public void Dispose() => Due = null;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private static SearchBox Box(ManualTime? clock = null, int debounceMs = 0)
    {
        SearchBox box = new() { Debounce = TimeSpan.FromMilliseconds(debounceMs) };
        if (clock != null) { box.Clock = clock; box.Post = action => action(); }
        return box;
    }

    private static KeyEventArgs Press(Key key, KeyModifiers modifiers = KeyModifiers.None) =>
        new() { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers };

    [Fact]
    public void FirstEscapeClearsTheTextAndIsSwallowed()
    {
        SearchBox box = Box();
        int queries = 0, exits = 0;
        box.QueryChanged += (_, _) => queries++;
        box.ExitRequested += (_, _) => exits++;
        box.Text = "abc";
        queries = 0;
        KeyEventArgs escape = Press(Key.Escape);
        box.RaiseEvent(escape);
        Assert.Equal(string.Empty, box.Text ?? string.Empty);
        Assert.True(escape.Handled); // The window, dialog or surface around the field stays open.
        Assert.Equal(1, queries);
        Assert.Equal(0, exits);
    }

    [Fact]
    public void SecondEscapeLeavesTheFieldAndOnlyWhenAnOwnerClaimsItIsItSwallowed()
    {
        SearchBox box = Box();
        int exits = 0;
        box.ExitRequested += (_, _) => exits++;
        KeyEventArgs unclaimed = Press(Key.Escape);
        box.RaiseEvent(unclaimed);
        Assert.Equal(1, exits);
        Assert.False(unclaimed.Handled); // Goes on to the owner's own Escape handling (bar, surface, window).

        box.ExitRequested += (_, e) => e.Handled = true;
        KeyEventArgs claimed = Press(Key.Escape);
        box.RaiseEvent(claimed);
        Assert.True(claimed.Handled);
    }

    [Fact]
    public void ModifiedEscapeIsLeftAlone()
    {
        SearchBox box = Box();
        box.Text = "abc";
        KeyEventArgs shifted = Press(Key.Escape, KeyModifiers.Shift);
        box.RaiseEvent(shifted);
        Assert.Equal("abc", box.Text);
        Assert.False(shifted.Handled);
    }

    [Fact]
    public void OnlyAFieldWithTextOwnsTheEscape()
    {
        SearchBox box = Box();
        Assert.False(SearchBox.OwnsEscape(box));
        box.Text = "x";
        Assert.True(SearchBox.OwnsEscape(box));
        Assert.False(SearchBox.OwnsEscape(new TextBox { Text = "x" }));
        Assert.False(SearchBox.OwnsEscape(null));
    }

    [Fact]
    public void EnterIsNotHandledSoTheOwnerDecides()
    {
        SearchBox box = Box();
        box.Text = "abc";
        KeyEventArgs enter = Press(Key.Enter);
        box.RaiseEvent(enter);
        Assert.False(enter.Handled);
        Assert.Equal("abc", box.Text);
    }

    [Fact]
    public void ClearButtonShowsOnlyWithTextHasAnAccessibleNameAndClears()
    {
        SearchBox box = Box();
        Assert.False(box.ClearButton.IsVisible);
        box.Text = "abc";
        Assert.True(box.ClearButton.IsVisible);
        Assert.Equal("Clear input", AutomationProperties.GetName(box.ClearButton));
        Assert.Equal("Clear input (Esc)", ToolTip.GetTip(box.ClearButton));
        box.ClearButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(string.Empty, box.Text ?? string.Empty);
        Assert.False(box.ClearButton.IsVisible);

        Texts.SetLanguage("de");
        Assert.Equal("Eingabe löschen", AutomationProperties.GetName(Box().ClearButton));
    }

    [Fact]
    public void ThePlaceholderNamesTheFieldUnlessTheOwnerChoseAName()
    {
        SearchBox box = Box();
        box.PlaceholderText = "File name contains…";
        Assert.Equal("File name contains…", AutomationProperties.GetName(box));
        box.PlaceholderText = "Other";
        Assert.Equal("Other", AutomationProperties.GetName(box)); // Language change keeps following.

        SearchBox owned = Box();
        AutomationProperties.SetName(owned, "Own name");
        owned.PlaceholderText = "Hint";
        Assert.Equal("Own name", AutomationProperties.GetName(owned));
    }

    [Fact]
    public void WithoutDebounceEveryEditIsReportedAtOnce()
    {
        SearchBox box = Box();
        int queries = 0;
        box.QueryChanged += (_, _) => queries++;
        box.Text = "a"; box.Text = "ab";
        Assert.Equal(2, queries);
        Assert.False(box.IsQueryPending);
    }

    [Fact]
    public void DebounceReportsOnlyTheLastEditOfABurstAfterTheDelay()
    {
        ManualTime clock = new();
        SearchBox box = Box(clock, 200);
        List<string?> seen = [];
        box.QueryChanged += (_, _) => seen.Add(box.Text);
        box.Text = "t";
        clock.Advance(TimeSpan.FromMilliseconds(150));
        box.Text = "tw";
        clock.Advance(TimeSpan.FromMilliseconds(150)); // 300 ms since the first edit, 150 since the last
        box.Text = "two";
        Assert.Empty(seen);
        Assert.True(box.IsQueryPending);
        clock.Advance(TimeSpan.FromMilliseconds(199));
        Assert.Empty(seen);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(["two"], seen);
        Assert.False(box.IsQueryPending);
    }

    [Fact]
    public void ASupersededTimerCallbackThatWasAlreadyQueuedDoesNothing()
    {
        ManualTime clock = new();
        SearchBox box = Box(clock, 200);
        int queries = 0;
        box.QueryChanged += (_, _) => queries++;
        box.Text = "a";
        Action stale = clock.StaleDelivery();
        box.Text = "ab";
        stale();
        Assert.Equal(0, queries);
        clock.Advance(TimeSpan.FromMilliseconds(200));
        Assert.Equal(1, queries);
    }

    [Fact]
    public void ClearingAppliesAtOnceAndDropsTheWaitingQuery()
    {
        ManualTime clock = new();
        SearchBox box = Box(clock, 200);
        List<string> seen = [];
        box.QueryChanged += (_, _) => seen.Add(box.Text ?? string.Empty);
        box.Text = "abc";
        box.RaiseEvent(Press(Key.Escape));
        Assert.Equal([""], seen); // Not "abc", not delayed.
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Single(seen);
    }

    [Fact]
    public void SilentTextChangeForAnOwnersResetCancelsAndReportsNothing()
    {
        ManualTime clock = new();
        SearchBox box = Box(clock, 200);
        int queries = 0;
        box.QueryChanged += (_, _) => queries++;
        box.Text = "abc";
        box.SetTextSilently(string.Empty);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(0, queries);
        Assert.False(box.IsQueryPending);
        Assert.Equal(string.Empty, box.Text ?? string.Empty);
    }

    [Fact]
    public void AResultCountHidesWhileItsQueryIsStillWaiting()
    {
        ManualTime clock = new();
        SearchBox box = Box(clock, 200);
        box.ResultText = "12 results";
        box.Text = "a";
        TextBlock count = ((StackPanel)box.InnerRightContent!).Children.OfType<TextBlock>().First();
        Assert.False(count.IsVisible);
        clock.Advance(TimeSpan.FromMilliseconds(200));
        Assert.True(count.IsVisible);
        Assert.Equal("12 results", count.Text);
    }

    [Fact]
    public void MediaSearchHasPrimaryFInItsOwnContextWithoutCollisions()
    {
        CommandDefinition command = CommandCatalog.Get("input.focus-media-search");
        Assert.Equal("Primary+F", command.Shortcut);
        Assert.Equal(CommandScope.Media, command.Scope);
        Assert.Equal(["media"], command.ExecutionContexts);
        Assert.Equal("Media · file name filter", CommandCatalog.ScopeHint("input.focus-media-search"));
        foreach (string other in new[] { "input.focus-search", "input.focus-download-search", "reader.find" })
            Assert.False(EffectiveShortcutBindings.ContextsOverlap("input.focus-media-search", other), other);
        Assert.Empty(EffectiveShortcutBindings.Build(platform: ShortcutPlatform.Linux).Issues);
        Assert.Empty(EffectiveShortcutBindings.Build(platform: ShortcutPlatform.MacOS).Issues);
    }

    [Fact]
    public void MediaSearchFollowsARebindingAndCollidesWithCommandsOfTheMediaPage()
    {
        var section = new ShortcutBindingsSection().Set("input.focus-media-search", new[] { ShortcutGesture.Parse("Primary+K") });
        var bindings = EffectiveShortcutBindings.Build(section, ShortcutPlatform.Linux);
        Assert.Empty(bindings.Issues);
        Assert.True(bindings.Matches(new KeyEventArgs { Key = Key.K, KeyModifiers = KeyModifiers.Control }, "input.focus-media-search"));
        Assert.False(bindings.Matches(new KeyEventArgs { Key = Key.F, KeyModifiers = KeyModifiers.Control }, "input.focus-media-search"));

        // A saved binding of another Media command on the same gesture wins: the new default yields instead of
        // disabling the user's binding.
        var gesture = new[] { ShortcutGesture.Parse("Primary+F") };
        var saved = EffectiveShortcutBindings.Build(new ShortcutBindingsSection().Set("media.rating.edit", gesture), ShortcutPlatform.Linux);
        Assert.DoesNotContain(saved.Issues, issue => issue.BlocksSave);
        Assert.True(saved.Matches(new KeyEventArgs { Key = Key.F, KeyModifiers = KeyModifiers.Control }, "media.rating.edit"));
        Assert.False(saved.Matches(new KeyEventArgs { Key = Key.F, KeyModifiers = KeyModifiers.Control }, "input.focus-media-search"));
    }

    [Fact]
    public void TextsExistInEveryLanguageWithoutDuplicates()
    {
        foreach (string code in new[] { "en", "de", "es", "fr", "ru" })
        {
            Texts.SetLanguage(code);
            foreach (string key in new[] { "search.clear", "search.count", "command.input.focus-media-search", "command.scope.input.focus-media-search" })
                Assert.NotEqual(key, Texts.T(key, 3));
            Assert.Contains("3", Texts.T("search.count", 3));
        }
    }
}
