using MeowChan.Persistence;
using MeowChan.Rules;

namespace MeowChan.Tests;
public sealed class RuleTemplateStoreTests
{
    [Fact]
    public async Task MissingCatalogIsEmptyAndDoesNotWriteRulesOrExamples()
    {
        using TempDirectory directory = new(); using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        RuleTemplateStore store = new(directory.Path, lease);
        Assert.Throws<InvalidOperationException>(() => store.Current);
        var loaded = await store.LoadAsync();
        Assert.Empty(loaded.Catalog.Templates); Assert.Null(loaded.Warning);
        Assert.False(File.Exists(Path.Combine(directory.Path, "rule_templates.json")));
        Assert.False(File.Exists(Path.Combine(directory.Path, "media_rules.json")));
    }
    [Fact]
    public async Task RoundTripKeepsGroupPresenceEmptySelectionAndExplicitParameterValues()
    {
        using TempDirectory directory = new(); using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        RuleTemplateStore store = new(directory.Path, lease); await store.LoadAsync();
        Guid id = Guid.NewGuid();
        var value = new RuleTemplate(id, "  No media  ", new(MediaTypeSelectionMode.Selected), imageFilter: new(MediaImageFilterMode.Plugin,
            "missing.plugin", "filter", "12", new Dictionary<string,string> { ["flag"] = "false", ["count"] = "0", ["text"] = "" }));
        var saved = await store.SaveAsync(new[] { value }, store.Current.Revision);
        var loaded = await new RuleTemplateStore(directory.Path, lease).LoadAsync();
        Assert.Equal(saved.Revision, loaded.Catalog.Revision);
        var copy = Assert.Single(loaded.Catalog.Templates); Assert.Equal(id, copy.Id); Assert.Equal("No media", copy.Name);
        Assert.Null(copy.SizeLimit); Assert.Equal(MediaTypeSelectionMode.Selected, copy.Types!.Mode); Assert.Empty(copy.Types.Extensions);
        Assert.Equal("false", copy.ImageFilter!.Parameters["flag"]); Assert.Equal("0", copy.ImageFilter.Parameters["count"]);
        Assert.Equal("", copy.ImageFilter.Parameters["text"]); Assert.False(copy.ImageFilter.Parameters.ContainsKey("unset"));
        Assert.False(File.Exists(Path.Combine(directory.Path, "media_rules.json")));
    }
    [Fact]
    public async Task StaleMemoryAndExternalDiskChangesNeverOverwrite()
    {
        using TempDirectory directory = new(); using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        RuleTemplateStore store = new(directory.Path, lease); await store.LoadAsync();
        string baseline = store.Current.Revision;
        var value = new RuleTemplate(Guid.NewGuid(), "All", MediaTypeSelection.All);
        var saved = await store.SaveAsync(new[] { value }, baseline);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(Array.Empty<RuleTemplate>(), baseline));
        RuleTemplateStore other = new(directory.Path, lease); await other.LoadAsync();
        await other.SaveAsync(Array.Empty<RuleTemplate>(), other.Current.Revision);
        string disk = File.ReadAllText(Path.Combine(directory.Path, "rule_templates.json"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(new[] { value }, saved.Revision));
        Assert.Equal(disk, File.ReadAllText(Path.Combine(directory.Path, "rule_templates.json")));
        Assert.Same(saved, store.Current);
    }
    [Fact]
    public async Task FuturePrimaryBlocksOlderBackupAndPreservesEveryByte()
    {
        using TempDirectory directory = new(); using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        RuleTemplateStore store = new(directory.Path, lease); await store.LoadAsync();
        await store.SaveAsync(new[] { new RuleTemplate(Guid.NewGuid(), "All", MediaTypeSelection.All) }, store.Current.Revision);
        await store.SaveAsync(Array.Empty<RuleTemplate>(), store.Current.Revision);
        string path = Path.Combine(directory.Path, "rule_templates.json"); string backup = File.ReadAllText(path + ".bak");
        const string future = "{\"schemaVersion\":2,\"newShape\":true}"; File.WriteAllText(path, future);
        await Assert.ThrowsAsync<NotSupportedException>(() => new RuleTemplateStore(directory.Path, lease).LoadAsync());
        await Assert.ThrowsAsync<NotSupportedException>(() => store.SaveAsync(Array.Empty<RuleTemplate>(), store.Current.Revision));
        Assert.Equal(future, File.ReadAllText(path)); Assert.Equal(backup, File.ReadAllText(path + ".bak"));
    }
    [Fact]
    public async Task FutureBackupBesideValidPrimaryBlocksSaveAndPreservesCurrent()
    {
        using TempDirectory directory = new(); using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        RuleTemplateStore store = new(directory.Path, lease); await store.LoadAsync();
        var saved = await store.SaveAsync(new[] { new RuleTemplate(Guid.NewGuid(), "known template", MediaTypeSelection.All) }, store.Current.Revision);
        string path = Path.Combine(directory.Path, "rule_templates.json");
        byte[] primary = File.ReadAllBytes(path);
        const string future = "{\"schemaVersion\":99,\"foreign\":true}";
        File.WriteAllText(path + ".bak", future);
        await Assert.ThrowsAsync<NotSupportedException>(() => store.SaveAsync(Array.Empty<RuleTemplate>(), saved.Revision));
        Assert.Same(saved, store.Current);
        Assert.Equal(primary, File.ReadAllBytes(path));
        Assert.Equal(future, File.ReadAllText(path + ".bak"));
    }

