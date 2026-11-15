using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Favorites;
using MeowChan.Persistence;
using Xunit.Abstractions;

namespace MeowChan.DesktopTests;

// This receipt is NOT the unrelated upgrade-v1x/receipt.json. Only the pinned,
// provider-generated synthetic 1.0.2 fixture is accepted; absence is a skip, not PASS.
[Collection("Delivered plugin hosts")]
public sealed class Released102RestPackageUpgradeTests(ITestOutputHelper output)
{
    private const string Revision = "eec6c0169f8d52f7481ea0bf7febfd20981e97c3";
    private const string ProfileVariable = "MEOWCHAN_RELEASED102_PROFILE";
    private const long MaximumFileBytes = 32 * 1024 * 1024;

    [Released102FixtureFact]
    public async Task ReleasedProviderProfileSurvivesCurrentOpenAndTwoOfflineProcessRestarts()
    {
        string profile = RequiredPath(ProfileVariable);
        string sourceManifest = RequiredPath("MEOWCHAN_RELEASED102_SOURCE_MANIFEST");
        string consumer = RequiredPath("MEOWCHAN_RELEASED102_CONSUMER_DLL");
        string results = RequiredPath("MEOWCHAN_RELEASED102_RESULTS");
        Assert.True(File.Exists(consumer), "Build the current helper with the SAME current source/configuration as this test.");
        string consumerProduct = Path.Combine(Path.GetDirectoryName(consumer)!, "MeowChan.dll");
        Assert.True(File.Exists(consumerProduct), "The current helper must carry its current product assembly.");
        Assert.Equal(AssemblyHash(typeof(Config).Assembly), Hash(ReadOrdinaryFile(consumerProduct)));
        Assert.False(File.Exists(results) || Directory.Exists(results), "Use a new results directory for every attempt, including failures.");
        Assert.False(IsWithin(profile, results), "Evidence must live outside the profile inventory.");
        RequireOrdinaryDirectory(profile);
        byte[] receiptBytes = ReadOrdinaryFile(SafePath(profile, "v102-receipt.json"));
        using JsonDocument receipt = ReadDocument(receiptBytes);
        using JsonDocument provenance = ReadDocument(ReadOrdinaryFile(sourceManifest));
        JsonElement old = receipt.RootElement;
        ValidateProvenance(old, provenance.RootElement, sourceManifest);
        JsonElement files = old.GetProperty("Files");
        VerifyFreshFiles(profile, files);
        ValidateReceiptShape(profile, old);
        Assert.Equal(old.GetProperty("Protocol").GetString(), File.ReadAllText(SafePath(profile, ".meowchan-profile.protocol")));
        Assert.Equal(old.GetProperty("LegacyProfileLock").GetBoolean(), File.Exists(SafePath(profile, ".ochan-profile.lock")));

        // Receipt and source binding survive independently even when current startup fails.
        Directory.CreateDirectory(results);
        File.WriteAllBytes(Path.Combine(results, "released-receipt.json"), receiptBytes);
        // Keep the full released settings for check_migrated.py, bound by receipt.Files["settings.json"].
        // The receipt's selected Settings projection cannot attest new defaults or every old scalar.
        File.WriteAllBytes(Path.Combine(results, "released-settings.json"), ReadOrdinaryFile(SafePath(profile, "settings.json")));
        File.Copy(sourceManifest, Path.Combine(results, "released-source-manifest.json"));
        var cycles = new List<object>();
        for (int cycle = 0; cycle < 3; cycle++)
        {
            string result = Path.Combine(results, $"cycle-{cycle}.json");
            output.WriteLine("Starting separate current process: cycle {0} (0=upgrade, 1/2=offline restart)", cycle);
            await RunCycleAsync(consumer, profile, result);
            using JsonDocument snapshot = ReadDocument(ReadOrdinaryFile(result));
            JsonElement current = snapshot.RootElement;
            Assert.Equal("meowchan.released-102-current-cycle.v1", current.GetProperty("Schema").GetString());
            Assert.Equal(profile, current.GetProperty("Profile").GetString());
            Assert.Equal(AssemblyHash(typeof(Config).Assembly), current.GetProperty("CurrentAssemblySha256").GetString());
            Assert.Equal(AssemblyHash(typeof(ArchiveReader).Assembly), current.GetProperty("CurrentCoreAssemblySha256").GetString());
            Assert.True(current.GetProperty("HostStopped").GetBoolean());
            Assert.Empty(current.GetProperty("NetworkRequests").EnumerateArray());
            AssertCycle(old, current);
            Assert.Equal(receiptBytes, ReadOrdinaryFile(SafePath(profile, "v102-receipt.json")));
            VerifyOriginalBytes(profile, old);
            cycles.Add(new { Cycle = cycle, Result = Path.GetFileName(result), Sha256 = Hash(ReadOrdinaryFile(result)),
                ProcessId = current.GetProperty("ProcessId").GetInt32() });
            output.WriteLine("Cycle {0}: receipt identities/bytes/metadata/settings/watch assertions completed; zero HTTP attempts.", cycle);
        }
        // Only the parent publishes acceptance after ALL three successful shutdowns and comparisons.
        File.WriteAllText(Path.Combine(results, "current-result.json"), JsonSerializer.Serialize(new
        {
            Schema = "meowchan.released-102-rest-package-result.v1", SourceRevision = Revision, Synthetic = true,
            ReceiptSha256 = Hash(receiptBytes), ConsumerSha256 = Hash(ReadOrdinaryFile(consumer)),
            SubsequentOfflineProcessRestarts = 2, Cycles = cycles,
            Scope = "Synthetic released-provider profile + current DesktopSessionHost processes; not UI, package, plugin-consent, tag or platform acceptance"
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private async Task RunCycleAsync(string consumer, string profile, string result)
    {
        string host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        // Opt-in OS-level network denial for the three current processes: a private user+network namespace in which
        // only a downed loopback exists. The consumer's own injected HTTP handler (zero requests) stays in force too.
        bool isolated = Environment.GetEnvironmentVariable("MEOWCHAN_RELEASED102_NETWORK_NAMESPACE") == "1";
        ProcessStartInfo start = new(isolated ? "unshare" : host)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(consumer)!
        };
        if (isolated) { start.ArgumentList.Add("-Urn"); start.ArgumentList.Add("--"); start.ArgumentList.Add(host); }
        start.ArgumentList.Add(consumer); start.ArgumentList.Add(profile); start.ArgumentList.Add(result);
        start.Environment.Remove("MEOWCHAN_PROFILE"); start.Environment.Remove("OCHAN_PROFILE");
        start.Environment["MEOWCHAN_DISABLE_UPDATE_CHECK"] = "1";
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start current consumer.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(240)); }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            throw;
        }
        string log = await stdout; string errors = await stderr;
        await File.WriteAllTextAsync(result + ".stdout.log", log);
        await File.WriteAllTextAsync(result + ".stderr.log", errors);
        output.WriteLine("Current consumer exit {0}\n{1}\n{2}", process.ExitCode, log, errors);
        Assert.True(process.ExitCode == 0, $"Current cycle failed (exit {process.ExitCode}); retain {result}*.log and regenerate, never repair the old profile.");
        Assert.True(File.Exists(result), "No fully disposed cycle receipt was published.");
    }

