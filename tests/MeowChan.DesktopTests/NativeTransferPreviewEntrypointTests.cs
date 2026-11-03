using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Avalonia.Interactivity;
using MeowChan;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Interfaces;
using MeowChan.Testing;
using MeowChan.Transfer;

namespace MeowChan.DesktopTests;

// Synthetic entrypoint filesystem integration and parsed AXAML/compiled metadata only.
// No profile or Window is constructed; XML/reflection assertions are not UI runtime coverage.
public sealed class NativeTransferPreviewEntrypointTests
{
    [Fact]
    public void WorkDirectoryFactoryAcceptsNoCallerPathAndCreatesDistinctEmptyOperationRoots()
    {
        using NativeTransferPreviewTestWorkspace first = new();
        using NativeTransferPreviewTestWorkspace second = new();
        MethodInfo factory = Assert.IsAssignableFrom<MethodInfo>(typeof(ArchivePage).GetMethod(
            nameof(ArchivePage.CreateTransferPreviewWorkDirectory), BindingFlags.NonPublic | BindingFlags.Static));

        Assert.Empty(factory.GetParameters());
        Assert.Equal(typeof(string), factory.ReturnType);
        Assert.NotEqual(first.Work, second.Work);
        foreach (string work in new[] { first.Work, second.Work })
        {
            Assert.True(Path.IsPathFullyQualified(work));
            Assert.StartsWith("meowchan-transfer-preview-", Path.GetFileName(work), StringComparison.Ordinal);
            Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())),
                Path.GetDirectoryName(work));
            Assert.Empty(Directory.GetFileSystemEntries(work));
            if (!OperatingSystem.IsWindows())
                Assert.Equal(NativeTransferPreviewTestWorkspace.OwnerOnly, File.GetUnixFileMode(work));
        }
    }

    [Fact]
    public async Task GeneratedOperationRootAndPrivateStageNeverReuseTheSourceOrItsParent()
    {
        using NativeTransferPreviewTestWorkspace workspace = new();
        string source = await workspace.WritePackageAsync();
        byte[] before = File.ReadAllBytes(source);
        string? staged = null, cleaned = null;
        UnixFileMode? stageMode = null;
        string[]? remainingAtCleanup = null;
        await using NativeTransferPreviewSession session = new(workspace.Work);
        session.AfterStageForTests = _ =>
        {
            // Capture observations only: the session converts callback exceptions to errors.
            staged = Directory.GetDirectories(workspace.Work).Single();
            if (!OperatingSystem.IsWindows()) stageMode = File.GetUnixFileMode(staged);
            return Task.CompletedTask;
        };
        session.BeforeDirectoryCleanupForTests = directory =>
        {
            cleaned = directory;
            remainingAtCleanup = Directory.GetFileSystemEntries(directory);
        };

        NativeTransferPreviewSnapshot result = await session.InspectAsync(source)
            .WaitAsync(NativeTransferPreviewTestWorkspace.Timeout);

        Assert.Equal(NativeTransferPreviewState.Inventory, result.State);
        Assert.Equal(NativeTransferPreviewError.None, result.Error);
        Assert.NotNull(result.Inventory);
        Assert.False(result.CleanupRetained);
        string stage = Assert.IsType<string>(staged);
        Assert.Equal(workspace.Work, Path.GetDirectoryName(stage));
        Assert.StartsWith(".meowchan-preview-", Path.GetFileName(stage), StringComparison.Ordinal);
        Assert.True(Guid.TryParseExact(Path.GetFileName(stage)[".meowchan-preview-".Length..], "N", out _));
        Assert.Equal(stage, cleaned);
        Assert.NotNull(remainingAtCleanup);
        Assert.Empty(remainingAtCleanup!);
        foreach (string work in new[] { workspace.Work, stage })
        {
            Assert.NotEqual(source, work);
            Assert.NotEqual(workspace.SourceRoot, work);
        }
        if (!OperatingSystem.IsWindows()) Assert.Equal(NativeTransferPreviewTestWorkspace.OwnerOnly, stageMode);
        Assert.False(Directory.Exists(stage));
        Assert.Empty(Directory.GetFileSystemEntries(workspace.Work));
        Assert.Equal(before, File.ReadAllBytes(source));
    }

    [Fact]
    public async Task SessionCleanupRetainsUnknownNestedChildrenAndDoesNotSweepItsParentOrSource()
    {
        using NativeTransferPreviewTestWorkspace workspace = new();
        string source = await workspace.WritePackageAsync();
        byte[] before = File.ReadAllBytes(source);
        string peer = Path.Combine(workspace.Work, "unowned-peer");
        string? stage = null, child = null, marker = null;
        File.WriteAllText(peer, "parent sentinel");
        await using NativeTransferPreviewSession session = new(workspace.Work);
        session.BeforeDirectoryCleanupForTests = directory =>
        {
            stage = directory;
            child = Directory.CreateDirectory(Path.Combine(directory, "unknown-child")).FullName;
            marker = Path.Combine(child, "keep.txt");
            File.WriteAllText(marker, "nested sentinel");
        };
        try
        {
            NativeTransferPreviewSnapshot result = await session.InspectAsync(source)
                .WaitAsync(NativeTransferPreviewTestWorkspace.Timeout);
            Assert.Equal(NativeTransferPreviewState.Inventory, result.State);
            Assert.True(result.CleanupRetained);
            await session.DisposeAsync();
            Assert.True(session.Current.CleanupRetained);
            Assert.Equal(workspace.Work, Path.GetDirectoryName(Assert.IsType<string>(stage)));
            Assert.Equal("nested sentinel", File.ReadAllText(Assert.IsType<string>(marker)));
            Assert.Equal("parent sentinel", File.ReadAllText(peer));
            Assert.Equal(before, File.ReadAllBytes(source));
            Assert.Single(Directory.GetDirectories(workspace.Work));
        }
        finally
        {
            await session.DisposeAsync();
            // Delete only fixture-authored paths, nonrecursively. Never sweep retained children.
            if (marker != null) File.Delete(marker);
            if (child != null) Directory.Delete(child, recursive: false);
            if (stage != null) Directory.Delete(stage, recursive: false);
            File.Delete(peer);
        }
    }

    [Fact]
    public void ParsedToolbarButtonTargetsAnExistingClickHandlerAndCompiledCommandDescriptor()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement root = XDocument.Load(Source("Desktop/Views/Archive/ArchivePage.axaml")).Root!;
        XElement button = Assert.Single(root.Descendants().Where(element =>
            element.Name.LocalName == "Button" &&
            (string?)element.Attribute(x + "Name") == "ArchiveTransferPreview"));
        Assert.Contains(button.Ancestors(), element =>
            (string?)element.Attribute(x + "Name") == "ArchivePageHeader");
        string handlerName = Assert.IsType<string>((string?)button.Attribute("Click"));
        Assert.Equal("OnTransferPreviewClick", handlerName);
        Assert.Null(button.Attribute("Command"));
        MethodInfo? handler = typeof(ArchivePage).GetMethod(handlerName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(handler);
        Assert.Equal(typeof(void), handler!.ReturnType);
        Assert.Equal(new[] { typeof(object), typeof(RoutedEventArgs) },
            handler.GetParameters().Select(parameter => parameter.ParameterType));

        CommandDefinition command = CommandCatalog.Get("archive.transfer-preview");
        Assert.Equal("transfer.preview.open", command.LabelKey);
        Assert.Equal(CommandScope.Archive, command.Scope);
        Assert.Equal("archive", command.Icon);
        Assert.Equal(new[] { "archive-header" }, command.Contexts);
        Assert.Null(command.Shortcut);
        Assert.True(command.IsImplemented);
        // This proves parsed markup + compiled metadata, NOT live router registration or clicks.
    }

    private static string Source(string relative, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative));
}

