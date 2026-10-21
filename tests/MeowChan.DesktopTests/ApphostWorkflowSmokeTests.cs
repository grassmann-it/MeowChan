using System.Net;
using System.Text.Json;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

public sealed class ApphostWorkflowSmokeTests
{
    [Fact]
    public void WorkflowNeedsExplicitOwnedDescriptorAndDoesNotCreateItsNewProfileDuringParse()
    {
        using OwnedRoot owned = new();
        string descriptor = owned.Descriptor();
        ApphostStartupSmoke smoke = ApphostStartupSmoke.Parse([ApphostWorkflowSmoke.Switch, descriptor], owned.Profile, null)!;
        Assert.Equal("download", smoke.Workflow!.Phase);
        Assert.False(Directory.Exists(owned.Profile));
        Assert.Equal(1, smoke.Complete(0)); // A descriptor never counts as an executed workflow.
    }

    [Fact]
    public void WrongTokenExternalProfileAndExistingProfileAreRejectedWithoutMutation()
    {
        using OwnedRoot owned = new();
        string descriptor = owned.Descriptor();
        Assert.Throws<ArgumentException>(() => ApphostStartupSmoke.Parse([ApphostWorkflowSmoke.Switch, descriptor],
            Path.Combine(Path.GetTempPath(), "unowned-profile"), null));
        File.WriteAllText(Path.Combine(owned.Root, ".workflow-owner"), new string('0', 32));
        Assert.Throws<ArgumentException>(() => ApphostStartupSmoke.Parse([ApphostWorkflowSmoke.Switch, descriptor], owned.Profile, null));
        Assert.False(Directory.Exists(owned.Profile));
        File.WriteAllText(Path.Combine(owned.Root, ".workflow-owner"), owned.Token);
        Directory.CreateDirectory(owned.Profile);
        File.WriteAllText(Path.Combine(owned.Profile, "sentinel"), "keep");
        Assert.Throws<ArgumentException>(() => ApphostStartupSmoke.Parse([ApphostWorkflowSmoke.Switch, descriptor], owned.Profile, null));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(owned.Profile, "sentinel")));
    }

    [Fact]
    public void DescriptorAndRestoreInputsAreBoundedAndSuccessfulPreviousPhaseIsRequired()
    {
        using OwnedRoot owned = new();
        string descriptor = owned.Descriptor();
        File.WriteAllText(descriptor, new string(' ', 16385));
        Assert.Throws<ArgumentException>(() => ApphostStartupSmoke.Parse([ApphostWorkflowSmoke.Switch, descriptor], owned.Profile, null));
        string previous = Path.Combine(owned.Root, "profile-download");
        Directory.CreateDirectory(previous);
        File.WriteAllText(Path.Combine(previous, ".workflow-profile"), owned.Token);
        File.WriteAllText(Path.Combine(previous, "apphost-workflow.json"), "{\"completed\":false,\"phase\":\"download\",\"failure\":null}");
        descriptor = owned.Descriptor("restore", previous);
        string profile = Path.Combine(owned.Root, "profile-restore");
        Assert.Throws<ArgumentException>(() => ApphostStartupSmoke.Parse([ApphostWorkflowSmoke.Switch, descriptor], profile, null));
        File.WriteAllText(Path.Combine(previous, "apphost-workflow.json"), "{\"completed\":true,\"phase\":\"download\",\"failure\":null}");
        Assert.Equal("restore", ApphostStartupSmoke.Parse([ApphostWorkflowSmoke.Switch, descriptor], profile, null)!.Workflow!.Phase);
        Assert.False(Directory.Exists(profile));
    }

    [Fact]
    public void RestoreTreeRejectsLinksWithoutFollowingThem()
    {
        if (OperatingSystem.IsWindows()) return; // Link creation needs a privilege on some Windows runners.
        using OwnedRoot owned = new();
        string profile = Path.Combine(owned.Root, "prior");
        Directory.CreateDirectory(profile);
        Directory.CreateSymbolicLink(Path.Combine(profile, "escape"), Path.GetTempPath());
        Assert.Throws<ArgumentException>(() => ApphostWorkflowSmoke.ValidateProfileTree(profile));
        Directory.Delete(Path.Combine(profile, "escape"));
    }

    [Fact]
    public void SystemTemporaryRootCanonicalizationResolvesAnAncestorAlias()
    {
        if (OperatingSystem.IsWindows()) return;
        using OwnedRoot owned = new();
        string real = Path.Combine(owned.Root, "real");
        Directory.CreateDirectory(Path.Combine(real, "child"));
        string alias = Path.Combine(owned.Root, "alias");
        Directory.CreateSymbolicLink(alias, real);
        try
        {
            System.Reflection.MethodInfo canonical = typeof(ApphostWorkflowSmoke).GetMethod("CanonicalDirectory",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            string actual = (string)canonical.Invoke(null, [Path.Combine(alias, "child")])!;
            string expected = (string)canonical.Invoke(null, [Path.Combine(real, "child")])!;
            Assert.Equal(expected, actual);
        }
        finally { Directory.Delete(alias); }
    }

    [Fact]
    public async Task TerminalHttpFixtureCannotSendUnexpectedTrafficAndHonorsCancellation()
    {
        using ApphostWorkflowSmoke.Transport transport = new();
        using HttpClient client = new(transport);
        using HttpResponseMessage boards = await client.GetAsync("https://a.4cdn.org/boards.json");
        Assert.Equal(HttpStatusCode.OK, boards.StatusCode);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("https://example.invalid/not-fixture"));
        Assert.Equal(1, transport.UnexpectedRequests);
        using CancellationTokenSource cancellation = new();
        Task<HttpResponseMessage> pending = client.GetAsync("https://i.4cdn.org/g/777.png", cancellation.Token);
        Assert.Equal(1, transport.MediaRequests);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, transport.CancelledRequests);
        transport.AllowMedia();
        Assert.Equal(ApphostWorkflowSmoke.Payload, await client.GetByteArrayAsync("https://i.4cdn.org/g/777.png"));
    }

    private sealed class OwnedRoot : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "meowchan-workflow-" + Guid.NewGuid().ToString("N"));
        internal string Token { get; } = Guid.NewGuid().ToString("N");
        internal string Profile => Path.Combine(Root, "profile-download");
        internal OwnedRoot()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root, ".workflow-owner"), Token);
        }
        internal string Descriptor(string phase = "download", string? previous = null)
        {
            string file = Path.Combine(Root, phase + ".json");
            File.WriteAllText(file, JsonSerializer.Serialize(new
            { schemaVersion = 1, phase, root = Root, previousProfile = previous, expectedScale = 1, ownerToken = Token }));
            return file;
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
