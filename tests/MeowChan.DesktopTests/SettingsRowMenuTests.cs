using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>The context menu of a settings row: reset to default and copy the id, and what each says when it cannot be done.</summary>
[Collection("Localization")]
public sealed class SettingsRowMenuTests : IDisposable
{
    public SettingsRowMenuTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static ContextMenuModel Model(SettingsRowMenu.ResetState state, string? id = "RecheckTimer", List<string>? log = null) =>
        SettingsRowMenu.Compose("Recheck timer", id, state, () => log?.Add("reset"), () => log?.Add("copy")).ToModel();

    [Fact]
    public void ADifferingValueCanBeResetAndTheIdCopied()
    {
        List<string> log = [];
        ContextMenuModel model = Model(SettingsRowMenu.ResetState.Resettable, log: log);
        Assert.Equal(new[] { "settings.reset-default", "settings.copy-id" }, model.Entries.Select(entry => entry.CommandId));
        Assert.Equal(new[] { "Reset to default", "Copy setting id" }, model.Entries.Select(entry => entry.Label));
        Assert.All(model.Entries, entry => { Assert.True(entry.Available); Assert.Null(entry.Reason); });
        Assert.All(model.Entries, entry => Assert.True(CommandCatalog.TryGet(entry.CommandId!, out _)));
        foreach (ContextMenuEntry entry in model.Entries) entry.Invoke!();
        Assert.Equal(new[] { "reset", "copy" }, log);
    }

    [Fact]
    public void AValueAtItsDefaultAndASettingWithoutOneSayWhyTheResetIsOff()
    {
        ContextMenuEntry atDefault = Model(SettingsRowMenu.ResetState.AtDefault).Entries.First();
        Assert.False(atDefault.Available);
        Assert.Equal("The setting already has its default value.", atDefault.Reason);
        ContextMenuEntry none = Model(SettingsRowMenu.ResetState.NoDefault).Entries.First();
        Assert.False(none.Available);
        Assert.Equal("No default is known for this setting.", none.Reason);
        // Copying the id does not depend on the default.
        Assert.True(Model(SettingsRowMenu.ResetState.NoDefault).Entries.Last().Available);
    }

    [Fact]
    public void ARowWithoutAnIdCannotCopyIt()
    {
        ContextMenuEntry copy = Model(SettingsRowMenu.ResetState.Resettable, id: null).Entries.Last();
        Assert.False(copy.Available);
        Assert.False(string.IsNullOrEmpty(copy.Reason));
    }
}