// Synthetic entrypoint fixture only; no production/session-test changes.
internal sealed class NativeTransferPreviewTestWorkspace : IDisposable
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    internal const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    internal string SourceRoot { get; }
    internal string Source => Path.Combine(SourceRoot, "synthetic.zip");
    internal string Work { get; }

    internal NativeTransferPreviewTestWorkspace()
    {
        SourceRoot = TemporaryWorkspacePaths.CreateDirectory("meowchan-transfer-preview-entry-source-");
        try { Work = ArchivePage.CreateTransferPreviewWorkDirectory(); }
        catch { Directory.Delete(SourceRoot, recursive: false); throw; }
    }

    internal async Task<string> WritePackageAsync()
    {
        DateTimeOffset when = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
        ResourceId id = new("4chan", "b", "7001");
        TransferPost post = new(new("15", 1, PostFieldPresence.All, Subject: "synthetic",
            Html: "<p>inert synthetic preview</p>", Attachments: ImmutableArray<CapturedAttachment>.Empty), []);
        NativeTransferCatalog catalog = new(when,
            [new TransferThread(id, null, "synthetic", ThreadSourceState.NotFound, when, null, null, null, [post])]);
        using MemoryStream output = new();
        TransferWriteResult written = await NativeTransferPackageWriter.WriteAsync(output, catalog,
            Array.Empty<TransferPayloadSource>(), Guid.Parse("5d7750f7-1c3a-4093-b770-7c854fcfb117"));
        Assert.True(written.IsComplete, written.Error.ToString());
        File.WriteAllBytes(Source, output.ToArray());
        return Source;
    }

    public void Dispose()
    {
        File.Delete(Source);
        Directory.Delete(SourceRoot, recursive: false);
        if (Directory.Exists(Work)) Directory.Delete(Work, recursive: false);
    }
}
