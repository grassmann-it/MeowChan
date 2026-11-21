using Avalonia.Input;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using Xunit;

namespace MeowChan.DesktopTests;

public sealed class MediaLabelKeyTests
{
    private static KeyEventArgs Press(Key key) => new() { Key = key, KeyModifiers = KeyModifiers.None };

    [Theory]
    [InlineData("media.label.red", Key.D6)] [InlineData("media.label.yellow", Key.D7)]
    [InlineData("media.label.green", Key.D8)] [InlineData("media.label.blue", Key.D9)]
    public void DigitsSixToNineSetTheLabelsWithoutConflicts(string id, Key key)
    {
        var bindings = EffectiveShortcutBindings.Build(platform: ShortcutPlatform.Linux);
        Assert.True(bindings.Matches(Press(key), id));
        Assert.Empty(bindings.Issues.Where(issue => issue.CommandId == id));
        Assert.Contains(id, EffectiveShortcutBindings.YieldingDefaultCommandIds);
        Assert.Empty(bindings.For("media.label.clear"));
    }

    [Fact]
    public void ADigitSavedForAnotherCommandKeepsItAndTheLabelDefaultYields()
    {
        var section = new ShortcutBindingsSection().Set("gallery.fit", [ShortcutGesture.Parse("6")]);
        var bindings = EffectiveShortcutBindings.Build(section, ShortcutPlatform.Linux);
        Assert.Equal("6", bindings.Display("gallery.fit"));
        // The default stays on the Media page and gives way only in the gallery, where the saved binding runs.
        Assert.Equal(new[] { "gallery" }, bindings.YieldedContexts("media.label.red", ShortcutGesture.Parse("6")));
    }

    [Fact]
    public void BareDigitsRateAndShiftDigitsStillDo()
    {
        var bindings = EffectiveShortcutBindings.Build(platform: ShortcutPlatform.Linux);
        Assert.True(bindings.Matches(Press(Key.D3), "media.rating.set-3"));
        Assert.True(bindings.Matches(Press(Key.D0), "media.rating.clear"));
        Assert.True(bindings.Matches(new() { Key = Key.D3, KeyModifiers = KeyModifiers.Shift }, "media.rating.set-3"));
        Assert.Empty(bindings.Issues.Where(issue => issue.BlocksSave || issue.Reason?.Code == "conflictingActions"));
    }

    [Fact]
    public void TheBadgeAndTheSentenceNameTheColour()
    {
        Texts.Initialize(); Texts.SetLanguage("en");
        MediaBadgeState state = new(Label: "green");
        Assert.Equal("Green label", MediaBadgeText.Describe(state));
        Assert.Contains(MediaBadgeModel.Resolve(state, MediaBadgeOptions.Default), badge => badge.Id == MediaBadgeId.Label);
        Texts.SetLanguage("de");
        Assert.Equal("Grünes Label", MediaBadgeText.Describe(state));
        Texts.SetLanguage("en");
        Assert.Empty(MediaBadgeModel.Resolve(new MediaBadgeState(Label: "purple"), MediaBadgeOptions.Default));
    }
}
