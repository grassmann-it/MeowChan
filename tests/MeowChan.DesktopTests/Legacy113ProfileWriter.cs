using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json.Nodes;

namespace MeowChan.DesktopTests;

/// <summary>Runs the released managed writer in an isolated load context with the test runtime.</summary>
internal sealed class Legacy113ProfileWriter : AssemblyLoadContext, IDisposable
{
    internal static string DirectoryPath => Environment.GetEnvironmentVariable("MEOWCHAN_113_WRITER")
        ?? Path.Combine(Path.GetTempPath(), "meowchan-1.1.3-linux-x64-writer");

    private readonly Assembly _desktop;
    private readonly Assembly _library;
    private readonly Type _config;
    private readonly Type _lease;

    internal Legacy113ProfileWriter() : base(isCollectible: true)
    {
        _desktop = LoadFromAssemblyPath(Path.Combine(DirectoryPath, "MeowChan.dll"));
        _library = LoadFromAssemblyPath(Path.Combine(DirectoryPath, "libmeowchan.dll"));
        _config = _desktop.GetType("MeowChan.Config", throwOnError: true)!;
        _lease = _library.GetType("MeowChan.Persistence.ProfileLease", throwOnError: true)!;
        Assert.Equal(new Version(1, 1, 3, 0), _desktop.GetName().Version);
    }

    protected override Assembly? Load(AssemblyName name)
    {
        string path = Path.Combine(DirectoryPath, name.Name + ".dll");
        return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
    }

    internal async Task WriteAsync(string profile, string downloads, string originalName, string? previous = null, string? originalDirectory = null)
    {
        using var lease = (IDisposable)Call(_lease, null, "Acquire", profile)!;
        object settings = Call(_config, null, "LoadConfig", profile, lease)!;
        Set(settings, "DownloadPath", downloads);
        Set(settings, "Language", "en");
        Set(settings, "AutomaticUpdateChecks", false);
        Call(_config, settings, "SaveConfig", profile);

        Type sessionType = _library.GetType("MeowChan.Persistence.SessionStore", true)!;
        object store = Activator.CreateInstance(sessionType, profile, lease)!;
        object session = Activator.CreateInstance(_library.GetType("MeowChan.Persistence.SessionSnapshot", true)!)!;
        Dictionary<string, string> directories = new()
        {
            ["https://boards.4chan.org/g/thread/123"] = downloads
        };
        if (previous is not null) directories.Add("https://boards.4chan.org/g/thread/456", previous);
        Set(session, "DownloadDirectories", directories);
        await (Task)Call(sessionType, store, "SaveAsync", session, CancellationToken.None)!;

        Type archiveType = _library.GetType("MeowChan.Archive.ArchiveCatalog", true)!;
        Task archive = (Task)Call(archiveType, null, "OpenAsync", profile, lease, CancellationToken.None)!;
        await archive;
        await ((IAsyncDisposable)Get(archive, "Result")!).DisposeAsync();

        Type catalogType = _library.GetType("MeowChan.Archive.BooruCatalogStore", true)!;
        object catalog = New(catalogType, profile, lease);
        Type claimType = _library.GetType("MeowChan.Archive.BooruPostClaim", true)!;
        ConstructorInfo claimConstructor = claimType.GetConstructors().Single(item => item.GetParameters().Length == 14);
        void LinkOriginal(string id, string root, string asset)
        {
            object claim = claimConstructor.Invoke(claimConstructor.GetParameters().Select((parameter, index) => index switch
            {
                0 => "danbooru",
                1 => id,
                2 => Activator.CreateInstance(parameter.ParameterType),
                _ => parameter.DefaultValue
            }).ToArray());
            Call(catalogType, catalog, "UpsertPost", claim, DateTimeOffset.UtcNow, CancellationToken.None);
            Call(catalogType, catalog, "LinkLocalAsset", "danbooru", id, asset,
                Path.Combine(root, originalName), CancellationToken.None);
        }
        LinkOriginal("123", originalDirectory ?? downloads, "fixture-asset");
        if (previous is not null) LinkOriginal("456", previous, "fixture-secondary");
    }

    internal async Task WriteTabsAsync(string profile, bool draft)
    {
        string file = Path.Combine(profile, "booru-search-tabs.json");
        string kind = draft ? "draft" : "full";
        using Stream fixture = typeof(Legacy113ProfileWriter).Assembly
            .GetManifestResourceStream($"MeowChan.DesktopTests.Fixtures.Booru112.{kind}.json")!;
        JsonObject seed = JsonNode.Parse(fixture)!.AsObject();
        if (!draft)
        {
            JsonNode tab = seed["tabs"]![seed["activeIndex"]!.GetValue<int>()]!;
            tab["isEnd"] = false;
            tab["maximumPage"] = 8;
            tab["lastResultPage"] = 5;
        }
        await File.WriteAllTextAsync(file, seed.ToJsonString());

        using var lease = (IDisposable)Call(_lease, null, "Acquire", profile)!;
        object admission = Get(lease, "Admission")!;
        Call(admission.GetType(), admission, "CompleteStartup");
        Type storeType = _desktop.GetType("MeowChan.Desktop.Booru.BooruSearchTabStore", true)!;
        object store = New(storeType, lease);
        object snapshot = LoadTabs(storeType, store);
        Call(storeType, store, "Save", snapshot, 30);
        Assert.True(await (Task<bool>)Get(store, "PendingSave")!);
        Assert.True(File.Exists(file));
    }

