using System.Reflection;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class CtsOwnerDisposalTests
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;

    // A Window reads Application.ActualThemeVariant in its constructor, and every AvaloniaObject verifies the dispatcher it captured when it was
    // created. TopLevel resolves that host through the locator (IThemeVariantHost / IGlobalStyles), not through Application.Current, and another
    // class of this assembly may already have set the process-wide platform up on its own worker thread (AvaloniaTestPlatform.EnterSkia is a
    // one-shot AppBuilder.Setup). Binding a fresh TokenApplication only as Application left those two entries pointing at that foreign instance,
    // so the dialog's window verified a dispatcher this thread does not own. The scope now also rebinds the two interfaces TopLevel asks for.
    private static void OnDispatcher(Action test) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = AvaloniaLocator.EnterScope();
        // The test dispatcher deliberately has no windowing subsystem. Bind only a scoped headless window platform; do not start another
        // Application/dispatcher or show a native window. Pinned to the test package's version.
        typeof(AvaloniaHeadlessPlatform).GetMethod("Initialize", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }]);
        TokenApplication app = TokenApplication.Bind();
        AvaloniaLocator.CurrentMutable.Bind<IThemeVariantHost>().ToConstant(app);
        AvaloniaLocator.CurrentMutable.Bind<IGlobalStyles>().ToConstant(app);
        test();
    });

    private static ArchiveTransferExportDialog Dialog(CancellationToken parent = default,
        Func<ArchiveTransferExportRequest, CancellationToken, Task<ArchiveTransferExportCount>>? count = null) =>
        new(count ?? ((_, _) => Task.FromResult(new ArchiveTransferExportCount(1, 1, 0, false, 0))),
            (_, _, _, _) => Task.FromResult(new ArchiveTransferExportResult(ArchiveTransferExportError.None)),
            [], _ => Task.FromResult<string?>(null), null, lifetime: parent);

    [Fact]
    public void FiftyCompletedExportRecountsReturnRegistrationsToBaseline() => OnDispatcher(() =>
    {
        ArchiveTransferExportDialog dialog = Dialog();
        var lifetime = (CancellationTokenSource)typeof(ArchiveTransferExportDialog).GetField("_lifetime", Fields)!.GetValue(dialog)!;
        try
        {
            Assert.True(dialog.Completion.IsCompletedSuccessfully);
            for (int i = 0; i < 50; i++)
                Assert.True(dialog.RecountForTestsAsync().IsCompletedSuccessfully);
            Assert.Equal(0, CtsDisposalTests.RegistrationCount(lifetime));
        }
        finally { dialog.Terminate(); }
    });

    [Fact]
    public void FiftyClosedExportDialogsReturnParentRegistrationsToBaseline() => OnDispatcher(() =>
    {
        using CancellationTokenSource parent = new();
        int baseline = CtsDisposalTests.RegistrationCount(parent);
        for (int i = 0; i < 50; i++)
        {
            ArchiveTransferExportDialog dialog = Dialog(parent.Token);
            dialog.Terminate();
            dialog.Terminate(); // Terminal cleanup must be idempotent.
        }
        Assert.Equal(baseline, CtsDisposalTests.RegistrationCount(parent));
    });

    [Fact]
    public void FiftyCompletedExportsReleaseEachOperation() => OnDispatcher(() =>
    {
        using ArchiveTransferExportDialog dialog = Dialog();
        var lifetime = (CancellationTokenSource)typeof(ArchiveTransferExportDialog).GetField("_lifetime", Fields)!.GetValue(dialog)!;
        for (int i = 0; i < 50; i++)
        {
            typeof(ArchiveTransferExportDialog).GetField("_target", Fields)!.SetValue(dialog, "unused-by-fake-export");
            Assert.True(dialog.StartForTestsAsync().IsCompletedSuccessfully);
            Assert.NotNull(dialog.LastResult);
            Assert.Equal(ArchiveTransferExportError.None, dialog.LastResult.Error);
        }
        Assert.Equal(0, CtsDisposalTests.RegistrationCount(lifetime));
    });

    [Fact]
    public void SupersededCountsWaitingForNonCooperativeIoDisposeOnTheirEarlyReturn() => OnDispatcher(() =>
    {
        TaskCompletionSource<ArchiveTransferExportCount> pending = new();
        using ArchiveTransferExportDialog dialog = Dialog(count: (_, _) => pending.Task);
        CancellationTokenSource Current() => (CancellationTokenSource)((Action)typeof(ArchiveTransferExportDialog).GetField("_cancelOperation", Fields)!.GetValue(dialog)!).Target!;
        CancellationTokenSource first = Current();
        Task second = dialog.RecountForTestsAsync();
        CancellationTokenSource middle = Current();
        Task third = dialog.RecountForTestsAsync();
        CancellationTokenSource last = Current();
        Assert.False(second.IsCompleted);
        Assert.False(third.IsCompleted);
        dialog.Dispose();
        Assert.True(first.Token.WaitHandle.WaitOne(0));
        pending.SetResult(new(1, 1, 0, false, 0));
        Dispatcher.UIThread.RunJobs();
        Assert.True(second.IsCompletedSuccessfully);
        Assert.True(third.IsCompletedSuccessfully);
        Assert.Throws<ObjectDisposedException>(() => first.Token);
        Assert.Throws<ObjectDisposedException>(() => middle.Token);
        Assert.Throws<ObjectDisposedException>(() => last.Token);
    });

    [Fact]
    public void ClosingExportDoesNotDisposeTheTokenOfANonCooperativeCount() => OnDispatcher(() =>
    {
        using CancellationTokenSource parent = new();
        TaskCompletionSource<ArchiveTransferExportCount> pending = new();
        CancellationToken token = default;
        ArchiveTransferExportDialog dialog = Dialog(parent.Token, (_, value) => { token = value; return pending.Task; });
        var operation = (CancellationTokenSource)((Action)typeof(ArchiveTransferExportDialog).GetField("_cancelOperation", Fields)!.GetValue(dialog)!).Target!;
        dialog.Terminate();
        Assert.True(token.IsCancellationRequested);
        Assert.True(token.WaitHandle.WaitOne(0));
        Assert.False(dialog.Completion.IsCompleted);
        pending.SetResult(new(1, 1, 0, false, 0));
        Dispatcher.UIThread.RunJobs();
        Assert.True(dialog.Completion.IsCompletedSuccessfully);
        Assert.Throws<ObjectDisposedException>(() => operation.Token);
        Assert.Equal(0, CtsDisposalTests.RegistrationCount(parent));
    });
}
