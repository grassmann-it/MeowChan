using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using MeowChan.Desktop.Commands;
using MeowChan.DesktopSmoke;

namespace MeowChan.DesktopTests;

/// <summary>Window-free contracts for failures that happen while DesktopSmoke is constructing its startup window.</summary>
public sealed class SmokeStartupContractTests
{
    [Fact]
    public void UnknownCommandRegistrationProducesAnActionableRepairDiagnostic()
    {
        Exception failure = new AggregateException(
            new InvalidOperationException("startup wrapper", new KeyNotFoundException("Unknown command: smoke.missing")));

        Assert.True(SmokeStartupDiagnostics.TryGetUnknownCommand(failure, out string commandId));
        Assert.Equal("smoke.missing", commandId);
        Assert.True(SmokeStartupDiagnostics.TryGetUnknownCommand("Desktop startup failed: Unknown command: smoke.missing\n", out string windowCommand));
        Assert.Equal(commandId, windowCommand);
        string diagnostic = SmokeStartupDiagnostics.UnknownCommandRegistration(commandId);
        Assert.Contains("smoke.missing", diagnostic, StringComparison.Ordinal);
        Assert.Contains("Desktop/Commands/CommandCatalog.cs", diagnostic, StringComparison.Ordinal);
        Assert.Contains("do not weaken", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void LiteralDesktopCommandRegistrationsMustBeCatalogEntries()
    {
        string repository = Repository();
        Regex registration = new(
            @"new\s+(?:MeowChan\.Desktop\.Commands\.)?CommandRegistration\(\s*""([^""]+)""",
            RegexOptions.Compiled);
        List<string> unknown = [];
        foreach (string file in Directory.EnumerateFiles(Path.Combine(repository, "Desktop"), "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(repository, "tests/MeowChan.DesktopSmoke"), "*.cs", SearchOption.AllDirectories)))
        {
            string relative = Path.GetRelativePath(repository, file);
            foreach (Match match in registration.Matches(File.ReadAllText(file)))
            {
                string id = match.Groups[1].Value;
                if (!CommandCatalog.TryGet(id, out _)) unknown.Add(relative + ": " + id);
            }
        }

        Assert.True(unknown.Count == 0,
            "DesktopSmoke would fail during startup on unknown command registration(s). "
            + "Add each id to Desktop/Commands/CommandCatalog.cs or correct the registration: "
            + string.Join(", ", unknown));
    }

    private static string Repository([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../.."));
}
