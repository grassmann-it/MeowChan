using System.Runtime.CompilerServices;
using System.Reflection;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class DownloadSafetyCapExtraRegressionTests : IDisposable
{
    public DownloadSafetyCapExtraRegressionTests()
    {
        Texts.Initialize();
        Texts.SetLanguage("en");
    }

    public void Dispose() => Texts.SetLanguage("en");

    public static TheoryData<decimal?> RawDrafts => new()
    {
        null,
        0m,
        -1m,
        0.5m,
        32.5m,
        257m,
        1m,
        32m,
        256m,
    };

    [Theory]
    [MemberData(nameof(RawDrafts))]
    public void RawSafetyDraftIsNeverClampedAndOnlyWholeValuesWithinOneAnd256Convert(decimal? raw)
    {
        var editor = (SettingsEditor)RuntimeHelpers.GetUninitializedObject(typeof(SettingsEditor));
        editor.ParallelDownloadsSafetyLimit = raw;

        MethodInfo converter = typeof(SettingsEditor).GetMethod("GetInteger",
            BindingFlags.Static | BindingFlags.NonPublic,
            binder: null,
            new[] { typeof(decimal?), typeof(string), typeof(int), typeof(int) },
            modifiers: null)!;
        bool accepted = raw is >= 1 and <= 256 && decimal.Truncate(raw.Value) == raw;

        if (accepted)
        {
            object? result = converter.Invoke(null, [raw, editor.ParallelSafetyLabel, 1, Downloader.DownloadQueue.MaximumParallelDownloadsSafetyLimit]);
            Assert.Equal((int)raw!.Value, result);
        }
        else
        {
            TargetInvocationException failure = Assert.Throws<TargetInvocationException>(() =>
                converter.Invoke(null, [raw, editor.ParallelSafetyLabel, 1, Downloader.DownloadQueue.MaximumParallelDownloadsSafetyLimit]));
            Assert.IsType<ArgumentException>(failure.InnerException);
        }

        // The draft itself must remain rejected input rather than being replaced with
        // a previous valid value during conversion (for example 32).
        Assert.Equal(raw, editor.ParallelDownloadsSafetyLimit);
    }
}
