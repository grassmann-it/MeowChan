using MeowChan.Archive;
using MeowChan.Testing;
using Xunit.Abstractions;

namespace MeowChan.Tests;

public sealed class ArchiveExportPathSafetyTests(ITestOutputHelper output)
{
    [Fact]
    public void NativeTemporaryPathDiagnosticsAndPhysicalFixtureKeepTheStrictExportGuard()
    {
        string requested = Path.GetFullPath(Path.GetTempPath());
        string physical = TemporaryWorkspacePaths.TempRoot;
        output.WriteLine("OS: " + System.Runtime.InteropServices.RuntimeInformation.OSDescription);
        output.WriteLine("Requested temporary root: " + requested);
        output.WriteLine("Physical temporary root: " + physical);
        string? firstLink = null;
        for (string? current = Path.TrimEndingDirectorySeparator(requested); current != null; current = Path.GetDirectoryName(current))
        {
            DirectoryInfo info = new(current);
            output.WriteLine($"Ancestor '{current}': LinkTarget='{info.LinkTarget ?? "(none)"}', Attributes={info.Attributes}");
            if (firstLink == null && (info.LinkTarget != null || (info.Attributes & FileAttributes.ReparsePoint) != 0))
                firstLink = current;
        }
        if (firstLink != null)
        {
            InvalidDataException rejection = Assert.Throws<InvalidDataException>(() => ArchiveExportMetadataFile.RejectLinks(requested));
            Assert.Contains(firstLink, rejection.Message);
            output.WriteLine("Strict raw-path rejection: " + rejection.Message);
        }
        else ArchiveExportMetadataFile.RejectLinks(requested);
        ArchiveExportMetadataFile.RejectLinks(physical);
        using TempDirectory workspace = new();
        ArchiveExportMetadataFile.RejectLinks(workspace.Path);
        Assert.Equal(workspace.Path, TemporaryWorkspacePaths.PhysicalDirectory(workspace.Path));
        output.WriteLine("Physical fixture: " + workspace.Path);
    }

    [Fact]
    public void CanonicalizingOwnedWorkspaceDoesNotPermitCallerSuppliedLinkedExportParents()
    {
        using TempDirectory workspace = new();
        string actual = Path.Combine(workspace.Path, "physical");
        Directory.CreateDirectory(Path.Combine(actual, "nested"));
        string alias = Path.Combine(workspace.Path, "alias");
        try { Directory.CreateSymbolicLink(alias, actual); }
        catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        { output.WriteLine("Symbolic-link creation unavailable: " + exception.Message); return; }
        Assert.Equal(Path.Combine(actual, "nested"), TemporaryWorkspacePaths.PhysicalDirectory(Path.Combine(alias, "nested")));
        string requested = Path.Combine(alias, "nested", "new-export");
        InvalidDataException rejection = Assert.Throws<InvalidDataException>(() => ArchiveExportMetadataFile.RejectLinks(requested));
        Assert.Contains(alias, rejection.Message);
        Assert.Contains(requested, rejection.Message);
        ArchiveExportMetadataFile.RejectLinks(Path.Combine(actual, "nested", "new-export"));
        Assert.False(Directory.Exists(requested));
    }

    [Fact]
    public void LinkedAndDanglingExportEntriesStillFailWithTheSpecificRejectedComponent()
    {
        using TempDirectory workspace = new();
        string actual = Path.Combine(workspace.Path, "actual.html");
        File.WriteAllText(actual, "KEEP");
        string link = Path.Combine(workspace.Path, "index.html");
        try { File.CreateSymbolicLink(link, actual); }
        catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        { output.WriteLine("Symbolic-link creation unavailable: " + exception.Message); return; }
        InvalidDataException rejection = Assert.Throws<InvalidDataException>(() => ArchiveExportMetadataFile.RejectLinks(link));
        Assert.Contains(link, rejection.Message);
        File.Delete(actual);
        rejection = Assert.Throws<InvalidDataException>(() => ArchiveExportMetadataFile.RejectLinks(link));
        Assert.Contains(link, rejection.Message);
    }
}
