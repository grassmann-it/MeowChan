#!/usr/bin/env python3
"""Execute actual scoped dispatch expressions with recording ports; no GUI acceptance."""
from pathlib import Path
import re
import subprocess
import tempfile

REPO = Path(__file__).resolve().parents[2]
SOURCE = (REPO / "tests/MeowChan.DesktopSmoke/SmokeScenarios.Runs.cs").read_text()
start = SOURCE.index("    internal static Task RunReaderAlternativeAsync")
end = SOURCE.index("    // Exactly the Root", start)
reader_dispatch = SOURCE[start:end]
roots = {
    "gif-playback-core": "GifPlaybackChecks.RunPlaybackAsync",
    "gif-successors": "GifPlaybackChecks.RunSuccessorsAsync",
    "gif-verified-failures": "GifPlaybackChecks.RunVerifiedFailuresAsync",
    "gif-reader-association": "GifPlaybackChecks.RunReaderAssociationAsync",
    "gif-ownership": "GifOwnershipChecks.RunAsync",
    "gallery-errors-retirement-clip": "GalleryErrorInteractionChecks.RunRetirementClipAsync",
    "gallery-errors-layout-roots": "GalleryErrorInteractionChecks.RunLayoutRootsAsync",
    "gallery-errors-notifications": "GalleryErrorInteractionChecks.RunNotificationsAsync",
    "gallery-errors-failure-reader-return": "GalleryErrorInteractionChecks.RunFailureReaderReturnAsync",
}
bindings = []
for scene in roots:
    matches = re.findall(r'^        \["' + re.escape(scene) + r'"\] = .*?(?=^        \["|^    };)', SOURCE, re.M | re.S)
    assert len(matches) == 1, (scene, matches)
    bindings.append(matches[0])
ports = """
internal sealed class MainWindow;
internal enum SmokeStage { Ready }
internal sealed record SmokeRun(SmokeStage Stage, Func<MainWindow, Task> Run) { internal Func<string?>? Precondition { get; init; } }
internal static class Calls {
    internal static List<string> All = [];
    internal static Task Record(string call) { All.Add(call); return Task.CompletedTask; }
    internal static void Expect(string call) {
        if (!All.SequenceEqual(new[] { call })) throw new Exception(call + ": " + string.Join(",", All));
        All.Clear();
    }
}
internal static class VideoLifecycleChecks { internal static Task RunAsync() => Calls.Record("video"); }
internal static class WindowMinimumChecks { internal static Task RunAsync(MainWindow w) => Calls.Record("minimum"); }
internal static class ReaderUxChecks {
    internal static Task RunAsync(MainWindow w) => Calls.Record("reader");
    internal static Task RunJourneyAsync(MainWindow w, string id) => Calls.Record("journey:" + id);
}
"""
classes = {}
for target in roots.values():
    cls, method = target.split(".")
    classes.setdefault(cls, []).append(
        f'internal static Task {method}(MainWindow w) => Calls.Record("{target}");')
for cls, methods in classes.items():
    ports += f'internal static class {cls} {{ {" ".join(methods)} }}\n'
main = """
MainWindow window = new();
foreach (string id in new[] { "reader-owned-navigation", "reader-gallery-roundtrip", "reader-list-gallery-return" }) {
    await SmokeScenarios.RunReaderAlternativeAsync(id, window); Calls.Expect("journey:" + id);
}
await SmokeScenarios.RunReaderAlternativeAsync("video-lifecycle", window); Calls.Expect("video");
await SmokeScenarios.RunReaderAlternativeAsync("window-minimum", window); Calls.Expect("minimum");
await SmokeScenarios.RunReaderAlternativeAsync("reader-navigation", window); Calls.Expect("reader");
"""
for scene, target in roots.items():
    main += f'await SmokeScenarios.Runs["{scene}"].Run(window); Calls.Expect("{target}");\n'
main += """
string authorization = OperatingSystem.IsLinux() ? "MEOWCHAN_DESKTOP_SMOKE_XVFB" : "MEOWCHAN_DESKTOP_SMOKE_NATIVE_ALLOWED";
string? originalAuthorization = Environment.GetEnvironmentVariable(authorization);
try {
    Environment.SetEnvironmentVariable(authorization, null);
    if (SmokeScenarios.Runs["gif-ownership"].Precondition!() == null) throw new Exception("ownership authorization lost");
    Environment.SetEnvironmentVariable(authorization, "1");
    if (SmokeScenarios.Runs["gif-ownership"].Precondition!() != null) throw new Exception("authorized ownership refused");
} finally { Environment.SetEnvironmentVariable(authorization, originalAuthorization); }
"""
main += 'Console.WriteLine("PASS: 15 extracted dispatch routes execute exactly one expected port; no native GUI claim.");\n'
source = (main + ports + "internal static class SmokeScenarios {\n" + reader_dispatch
          + "internal static readonly Dictionary<string, SmokeRun> Runs = new() {\n"
          + "\n".join(bindings) + "\n};\n}\n")
with tempfile.TemporaryDirectory(prefix="reader-dispatch-contract-") as directory:
    temp = Path(directory)
    (temp / "Program.cs").write_text(source)
    (temp / "Dispatch.csproj").write_text(
        '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>'
        '<TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings>'
        '<Nullable>enable</Nullable></PropertyGroup></Project>')
    subprocess.run(["dotnet", "run", "--project", str(temp / "Dispatch.csproj"),
                    "--verbosity", "quiet"], check=True)