    internal void CheckAndEdit(string profile, string downloads, string originalName, bool edit, string? previous = null)
    {
        using var lease = (IDisposable)Call(_lease, null, "Acquire", profile)!;
        object settings = Call(_config, null, "LoadConfig", profile, lease)!;
        Assert.Equal(downloads, Get(settings, "DownloadPath"));
        Assert.Equal(edit ? "en" : "de", Get(settings, "Language"));
        Type sessionType = _library.GetType("MeowChan.Persistence.SessionStore", true)!;
        object store = Activator.CreateInstance(sessionType, profile, lease)!;
        Task read = (Task)Call(sessionType, store, "LoadAsync", CancellationToken.None)!;
        read.GetAwaiter().GetResult();
        object session = Get(Get(read, "Result")!, "Snapshot")!;
        var directories = Assert.IsType<Dictionary<string, string>>(Get(session, "DownloadDirectories"));
        if (previous is null) Assert.Equal(downloads, Assert.Single(directories).Value);
        else
        {
            Assert.Equal(2, directories.Count);
            Assert.Equal(downloads, directories["https://boards.4chan.org/g/thread/123"]);
            Assert.Equal(previous, directories["https://boards.4chan.org/g/thread/456"]);
        }
        object admission = Get(lease, "Admission")!;
        Call(admission.GetType(), admission, "CompleteStartup");
        Type tabStore = _desktop.GetType("MeowChan.Desktop.Booru.BooruSearchTabStore", true)!;
        object snapshot = LoadTabs(tabStore, New(tabStore, lease));
        var tabs = Assert.IsAssignableFrom<Array>(Get(snapshot, "Tabs"));
        Assert.Equal(33, tabs.Length);
        object active = tabs.GetValue((int)Get(snapshot, "ActiveIndex")!)!;
        Assert.Equal(2, Get(active, "Page"));
        Assert.Equal(5, Get(active, "LastResultPage"));
        Type catalogType = _library.GetType("MeowChan.Archive.BooruCatalogStore", true)!;
        object catalog = New(catalogType, profile, lease);
        object? post = Call(catalogType, catalog, "TryGetPost", "danbooru", "123");
        Assert.NotNull(post);
        Assert.IsType(_library.GetType("MeowChan.Archive.BooruPostRecord", true)!, post);
        Assert.Equal("fixture-asset", Get(post, "LocalAssetId"));
        Assert.Equal(Path.Combine(downloads, originalName), Get(post, "LocalPath"));
        if (previous is not null)
        {
            object? secondary = Call(catalogType, catalog, "TryGetPost", "danbooru", "456");
            Assert.NotNull(secondary);
            Assert.Equal("fixture-secondary", Get(secondary, "LocalAssetId"));
            Assert.Equal(Path.Combine(previous, originalName), Get(secondary, "LocalPath"));
        }
        Assert.Null(Call(catalogType, catalog, "TryGetPost", "e621", "123"));
        if (!edit) return;
        Set(settings, "Language", "de");
        Call(_config, settings, "SaveConfig", profile);
    }

    private object LoadTabs(Type storeType, object store)
    {
        Type viewType = _desktop.GetType("MeowChan.Desktop.Booru.BooruSearchView", true)!;
        ConstructorInfo constructor = viewType.GetConstructors().Single(item => item.GetParameters().Length == 16);
        object blank = constructor.Invoke(constructor.GetParameters().Select(parameter =>
            parameter.ParameterType == typeof(string) ? "danbooru"
            : parameter.ParameterType.IsValueType ? Activator.CreateInstance(parameter.ParameterType) : null).ToArray());
        object? snapshot = Call(storeType, store, "Load", blank, 30, null);
        Assert.NotNull(snapshot);
        Assert.IsType(_desktop.GetType("MeowChan.Desktop.Booru.BooruTabSnapshot", true)!, snapshot);
        return snapshot;
    }

    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private static object New(Type type, params object[] args) =>
        Activator.CreateInstance(type, Members, binder: null, args, culture: null)!;

    private static object? Call(Type type, object? instance, string name, params object?[] args) =>
        type.GetMethods(Members).Single(method => method.Name == name && method.GetParameters().Length == args.Length)
            .Invoke(instance, args);

    private static object? Get(object instance, string property) => instance.GetType().GetProperty(property, Members)!.GetValue(instance);

    private static void Set(object instance, string property, object value) =>
        instance.GetType().GetProperty(property, Members)!.SetValue(instance, value);

    public void Dispose() => Unload();
}

internal sealed class Legacy113WriterFactAttribute : FactAttribute
{
    public Legacy113WriterFactAttribute()
    {
        if (!File.Exists(Path.Combine(Legacy113ProfileWriter.DirectoryPath, "MeowChan.dll")))
            Skip = "Provision the 1.1.3 writer fixture with Fixtures/ProfileUpgrade/extract-linux-writer.py.";
    }
}