    [Fact]
    public async Task MalformedUtf8NameIsNotLoadedOrRewrittenWithReplacementCharacters()
    {
        using TempDirectory directory = new(); using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        RuleTemplateStore store = new(directory.Path, lease); await store.LoadAsync();
        var saved = await store.SaveAsync(new[] { new RuleTemplate(Guid.NewGuid(), "encoded-name", MediaTypeSelection.All) }, store.Current.Revision);
        string path = Path.Combine(directory.Path, "rule_templates.json");
        byte[] corrupt = File.ReadAllBytes(path);
        int offset = System.Text.Encoding.UTF8.GetString(corrupt).IndexOf("encoded-name", StringComparison.Ordinal);
        Assert.True(offset >= 0); corrupt[offset] = 0xFF; File.WriteAllBytes(path, corrupt);
        await Assert.ThrowsAsync<InvalidDataException>(() => new RuleTemplateStore(directory.Path, lease).LoadAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(Array.Empty<RuleTemplate>(), saved.Revision));
        Assert.Equal(corrupt, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".bak"));
        Assert.Same(saved, store.Current);
    }

    [Fact]
    public async Task CorruptPrimaryRecoversWithWarningAndWriteFailureKeepsCurrent()
    {
        using TempDirectory directory = new(); using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        RuleTemplateStore store = new(directory.Path, lease); await store.LoadAsync();
        var saved = await store.SaveAsync(new[] { new RuleTemplate(Guid.NewGuid(), "All", MediaTypeSelection.All) }, store.Current.Revision);
        await store.SaveAsync(Array.Empty<RuleTemplate>(), store.Current.Revision);
        string path = Path.Combine(directory.Path, "rule_templates.json"); File.WriteAllText(path, "broken");
        var recovered = await store.LoadAsync(); Assert.True(recovered.Recovered); Assert.NotNull(recovered.Warning); Assert.Equal(saved.Revision, recovered.Catalog.Revision);
        Assert.Equal("broken", File.ReadAllText(path));
        File.Delete(path + ".bak"); Directory.CreateDirectory(path + ".bak");
        Exception? failure = await Record.ExceptionAsync(() => store.SaveAsync(Array.Empty<RuleTemplate>(), store.Current.Revision));
        Assert.True(failure is IOException or UnauthorizedAccessException); Assert.Equal(saved.Revision, store.Current.Revision);
    }
    [Fact]
    public void NamesAndPresenceAreValidatedWithoutConflatingInheritAndAbsent()
    {
        Assert.Throws<ArgumentException>(() => new RuleTemplate(Guid.NewGuid(), "empty"));
        Assert.Throws<ArgumentException>(() => new RuleTemplate(Guid.Empty, "id", MediaTypeSelection.All));
        Assert.Throws<ArgumentException>(() => new RuleTemplate(Guid.NewGuid(), "bad\nname", MediaTypeSelection.All));
        var inherit = new RuleTemplate(Guid.NewGuid(), "A", MediaTypeSelection.Inherit);
        var size = new RuleTemplate(Guid.NewGuid(), "a", sizeLimit: MediaSizeLimit.Unlimited);
        Assert.Null(inherit.SizeLimit); Assert.Equal(MediaTypeSelectionMode.Inherit, inherit.Types!.Mode);
        Assert.Throws<ArgumentException>(() => new RuleTemplateSet("r", new[] { inherit, size }));
    }
}
