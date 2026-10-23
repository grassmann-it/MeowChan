using System.Reflection;
using MeowChan.Desktop;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.DesktopTests;

public sealed class ReaderGroupsTests
{
    private static readonly ResourceId A = new("a", "b", "1"), B = new("a", "b", "2");
    private static ReaderGroupsSnapshot Initial => new(1, 0, [new(Guid.NewGuid(), "Group", [A, B])]);

    [Fact]
    public void DraftComparesNameMembershipOrderAndFullReversion()
    {
        ReaderGroupsDraft draft = new(Initial); Guid id = draft.Groups[0].Id;
        Assert.False(draft.IsDirty);
        draft.Rename(id, "changed"); Assert.True(draft.IsDirty);
        draft.Rename(id, "Group"); Assert.False(draft.IsDirty);
        draft.Move(id, 0, 1); Assert.True(draft.IsDirty);
        draft.Move(id, 1, -1); Assert.False(draft.IsDirty);
        draft.Add(id, [A, B]); Assert.False(draft.IsDirty);
        draft.Remove(id, B); Assert.True(draft.IsDirty);
        draft.Add(id, [B]); Assert.False(draft.IsDirty);
        draft.Create(); Assert.True(draft.IsDirty);
        draft.Discard(); Assert.False(draft.IsDirty); Assert.Single(draft.Groups);
        draft.Groups.Clear(); Assert.True(draft.IsDirty);
        draft.Discard(); Assert.Equal(new[] { A, B }, draft.Groups[0].Members);
    }

    [Fact]
    public async Task HostUsesExistingOwnerAndDoesNotMutateDownloadsAndProfilesRemainSeparate()
    {
        using TempDirectory directory = new(); using TempDirectory other = new();
        new Config { SaveUrlsOnExit = false, RememberReadingPosition = false }.SaveConfig(directory.Path);
        ReaderGroupsSnapshot saved;
        await using (DesktopSessionHost host = new(directory.Path, Array.Empty<IImageBoard>()))
        {
            await host.StartAsync();
            var before = host.GetView(); bool paused = host.IsPaused;
            saved = await host.SaveReaderGroupsAsync(Initial, 0);
            Assert.Equal(1, saved.Revision);
            Assert.Empty((await host.FindReaderGroupCandidatesAsync("", 50)).Items);
            var missing = await host.ResolveReaderGroupMembersAsync([A, B]);
            Assert.All(missing, item => Assert.False(item.HasLocalPosts));
            Assert.Equal(before.Threads, host.GetView().Threads); Assert.Equal(before.Boards, host.GetView().Boards);
            Assert.Equal(paused, host.IsPaused); Assert.Null(await host.ReadReadingPositionAsync());
            Assert.Equal(new[] { A, B }, (await host.LoadReaderGroupsAsync()).Snapshot!.Groups[0].Members);
        }
        await using DesktopSessionHost restarted = new(directory.Path, Array.Empty<IImageBoard>());
        await restarted.StartAsync(); Assert.Equal(saved.Revision, (await restarted.LoadReaderGroupsAsync()).Snapshot!.Revision);
        await using DesktopSessionHost independent = new(other.Path, Array.Empty<IImageBoard>());
        await independent.StartAsync(); Assert.Empty((await independent.LoadReaderGroupsAsync()).Snapshot!.Groups);
    }

    [Fact]
    public async Task QueuedWriteCannotOutliveStoppingProfileOwner()
    {
        using TempDirectory directory = new();
        await using DesktopSessionHost host = new(directory.Path, Array.Empty<IImageBoard>());
        await host.StartAsync();
        SemaphoreSlim commands = (SemaphoreSlim)typeof(DesktopSessionHost).GetField("_commands", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(host)!;
        await commands.WaitAsync();
        Task write = host.SaveReaderGroupsAsync(Initial, 0);
        Task stop = host.StopAsync();
        commands.Release();
        await Assert.ThrowsAnyAsync<Exception>(async () => await write);
        await stop;
        Assert.False(File.Exists(Path.Combine(directory.Path, "reader-groups.json")));
    }
}