    private static void AssertCycle(JsonElement old, JsonElement current)
    {
        JsonElement expected = old.GetProperty("Inventory");
        JsonElement observed = current.GetProperty("Observed");
        JsonElement persisted = current.GetProperty("Persisted");
        Assert.True(observed.GetProperty("IsPaused").GetBoolean());
        AssertJson(expected.GetProperty("Settings"), observed.GetProperty("Settings"), "ready settings");
        AssertJson(expected.GetProperty("Settings"), persisted.GetProperty("Settings"), "post-shutdown settings");
        Assert.False(persisted.GetProperty("Recovered").GetBoolean());
        Assert.Equal(JsonValueKind.Null, persisted.GetProperty("Warning").ValueKind);
        AssertJson(expected.GetProperty("Watch"), persisted.GetProperty("Watch"), "persisted watch snapshot (including download directories)");
        AssertJsonSet(expected.GetProperty("MediaFavorites"), observed.GetProperty("MediaFavorites"), "exact media favorites");
        AssertJsonSet(expected.GetProperty("Ratings"), observed.GetProperty("Ratings"), "ratings (full key and value)");
        AssertJson(expected.GetProperty("ThreadFavorites"), observed.GetProperty("ThreadFavorites"), "thread favorites/revision/fingerprint");
        AssertJson(expected.GetProperty("Bookmarks"), observed.GetProperty("Bookmarks"), "bookmark keys, multiline notes and revision");
        AssertJsonSet(expected.GetProperty("MediaFavorites"), persisted.GetProperty("MediaFavorites"), "post-shutdown media favorites");
        AssertJsonSet(expected.GetProperty("Ratings"), persisted.GetProperty("Ratings"), "post-shutdown ratings");
        AssertJson(expected.GetProperty("ThreadFavorites"), persisted.GetProperty("ThreadFavorites"), "post-shutdown thread favorites");
        AssertJson(expected.GetProperty("Bookmarks"), persisted.GetProperty("Bookmarks"), "post-shutdown bookmarks");
        JsonElement threads = old.GetProperty("Threads");
        JsonElement actualThreads = observed.GetProperty("Threads");
        JsonElement hostThreads = observed.GetProperty("HostThreads");
        JsonElement work = observed.GetProperty("Work");
        Assert.Equal(threads.GetArrayLength(), actualThreads.GetArrayLength());
        Assert.Equal(threads.GetArrayLength(), hostThreads.GetArrayLength());
        Assert.Equal(threads.GetArrayLength(), work.GetArrayLength());
        Assert.Equal(threads.GetArrayLength(), observed.GetProperty("ArchiveTotal").GetInt32());
        AssertJsonSet(JsonSerializer.SerializeToElement(threads.EnumerateArray().Select(t => t.GetProperty("Id")).ToArray()),
            observed.GetProperty("ArchiveIds"), "complete archived identities");
        AssertJsonSet(expected.GetProperty("Watch").GetProperty("BoardUrls"), observed.GetProperty("HostBoardUrls"), "watched boards");
        foreach (JsonElement thread in threads.EnumerateArray())
        {
            JsonElement id = thread.GetProperty("Id");
            JsonElement actual = Assert.Single(actualThreads.EnumerateArray(), t => JsonElement.DeepEquals(id, t.GetProperty("Id")));
            JsonElement watch = Assert.Single(hostThreads.EnumerateArray(), t => JsonElement.DeepEquals(id, t.GetProperty("Id")));
            AssertJson(thread.GetProperty("Url"), watch.GetProperty("Url"), "watch URL");
            AssertJson(thread.GetProperty("Url"), actual.GetProperty("Url"), "archive URL");
            AssertJson(thread.GetProperty("MediaDirectory"), actual.GetProperty("MediaDirectory"), "media directory identity");
            // C18 adds nullable image dimensions to the current attachment projection. A
            // genuinely old 1.0.2 capture has no dimensions, which must remain null rather
            // than making this immutable-profile oracle fail on newly materialized fields.
            AssertJson(WithLegacyAttachmentDimensions(thread.GetProperty("Posts")), actual.GetProperty("Posts"),
                "every captured post/attachment/bound revision field");
            JsonElement asset = thread.GetProperty("Asset");
            JsonElement bytes = Assert.Single(actual.GetProperty("Assets").EnumerateArray());
            AssertJson(asset, bytes.GetProperty("Reference"), "complete manifest reference/asset identity");
            AssertJson(asset.GetProperty("Length"), bytes.GetProperty("Length"), "verified original length");
            Assert.Equal(asset.GetProperty("Sha256").GetString(), bytes.GetProperty("Sha256").GetString(), ignoreCase: true);
            // C24: the release asset's optional local SHA-1 measurement must survive the
            // upgrade and still match the bytes read from the current manifest.
            Assert.Equal(asset.GetProperty("Sha1").GetString(), bytes.GetProperty("Sha1").GetString());
            JsonElement resource = Assert.Single(work.EnumerateArray(), w => w.GetProperty("Url").GetString() == thread.GetProperty("Url").GetString());
            JsonElement media = Assert.Single(resource.GetProperty("Media").EnumerateArray());
            AssertJson(asset.GetProperty("MediaIdentifier"), media.GetProperty("MediaIdentifier"), "work media identifier");
            AssertJson(asset.GetProperty("Variant"), media.GetProperty("Variant"), "work variant");
            AssertJson(asset.GetProperty("AssetId"), media.GetProperty("CommittedAssetId"), "work committed asset identity");
            AssertJson(asset.GetProperty("SourceUrl"), media.GetProperty("DownloadUrl"), "work download origin");
            Assert.Equal((int)MediaWorkStatus.Succeeded, media.GetProperty("Status").GetInt32());
            JsonElement export = Assert.Single(actual.GetProperty("Exports").EnumerateArray());
            JsonElement inspection = export.GetProperty("Inspection");
            Assert.NotEqual(JsonValueKind.Null, inspection.ValueKind);
            // A released-1.0.2 marker predates press-time language, so its document has no
            // Options.Language. Current code derives the documented default "en" on read. Compare
            // with that single derived field normalized instead of rewriting the immutable export
            // (exports are byte-verified originals, see VerifyOriginalBytes).
            AssertJson(WithDerivedExportLanguage(thread.GetProperty("Export")), inspection.GetProperty("Metadata"),
                "released export metadata/files/generation");
            Assert.Empty(inspection.GetProperty("ModifiedPaths").EnumerateArray());
            Assert.Empty(inspection.GetProperty("ExtraPaths").EnumerateArray());
            AssertJson(id, export.GetProperty("Entry").GetProperty("Thread"), "export history thread");
            AssertJson(thread.GetProperty("Export").GetProperty("ExportId"), export.GetProperty("Entry").GetProperty("ExportId"), "export history identity");
            AssertJson(thread.GetProperty("Export").GetProperty("Generation"), export.GetProperty("Entry").GetProperty("Generation"), "export history generation");
        }
    }

