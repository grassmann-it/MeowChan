using System.Reflection;
using System.Runtime.CompilerServices;
using MeowChan.Archive;

namespace MeowChan.DesktopTests;

// Exercise the real ArchivePage fields/transition, without its XAML constructor, a native
// window, a profile, a reader or destination/history I/O. Only the existing async scope
// entry and visibility boundary are supplied; permission and publication are production code.
public sealed class ArchiveExportLinkListHandoffTests
{
    [Fact]
    public async Task AllResultsPublishesLinkListOnlyAfterReturningToTheExactFrozenThread()
    {
        using PageState state = new();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> choice = state.Choose(() =>
        {
            state.AcceptSelection();
            state.Set("_exportLoading", true);
            return completion.Task;
        });

        Assert.False(choice.IsCompleted);
        Assert.Equal(ArchivePage.ExportFormat.Html, state.Page.CurrentExportFormat);
        Assert.Equal(ArchivePage.ExportScope.Selection, state.Page.CurrentExportScope);
        state.Set("_exportLoading", false);
        completion.SetResult();

        Assert.True(await choice);
        Assert.Equal(ArchivePage.ExportFormat.LinkList, state.Page.CurrentExportFormat);
        Assert.Same(state.Frozen, state.Page.LinkListReviewThread);
        Assert.NotEqual(state.FirstResult, state.Page.LinkListReviewThread);
    }

    [Theory]
    [InlineData("busy")]
    [InlineData("filename-search")]
    [InlineData("running")]
    [InlineData("picking")]
    [InlineData("url-review")]
    [InlineData("cleanup")]
    [InlineData("unavailable")]
    [InlineData("ended")]
    [InlineData("lifetime-cancelled")]
    [InlineData("no-reader")]
    [InlineData("closed")]
    public async Task BlockedChoiceNeitherStartsAScopeSwitchNorPublishesAFormat(string blocker)
    {
        using PageState state = new();
        state.Block(blocker);
        int switches = 0;
        Assert.False(await state.Choose(() => { switches++; return Task.CompletedTask; }));
        Assert.Equal(0, switches);
        Assert.Equal(ArchivePage.ExportFormat.Html, state.Page.CurrentExportFormat);
        Assert.Equal(ArchivePage.ExportScope.AllResults, state.Page.CurrentExportScope);
        Assert.Null(state.Page.LinkListReviewThread);
        Assert.Same(state.FirstResult, state.Get<ResourceId>("_exportThread"));
    }

    [Fact]
    public async Task EarlyReturnFromScopeEntryIsNotACompletedScopeSwitch()
    {
        using PageState state = new();
        int switches = 0;
        Assert.False(await state.Choose(() => { switches++; return Task.CompletedTask; }));
        Assert.Equal(1, switches);
        Assert.Equal(ArchivePage.ExportFormat.Html, state.Page.CurrentExportFormat);
        Assert.Equal(ArchivePage.ExportScope.AllResults, state.Page.CurrentExportScope);
        Assert.Null(state.Page.LinkListReviewThread);
    }

    [Theory]
    [InlineData("unchanged-request")]
    [InlineData("all-results")]
    [InlineData("wrong-thread")]
    [InlineData("wrong-source")]
    [InlineData("collection")]
    [InlineData("loading")]
    [InlineData("resolving")]
    public async Task ScopeCompletionMustRepresentTheMatchingReadySingleSelection(string mismatch)
    {
        using PageState state = new();
        Assert.False(await state.Choose(() =>
        {
            state.AcceptSelection();
            switch (mismatch)
            {
                case "unchanged-request": state.Set("_exportRequest", 40); break;
                case "all-results": state.Set("_exportAllResults", true); break;
                case "wrong-thread": state.Set("_exportThread", state.FirstResult); break;
                case "wrong-source": state.Set("_exportThread", state.Frozen with { Source = "8kun" }); break;
                case "collection": state.Set("_exportMembers", new[] { state.Frozen, state.FirstResult }); break;
                case "loading": state.Set("_exportLoading", true); break;
                case "resolving": state.Set("_exportResolving", true); break;
                default: throw new ArgumentOutOfRangeException(nameof(mismatch));
            }
            return Task.CompletedTask;
        }));
        Assert.Equal(ArchivePage.ExportFormat.Html, state.Page.CurrentExportFormat);
    }

