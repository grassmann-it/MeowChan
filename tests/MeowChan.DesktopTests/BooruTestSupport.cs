using MeowChan.Desktop.Booru;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopTests;

internal static class BooruTestSupport
{
    internal static async Task WaitAsync(Func<bool> condition, string what, int milliseconds = 5000)
    {
        DateTime end = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (!condition())
        {
            if (DateTime.UtcNow > end) throw new TimeoutException("Timed out waiting for " + what);
            await Task.Delay(5);
        }
    }

    /// <summary>Lets the fire-and-forget follow-ups (previews, local states) of a displayed page finish.</summary>
    internal static Task SettleAsync(BooruSearchModel model) => WaitAsync(
        () => model.View.Rows.All(row => row.Preview != BooruPreviewState.Loading), "previews");

    internal static async Task SearchAsync(BooruSearchModel model, string input, BooruInputMode mode = BooruInputMode.Tags)
    {
        model.SetMode(mode);
        model.SetInput(input);
        await model.SubmitAsync();
    }
}

/// <summary>What a test needs from the shared integration, which this slice does not touch.</summary>
internal enum BooruSharedNeed
{
    /// <summary><c>BuiltInCatalog</c> concatenates <c>BooruUiCatalog</c> and the bundled packs carry the keys.</summary>
    UiCatalog,
    /// <summary><c>CommandCatalog</c> lists <c>booru.open</c>.</summary>
    OpenCommand
}

internal static class BooruSharedIntegration
{
    internal static string? MissingReason(BooruSharedNeed need) => need switch
    {
        BooruSharedNeed.UiCatalog when !MeowChan.Desktop.Localization.BuiltInCatalog.English.ContainsKey("booru.ui.title")
            => "Shared integration pending: BuiltInCatalog does not yet concatenate BooruUiCatalog.Entries.",
        BooruSharedNeed.OpenCommand when !MeowChan.Desktop.Commands.CommandCatalog.TryGet("booru.open", out _)
            => "Shared integration pending: CommandCatalog has no booru.open command.",
        BooruSharedNeed.OpenCommand when !MeowChan.Desktop.Localization.BuiltInCatalog.English.ContainsKey("booru.ui.command.open")
            => "Shared integration pending: BuiltInCatalog does not yet concatenate BooruUiCatalog.Entries.",
        _ => null
    };
}

/// <summary>A fact that runs as soon as the shared integration it depends on exists and is skipped, with the reason, before.</summary>
internal sealed class BooruSharedFactAttribute : FactAttribute
{
    public BooruSharedFactAttribute(BooruSharedNeed need = BooruSharedNeed.UiCatalog) => Skip = BooruSharedIntegration.MissingReason(need);
}

internal sealed class BooruSharedTheoryAttribute : TheoryAttribute
{
    public BooruSharedTheoryAttribute(BooruSharedNeed need = BooruSharedNeed.UiCatalog) => Skip = BooruSharedIntegration.MissingReason(need);
}