    private static void ValidateProvenance(JsonElement receipt, JsonElement source, string manifest)
    {
        Assert.Equal("meowchan.synthetic-upgrade-102.v1", receipt.GetProperty("Schema").GetString());
        Assert.True(receipt.GetProperty("Synthetic").GetBoolean());
        Assert.Equal(Revision, receipt.GetProperty("SourceRevision").GetString());
        Assert.Equal("1.0.2+" + Revision, receipt.GetProperty("ProductVersion").GetString());
        Assert.Equal("meowchan.synthetic-upgrade-102-source.v2", source.GetProperty("schema").GetString());
        Assert.True(source.GetProperty("synthetic").GetBoolean());
        Assert.Equal(Revision, source.GetProperty("source_commit").GetString());
        Assert.Equal(Hash(ReadOrdinaryFile(manifest)), receipt.GetProperty("SourceManifestSha256").GetString(), ignoreCase: true);
        Assert.NotEmpty(source.GetProperty("files").EnumerateObject());
        Assert.Equal(new[] { "tests/Upgrade102FixtureGenerator/Program.cs", "tests/Upgrade102FixtureGenerator/UpgradeFixtureGenerator.csproj" },
            source.GetProperty("overlay").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        // Full source/blob/overlay checking remains mandatory via check.py BEFORE the current open.
    }

    private static void ValidateReceiptShape(string profile, JsonElement old)
    {
        JsonElement threads = old.GetProperty("Threads");
        Assert.Equal(2, threads.GetArrayLength()); // Fixture shape guards, never the preservation oracle.
        Assert.Equal(new[] { "4chan", "8kun" }, threads.EnumerateArray().Select(t => t.GetProperty("Id").GetProperty("Source").GetString()).Order());
        var keys = new HashSet<FavoriteAssetKey>();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement thread in threads.EnumerateArray())
        {
            ResourceId id = thread.GetProperty("Id").Deserialize<ResourceId>()!;
            Assert.Equal("g", id.Board); Assert.Equal("123", id.Thread);
            Assert.Equal(new[] { "123", "124" }, thread.GetProperty("Posts").EnumerateArray().Select(p => p.GetProperty("PostId").GetString()).Order());
            var favorite = thread.GetProperty("Favorite").Deserialize<FavoriteEntry>()!;
            Assert.Equal(id, favorite.Key.Thread); Assert.True(keys.Add(favorite.Key));
            Assert.Equal(thread.GetProperty("Asset").GetProperty("Length").GetInt64(), favorite.Length);
            string assetPath = thread.GetProperty("Asset").GetProperty("Path").GetString()!;
            Assert.True(paths.Add(assetPath), "Equal bytes at independent occurrences must remain separate paths/identities.");
            SafeAbsolute(profile, thread.GetProperty("MediaDirectory").GetString()!);
            SafeAbsolute(profile, assetPath);
            Assert.Equal(FavoriteAssetKey.From(id, thread.GetProperty("Asset").Deserialize<MeowChan.Storage.MediaAssetReference>()!), favorite.Key);
        }
        JsonElement inventory = old.GetProperty("Inventory");
        AssertJsonSet(JsonSerializer.SerializeToElement(threads.EnumerateArray().Select(t => t.GetProperty("Favorite")).ToArray()),
            inventory.GetProperty("MediaFavorites"), "receipt favorites agree with released thread readback");
        AssertJsonSet(JsonSerializer.SerializeToElement(threads.EnumerateArray().Select(t => t.GetProperty("Id")).ToArray()),
            inventory.GetProperty("ThreadFavorites").GetProperty("Threads"), "receipt thread favorites");
        AssertJsonSet(JsonSerializer.SerializeToElement(threads.EnumerateArray().Select(t => t.GetProperty("Url")).ToArray()),
            inventory.GetProperty("Watch").GetProperty("ThreadUrls"), "receipt watch URLs");
        JsonElement ratings = inventory.GetProperty("Ratings");
        Assert.Equal(keys.Count, ratings.GetArrayLength());
        foreach (JsonElement rating in ratings.EnumerateArray())
        {
            Assert.Contains(rating.GetProperty("Key").Deserialize<FavoriteAssetKey>()!, keys);
            Assert.InRange(rating.GetProperty("Value").GetInt32(), 1, 5);
        }
        JsonElement bookmarks = inventory.GetProperty("Bookmarks").GetProperty("Bookmarks");
        Assert.Equal(threads.GetArrayLength(), bookmarks.GetArrayLength());
        foreach (JsonElement thread in threads.EnumerateArray())
        {
            JsonElement bookmark = Assert.Single(bookmarks.EnumerateArray(), b =>
                JsonElement.DeepEquals(thread.GetProperty("Id"), b.GetProperty("Key").GetProperty("Thread")));
            Assert.Contains(thread.GetProperty("Posts").EnumerateArray(), post =>
                post.GetProperty("PostId").GetString() == bookmark.GetProperty("Key").GetProperty("Post").GetString());
            Assert.False(string.IsNullOrEmpty(bookmark.GetProperty("Note").GetString()));
        }
        JsonElement settings = inventory.GetProperty("Settings");
        Assert.True(settings.GetProperty("DownloadsPaused").GetBoolean());
        Assert.False(settings.GetProperty("AutomaticUpdateChecks").GetBoolean());
        SafeAbsolute(profile, settings.GetProperty("DownloadPath").GetString()!);
        SafeAbsolute(profile, settings.GetProperty("LastExportDirectory").GetString()!);
        foreach (JsonProperty directory in old.GetProperty("Inventory").GetProperty("Watch").GetProperty("DownloadDirectories").EnumerateObject())
            SafeAbsolute(profile, directory.Value.GetString()!);
        JsonElement requests = old.GetProperty("Requests");
        Assert.InRange(requests.GetArrayLength(), 1, 256);
        string[] allowed = ["https://a.4cdn.org/boards.json", "https://8kun.top/boards.json",
            "https://a.4cdn.org/g/thread/123.json", "https://8kun.top/g/res/123.json",
            "https://i.4cdn.org/g/456.png", "https://nerv.8kun.top/file_store/456.png"];
        Assert.All(requests.EnumerateArray(), request => Assert.Contains(request.GetString(), allowed));
    }