    [Theory]
    [InlineData("busy")]
    [InlineData("filename-search")]
    [InlineData("running")]
    [InlineData("picking")]
    [InlineData("unavailable")]
    [InlineData("ended")]
    [InlineData("lifetime-cancelled")]
    [InlineData("closed")]
    [InlineData("new-request")]
    [InlineData("new-session")]
    public async Task LateCompletionCannotPublishIntoABlockedClosedOrReplacementRequest(string change)
    {
        using PageState state = new();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> choice = state.Choose(() => { state.AcceptSelection(); return completion.Task; });
        Assert.Equal(ArchivePage.ExportFormat.Html, state.Page.CurrentExportFormat);
        if (change == "new-request") state.Set("_exportRequest", 42);
        else if (change == "new-session") state.Set("_exportSessionMembers", new[] { state.Frozen });
        else state.Block(change);
        completion.SetResult();
        Assert.False(await choice);
        Assert.Equal(ArchivePage.ExportFormat.Html, state.Page.CurrentExportFormat);
    }

    [Fact]
    public async Task CancelledScopeTaskLeavesHtmlPublished()
    {
        using PageState state = new();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => state.Choose(() =>
        {
            state.AcceptSelection();
            return Task.FromCanceled(cancellation.Token);
        }));
        Assert.Equal(ArchivePage.ExportFormat.Html, state.Page.CurrentExportFormat);
    }

    [Fact]
    public async Task CancelledScopeLookupCannotPublishEvenIfTheRequestNumberHasNotChanged()
    {
        using PageState state = new();
        using CancellationTokenSource lookup = new();
        Assert.False(await state.Choose(() =>
        {
            state.AcceptSelection();
            state.Set("_exportLookupCancellation", lookup);
            lookup.Cancel();
            return Task.CompletedTask;
        }));
        Assert.Equal(ArchivePage.ExportFormat.Html, state.Page.CurrentExportFormat);
    }

    [Fact]
    public async Task ScopeEntryCannotRetargetTheOriginalFrozenArrayInPlace()
    {
        using PageState state = new();
        Assert.False(await state.Choose(() =>
        {
            state.AcceptSelection();
            state.Get<ResourceId[]>("_exportSessionMembers")[0] = state.FirstResult;
            state.Set("_exportThread", state.FirstResult);
            return Task.CompletedTask;
        }));
        Assert.Equal(ArchivePage.ExportFormat.Html, state.Page.CurrentExportFormat);
    }

    [Fact]
    public async Task ExistingSingleSelectionNeedsNoSwitchAndHandsOverTheFrozenObjectNotItsEqualDraftCopy()
    {
        using PageState state = new();
        state.Set("_exportAllResults", false);
        state.Set("_exportThread", state.Frozen with { });
        state.Set("_exportMembers", new[] { state.Frozen with { } });
        Assert.True(await state.Choose(() => throw new InvalidOperationException("No switch expected.")));
        Assert.Equal(ArchivePage.ExportFormat.LinkList, state.Page.CurrentExportFormat);
        Assert.Same(state.Frozen, state.Page.LinkListReviewThread);
    }

    [Theory]
    [MemberData(nameof(InvalidSessions))]
    public async Task MissingMultipleOrMalformedFrozenThreadsCannotBeChosenOrHandedOver(ResourceId[] session)
    {
        using PageState state = new();
        state.Set("_exportSessionMembers", session);
        state.Set("_exportAllResults", false);
        state.Set("_exportMembers", null);
        state.Set("_exportThread", session.FirstOrDefault());
        Assert.Null(state.Page.LinkListReviewThread);
        Assert.False(await state.Choose(() => throw new InvalidOperationException("No switch expected.")));
        Assert.Equal(ArchivePage.ExportFormat.Html, state.Page.CurrentExportFormat);
    }

    public static IEnumerable<object[]> InvalidSessions()
    {
        yield return new object[] { Array.Empty<ResourceId>() };
        yield return new object[] { new ResourceId[] { null! } };
        yield return new object[] { new[] { new ResourceId("4chan", "g", "123"), new ResourceId("4chan", "g", "456") } };
        yield return new object[] { new[] { new ResourceId("4chan", "g") } };
        yield return new object[] { new[] { new ResourceId("4chan", "", "123") } };
        yield return new object[] { new[] { new ResourceId("", "g", "123") } };
        yield return new object[] { new[] { new ResourceId("4chan", "g", " ") } };
    }

    [Theory]
    [InlineData("all-results")]
    [InlineData("wrong-thread")]
    [InlineData("collection")]
    public void ReviewTargetRejectsInconsistentDraftEvenIfTheFormatAlreadySaysLinkList(string mismatch)
    {
        using PageState state = new();
        state.AcceptSelection();
        state.Set("_exportFormat", ArchivePage.ExportFormat.LinkList);
        if (mismatch == "all-results") state.Set("_exportAllResults", true);
        else if (mismatch == "wrong-thread") state.Set("_exportThread", state.FirstResult);
        else state.Set("_exportMembers", new[] { state.Frozen, state.FirstResult });
        Assert.Null(state.Page.LinkListReviewThread);
    }

    private sealed class PageState : IDisposable
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly CancellationTokenSource _lifetime = new();
        internal ArchivePage Page { get; } = (ArchivePage)RuntimeHelpers.GetUninitializedObject(typeof(ArchivePage));
        internal ResourceId Frozen { get; } = new("4chan", "g", "123");
        internal ResourceId FirstResult { get; } = new("4chan", "g", "456");
        private bool _visible = true;

        internal PageState()
        {
            Set("_lifetime", _lifetime);
            Set("_available", true);
            Set("_openReader", (Func<CancellationToken, Task<ArchiveReader>>)(_ =>
                throw new InvalidOperationException("No archive I/O allowed in handoff tests.")));
            Set("_exportSessionMembers", new[] { Frozen });
            Set("_exportThread", FirstResult);
            Set("_exportMembers", new[] { FirstResult, Frozen });
            Set("_exportAllResults", true);
            Set("_exportRequest", 40);
        }

        internal Task<bool> Choose(Func<Task> switchScope) => Page.TryChooseLinkListAsync(switchScope, () => _visible);
        internal void AcceptSelection()
        {
            Set("_exportRequest", Get<int>("_exportRequest") + 1);
            Set("_exportAllResults", false);
            Set("_exportThread", Frozen with { });
            Set("_exportMembers", null);
        }

        internal void Block(string blocker)
        {
            switch (blocker)
            {
                case "busy": Set("_busy", true); break;
                case "filename-search": SetUninitialized("_filenameSearchOwner"); break;
                case "running": Set("_exportRunning", true); break;
                case "picking": Set("_exportPicking", true); break;
                case "url-review": Set("_urlTextExportBusy", true); break;
                case "cleanup": SetUninitialized("_cleanupDialog"); break;
                case "unavailable": Set("_available", false); break;
                case "ended": Set("_ended", true); break;
                case "lifetime-cancelled": _lifetime.Cancel(); break;
                case "no-reader": Set("_openReader", null); break;
                case "closed": _visible = false; break;
                default: throw new ArgumentOutOfRangeException(nameof(blocker));
            }
        }

        private static FieldInfo Field(string name) => typeof(ArchivePage).GetField(name, Fields)!;
        internal void Set(string name, object? value) => Field(name).SetValue(Page, value);
        internal T Get<T>(string name) => (T)Field(name).GetValue(Page)!;
        private void SetUninitialized(string name) => Set(name, RuntimeHelpers.GetUninitializedObject(Field(name).FieldType));
        public void Dispose() => _lifetime.Dispose();
    }
}