    private static void VerifyFreshFiles(string profile, JsonElement files)
    {
        JsonProperty[] expected = files.EnumerateObject().ToArray();
        Assert.InRange(expected.Length, 1, 4096);
        Assert.DoesNotContain(expected, file => file.Name == "v102-receipt.json");
        HashSet<string> actual = new(StringComparer.Ordinal);
        Stack<string> pending = new(); pending.Push(profile);
        int entries = 0;
        while (pending.Count != 0)
            foreach (string path in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                Assert.InRange(++entries, 1, 8192);
                FileAttributes attributes = File.GetAttributes(path);
                Assert.False((attributes & FileAttributes.ReparsePoint) != 0, "No fixture links: " + path);
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
                else actual.Add(Path.GetRelativePath(profile, path).Replace(Path.DirectorySeparatorChar, '/'));
            }
        Assert.Equal(expected.Select(file => file.Name).Append("v102-receipt.json").Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));
        long total = 0;
        foreach (JsonProperty file in expected)
        {
            byte[] bytes = ReadOrdinaryFile(SafePath(profile, file.Name));
            Assert.InRange(total += bytes.Length, 0, 128L * 1024 * 1024);
            Assert.Equal(file.Value.GetString(), Hash(bytes), ignoreCase: true);
        }
    }

    private static void VerifyOriginalBytes(string profile, JsonElement old)
    {
        foreach (JsonElement thread in old.GetProperty("Threads").EnumerateArray())
        {
            JsonElement asset = thread.GetProperty("Asset");
            byte[] bytes = ReadOrdinaryFile(SafeAbsolute(profile, asset.GetProperty("Path").GetString()!));
            Assert.Equal(asset.GetProperty("Length").GetInt64(), bytes.LongLength);
            Assert.Equal(asset.GetProperty("Sha256").GetString(), Hash(bytes), ignoreCase: true);
        }
        // Original exports are immutable output; databases, caches and session envelopes are not.
        foreach (JsonProperty file in old.GetProperty("Files").EnumerateObject().Where(file => file.Name.StartsWith("exports/", StringComparison.Ordinal)))
            Assert.Equal(file.Value.GetString(), Hash(ReadOrdinaryFile(SafePath(profile, file.Name))), ignoreCase: true);
    }

    /// <summary>Adds only the documented default language when a released marker predates it.</summary>
    private static JsonElement WithDerivedExportLanguage(JsonElement released)
    {
        if (!released.TryGetProperty("Options", out JsonElement options) ||
            options.ValueKind != JsonValueKind.Object ||
            options.TryGetProperty("Language", out _))
            return released;
        Dictionary<string, JsonElement> extended = new();
        foreach (JsonProperty property in options.EnumerateObject()) extended[property.Name] = property.Value;
        extended["Language"] = JsonSerializer.SerializeToElement("en");
        Dictionary<string, JsonElement> rewritten = new();
        foreach (JsonProperty property in released.EnumerateObject())
            rewritten[property.Name] = property.NameEquals("Options") ? JsonSerializer.SerializeToElement(extended) : property.Value;
        return JsonSerializer.SerializeToElement(rewritten);
    }

    private static JsonElement WithLegacyAttachmentDimensions(JsonElement posts)
    {
        List<JsonElement> normalizedPosts = new();
        foreach (JsonElement post in posts.EnumerateArray())
        {
            Dictionary<string, JsonElement> postProperties = post.EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
            List<JsonElement> attachments = new();
            foreach (JsonElement attachment in post.GetProperty("Attachments").EnumerateArray())
            {
                Dictionary<string, JsonElement> properties = attachment.EnumerateObject()
                    .ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
                properties.TryAdd("Width", JsonSerializer.SerializeToElement((int?)null));
                properties.TryAdd("Height", JsonSerializer.SerializeToElement((int?)null));
                attachments.Add(JsonSerializer.SerializeToElement(properties));
            }
            postProperties["Attachments"] = JsonSerializer.SerializeToElement(attachments);
            normalizedPosts.Add(JsonSerializer.SerializeToElement(postProperties));
        }
        return JsonSerializer.SerializeToElement(normalizedPosts);
    }

    private static void AssertJson(JsonElement expected, JsonElement actual, string context) =>
        Assert.True(JsonElement.DeepEquals(expected, actual), $"Receipt mismatch ({context}).\nExpected: {expected}\nActual: {actual}");

    private static void AssertJsonSet(JsonElement expected, JsonElement actual, string context)
    {
        Assert.True(expected.GetArrayLength() == actual.GetArrayLength(), "Receipt set size mismatch: " + context);
        foreach (JsonElement item in expected.EnumerateArray())
            Assert.True(actual.EnumerateArray().Count(candidate => JsonElement.DeepEquals(item, candidate)) == 1,
                $"Receipt set mismatch ({context}): {item}");
        foreach (JsonElement item in actual.EnumerateArray())
            Assert.True(expected.EnumerateArray().Count(candidate => JsonElement.DeepEquals(item, candidate)) == 1,
                $"Unexpected or duplicate receipt set item ({context}): {item}");
    }

    private static string RequiredPath(string variable)
    {
        string? value = Environment.GetEnvironmentVariable(variable);
        Assert.False(string.IsNullOrWhiteSpace(value), "Missing prerequisite: " + variable);
        Assert.True(Path.IsPathFullyQualified(value!), "Use an absolute path: " + variable);
        return Path.GetFullPath(value!);
    }

    private static bool IsWithin(string root, string path) =>
        path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal) || path == root;

    private static string SafeAbsolute(string root, string path)
    {
        Assert.True(Path.IsPathFullyQualified(path) && IsWithin(root, Path.GetFullPath(path)), "Fixture path escaped its owned profile: " + path);
        return SafePath(root, Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'));
    }

    private static string SafePath(string root, string relative)
    {
        string[] parts = relative.Split('/');
        if (Path.IsPathRooted(relative) || relative.Contains('\\') || parts.Any(p => p is "" or "." or ".." || p.Contains(':') || p.Any(char.IsControl)))
            throw new InvalidDataException("Unsafe fixture relative path: " + relative);
        string result = root;
        foreach (string part in parts)
        {
            result = Path.Combine(result, part);
            if (new FileInfo(result).LinkTarget != null || new DirectoryInfo(result).LinkTarget != null)
                throw new InvalidDataException("Link in fixture path: " + relative);
        }
        return result;
    }

    private static void RequireOrdinaryDirectory(string path) =>
        Assert.True(Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0, "Requires an ordinary owned profile directory.");

    private static byte[] ReadOrdinaryFile(string path)
    {
        FileInfo file = new(path);
        Assert.True(file.Exists && file.LinkTarget == null && file.Length <= MaximumFileBytes, "Missing, linked or oversized fixture file: " + path);
        return File.ReadAllBytes(path);
    }

    private static JsonDocument ReadDocument(byte[] bytes)
    {
        JsonDocument document = JsonDocument.Parse(bytes);
        try { RejectDuplicateProperties(document.RootElement); return document; }
        catch { document.Dispose(); throw; }
    }

    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate JSON key: " + property.Name);
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in value.EnumerateArray()) RejectDuplicateProperties(item);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string AssemblyHash(Assembly assembly)
    {
        using FileStream input = File.OpenRead(assembly.Location);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("media/../escape")]
    [InlineData("media//file")]
    [InlineData("media\\file")]
    [InlineData("C:/file")]
    [InlineData("media/./file")]
    public void ReceiptPathsCannotEscapeOrAliasTheirOwnedProfile(string relative)
    {
        using TempDirectory temp = new();
        Assert.Throws<InvalidDataException>(() => SafePath(temp.Path, relative));
    }

    // Pure assertion-helper inputs, NOT hand-authored old profile state.
    [Theory]
    [InlineData("[{\"identity\":\"A\",\"value\":2}]", "[{\"identity\":\"B\",\"value\":2}]")]
    [InlineData("[{\"identity\":\"A\",\"value\":2}]", "[{\"identity\":\"A\",\"value\":5}]")]
    [InlineData("[{\"note\":\"line1\\nline2\"}]", "[{\"note\":\"line1 line2\"}]")]
    [InlineData("[1,1]", "[1,2]")]
    public void ReceiptSetComparisonRejectsSameCountSubstitutions(string expected, string actual)
    {
        using JsonDocument before = JsonDocument.Parse(expected);
        using JsonDocument after = JsonDocument.Parse(actual);
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertJsonSet(before.RootElement, after.RootElement, "guard unit input"));
    }

    [Fact]
    public void DuplicateReceiptPropertiesAreRejectedEvenWhenNested()
    {
        Assert.Throws<InvalidDataException>(() => ReadDocument("{\"Inventory\":{\"Value\":1,\"Value\":2}}"u8.ToArray()));
    }
}

public sealed class Released102FixtureFactAttribute : FactAttribute
{
    public Released102FixtureFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MEOWCHAN_RELEASED102_PROFILE")))
            Skip = "Requires a fresh released-1.0.2 provider fixture plus built current consumer; see tests/fixtures/upgrade-v1.0.2/README.md. A skip is not upgrade acceptance.";
    }
}
