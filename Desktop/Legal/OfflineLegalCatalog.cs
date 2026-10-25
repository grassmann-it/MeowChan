using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MeowChan.Desktop.Legal;

/// <summary>
/// Reads the notices shipped beside the executable. The package table in that same
/// file is the sole maintained component/version/copyright inventory.
/// </summary>
public sealed class OfflineLegalCatalog
{
    public sealed record Entry(string Name, string Version, string License, string Copyright,
        string Source, string FullText)
    {
        public override string ToString() => $"{Name} · {Version} · {License}";
    }

    private static readonly Regex PackageRow = new(
        @"^\| \[(?<name>[^]]+)\]\((?<source>[^)]+)\) \| (?<version>[^|]+) \| (?<license>[^|]+) \| (?<copyright>[^|]+) \|$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public IReadOnlyList<Entry> Entries { get; }
    public string Notices { get; }
    public string? Error { get; }

    private OfflineLegalCatalog(IReadOnlyList<Entry> entries, string notices, string? error)
    {
        Entries = entries;
        Notices = notices;
        Error = error;
    }

    public static OfflineLegalCatalog Load(string directory)
    {
        try
        {
            // Git checkouts and Windows packages can use CRLF. Section boundaries
            // are Markdown lines, independent of the host's newline convention.
            string notices = File.ReadAllText(Path.Combine(directory, "THIRD-PARTY-NOTICES.md"))
                .ReplaceLineEndings("\n");
            var entries = new List<Entry>();
            string ownLicense = ReadOptional(directory, "LICENSE");
            entries.Add(new Entry("MeowChan", "application", "Artistic License 2.0", "TomKa (grassmann-it)",
                "LICENSE · https://github.com/grassmann-it/MeowChan", ownLicense));

            foreach (string line in notices.Split('\n'))
            {
                Match match = PackageRow.Match(line.TrimEnd('\r'));
                if (!match.Success)
                    continue;
                string name = match.Groups["name"].Value;
                entries.Add(new Entry(name, match.Groups["version"].Value.Trim(),
                    match.Groups["license"].Value.Trim(), match.Groups["copyright"].Value.Trim(),
                    match.Groups["source"].Value.Trim(), SectionsFor(name, notices)));
            }
            string? runtimeRow = notices.Split('\n').FirstOrDefault(line =>
                line.StartsWith("| .NET runtime (", StringComparison.Ordinal));
            if (runtimeRow != null)
            {
                string[] cells = runtimeRow.Split('|');
                entries.Add(new Entry(cells[1].Trim(), cells[2].Trim(), cells[3].Trim(), cells[4].Trim(),
                    "THIRD-PARTY-NOTICES.md · Microsoft.NETCore.App.Runtime",
                    Section(notices, ".NET runtime — MIT") + "\n\n" + Section(notices, ".NET runtime — upstream third-party notices")));
            }

            string feather = Section(notices, "Feather Icons — MIT");
            entries.Add(new Entry("Feather Icons", "v4.29.2 (new SVG assets)", "MIT",
                "Copyright (c) 2013-2023 Cole Bemis",
                "Assets/icons/feather/LICENSE · https://github.com/feathericons/feather/tree/1b002399e8758fb2bccea4d07312dc9e0f43dcb8",
                feather));

            string videoIntro = Section(notices, "Gallery video dependencies (2026-10-20)");
            string lgpl = Section(notices, "VideoLAN / LibVLCSharp — LGPL 2.1");
            foreach (string line in videoIntro.Split('\n'))
            {
                string[] cells = line.Split('|');
                if (cells.Length < 4 || cells[1].Trim() is not ("LibVLCSharp" or "LibVLCSharp.Avalonia" or "VideoLAN.LibVLC.Windows" or "VideoLAN.LibVLC.Mac"))
                    continue;
                string name = cells[1].Trim();
                if (name == "VideoLAN.LibVLC.Windows" && !OperatingSystem.IsWindows() ||
                    name == "VideoLAN.LibVLC.Mac" && !OperatingSystem.IsMacOS())
                    continue;
                entries.Add(new Entry(name, cells[2].Trim(), name == "VideoLAN.LibVLC.Mac" ?
                    "LGPL-2.1-or-later core; retained GPL-2.0-or-later plugins" : "LGPL-2.1-or-later",
                    "VideoLAN and LibVLCSharp contributors",
                    name == "VideoLAN.LibVLC.Mac" ? "docs/native-video-macos-sources.md · docs/native-video-macos-source-manifest.json" :
                    "docs/native-video-sources.md · docs/native-video-source-manifest.json",
                    videoIntro + "\n\n" + lgpl +
                    (name == "VideoLAN.LibVLC.Mac" ? "\n\n" + ReadOptional(directory, "docs/native-video-macos-sources.md") +
                    "\n\n" + ReadOptional(directory, "docs/native-video-macos-COPYING") :
                    "\n\n" + ReadOptional(directory, "docs/native-video-sources.md"))));
            }

            // The collected native component texts and exact source manifests are
            // separate shipped documents, not replaced by a generic upstream URL.
            if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
                entries.Add(new Entry("VLC native component notices (Windows/macOS)", "3.0.24", "Various; see component texts",
                    "VideoLAN and component contributors", "docs/native-video-licenses.txt · docs/native-video-source-manifest.json",
                    ReadOptional(directory, "docs/native-video-licenses.txt")));
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                AddLinuxRuntime(entries, directory);
            return new OfflineLegalCatalog(entries.AsReadOnly(), notices, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or ArgumentException)
        {
            return new OfflineLegalCatalog(Array.Empty<Entry>(), "", exception.Message);
        }
    }

    public IReadOnlyList<Entry> Search(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Entries;
        string term = query.Trim();
        return Entries.Where(e => e.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            e.Version.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            e.License.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            e.Copyright.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            e.Source.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            e.FullText.Contains(term, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private static string ReadOptional(string root, string relative)
    {
        string path = Path.Combine(root, relative);
        return File.Exists(path) ? File.ReadAllText(path) : $"Local document missing: {relative}";
    }

    private static void AddLinuxRuntime(List<Entry> entries, string directory)
    {
        const string modeFile = "linux-video-runtime.json";
        string modePath = Path.Combine(directory, modeFile);
        string guidance = ReadOptional(directory, "docs/native-video-linux-sources.md");
        if (!File.Exists(modePath))
        {
            entries.Add(new Entry("Linux LibVLC package mode", "Not recorded", "See package inventory",
                "VideoLAN and distribution contributors", modeFile,
                "This local build has no Linux package mode file. Bundled native components cannot be inferred.\n\n" + guidance));
            return;
        }
        using JsonDocument mode = JsonDocument.Parse(File.ReadAllText(modePath));
        string? value = mode.RootElement.TryGetProperty("Mode", out JsonElement property) ? property.GetString() : null;
        if (value == "system")
        {
            entries.Add(new Entry("Linux LibVLC (host required)", "Host-installed VLC 3", "Host distribution terms",
                "VideoLAN and host distribution contributors", "linux-video-runtime.json · docs/native-video-linux-sources.md",
                "This package does not ship a native VLC tree or its distribution copyright inventory. Install compatible VLC 3 and consult the host distribution's license files.\n\n" + guidance));
            return;
        }
        if (value != "bundled")
        {
            entries.Add(new Entry("Linux LibVLC package mode", "Invalid package metadata", "See package inventory",
                "VideoLAN and distribution contributors", modeFile, "Unknown Linux package mode: " + value));
            return;
        }

        string inventoryPath = Path.Combine(directory, "linux-video-runtime-inventory.json");
        string licensesRoot = Path.Combine(directory, "linux-video-licenses");
        if (!File.Exists(inventoryPath) || !Directory.Exists(licensesRoot))
        {
            entries.Add(new Entry("Linux LibVLC (bundled)", "Inventory missing", "Per-component distribution licenses",
                "VideoLAN, Ubuntu and component contributors", "linux-video-runtime-inventory.json · linux-video-licenses/",
                "Bundled VLC license inventory or full local texts are missing from this package.\n\n" + guidance));
            return;
        }

        using JsonDocument inventory = JsonDocument.Parse(File.ReadAllText(inventoryPath));
        string version = inventory.RootElement.TryGetProperty("vlc_version", out JsonElement vlcVersion)
            ? vlcVersion.GetString() ?? "See inventory" : "See inventory";
        entries.Add(new Entry("Linux LibVLC (bundled)", version, "Per-component distribution licenses",
            "VideoLAN, Ubuntu and component contributors",
            "linux-video-runtime-inventory.json · docs/native-video-linux-sources.md · matching same-release source companion",
            guidance + "\n\n" + File.ReadAllText(inventoryPath)));

        // Packaging copies Debian copyright records and common license texts.
        // Bound file count/size, reject symlinks, and never follow paths outside
        // this packaged directory. A missing record remains visible as such.
        const int maxFiles = 512;
        const long maxTotalBytes = 16L * 1024 * 1024;
        const long maxFileBytes = 2L * 1024 * 1024;
        List<string> files = Directory.EnumerateFiles(licensesRoot, "*", new EnumerationOptions
            { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
            .Take(maxFiles + 1).ToList();
        if (files.Count > maxFiles)
            throw new InvalidDataException("Linux license inventory exceeds 512 files.");
        long total = 0;
        var content = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string path in files)
        {
            FileInfo file = new(path);
            if (file.LinkTarget != null || file.Length > maxFileBytes || (total += file.Length) > maxTotalBytes)
                throw new InvalidDataException("Linux license inventory is not a bounded set of regular local files.");
            string relative = Path.GetRelativePath(licensesRoot, path).Replace('\\', '/');
            content.Add(relative, File.ReadAllText(path));
        }
        if (inventory.RootElement.TryGetProperty("packages", out JsonElement packages))
            foreach (JsonElement package in packages.EnumerateArray())
            {
                string name = package.GetProperty("package").GetString()!;
                string packageVersion = package.GetProperty("version").GetString()!;
                string source = package.GetProperty("source").GetString()!;
                string sourceVersion = package.GetProperty("source_version").GetString()!;
                string relative = name.Replace(':', '_') + ".copyright";
                string fullText = content.TryGetValue(relative, out string? valueText)
                    ? valueText : "Local copyright record missing: " + relative;
                entries.Add(new Entry(name + " (bundled Linux)", packageVersion,
                    "See Debian copyright record", "See full Debian copyright record",
                    $"linux-video-licenses/{relative} · {source} {sourceVersion} · matching same-release source companion",
                    fullText));
            }
        foreach (KeyValuePair<string, string> file in content.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            if (!file.Key.EndsWith(".copyright", StringComparison.Ordinal))
                entries.Add(new Entry("Linux license text: " + file.Key, "packaged", "See full text",
                    "See full text", "linux-video-licenses/" + file.Key, file.Value));
    }

    private static string SectionsFor(string name, string notices)
    {
        string[] titles = name switch
        {
            var n when n.StartsWith("Semi.Avalonia", StringComparison.Ordinal) => ["Semi for Avalonia — MIT"],
            var n when n.StartsWith("Irihi.Ursa", StringComparison.Ordinal) => ["Ursa for Avalonia — MIT"],
            var n when n.StartsWith("Irihi.Avalonia.Shared", StringComparison.Ordinal) => ["Irihi Avalonia Shared — MIT"],
            "Avalonia.Fonts.Inter" => ["Inter fonts — SIL Open Font License 1.1", "Avalonia — MIT"],
            var n when n.StartsWith("Avalonia.Angle", StringComparison.Ordinal) => ["ANGLE Windows native assets — BSD license"],
            var n when n.StartsWith("Avalonia", StringComparison.Ordinal) => ["Avalonia — MIT", "Avalonia — upstream third-party notices"],
            var n when n.StartsWith("HarfBuzzSharp", StringComparison.Ordinal) || n.StartsWith("SkiaSharp", StringComparison.Ordinal)
                => ["SkiaSharp and HarfBuzzSharp — MIT", "SkiaSharp and HarfBuzzSharp — upstream third-party notices"],
            var n when n.StartsWith("Microsoft.Data.Sqlite", StringComparison.Ordinal) => ["Microsoft.Data.Sqlite — MIT"],
            var n when n.StartsWith("Microsoft.Extensions", StringComparison.Ordinal) => ["Microsoft.Extensions packages — MIT", "Microsoft.Extensions packages — upstream third-party notices"],
            var n when n.StartsWith("SQLitePCLRaw", StringComparison.Ordinal) => ["SQLitePCLRaw — Apache License 2.0"],
            var n when n.StartsWith("Serilog.Extensions", StringComparison.Ordinal) => ["Serilog.Extensions.Logging — Apache License 2.0"],
            var n when n.StartsWith("Serilog.Sinks.Console", StringComparison.Ordinal) => ["Serilog.Sinks.Console — Apache License 2.0"],
            var n when n.StartsWith("Serilog.Sinks.File", StringComparison.Ordinal) => ["Serilog.Sinks.File — Apache License 2.0"],
            var n when n.StartsWith("Serilog", StringComparison.Ordinal) => ["Serilog — Apache License 2.0"],
            "MicroCom.Runtime" => ["MicroCom.Runtime — MIT"],
            "Tmds.DBus.Protocol" => ["Tmds.DBus.Protocol — MIT"],
            "Newtonsoft.Json" => ["Newtonsoft.Json — MIT"],
            _ => []
        };
        if (name == "Avalonia.Controls.DataGrid")
            titles = ["Avalonia.Controls.DataGrid — license", "Avalonia — upstream third-party notices"];
        if (titles.Length == 0)
            return notices; // Inventory is still complete if a future package needs a new mapping.
        var text = new StringBuilder();
        foreach (string title in titles)
            text.AppendLine(Section(notices, title));
        return text.ToString();
    }

    private static string Section(string notices, string title)
    {
        string heading = "## " + title;
        int start = notices.IndexOf(heading + "\n", StringComparison.Ordinal);
        if (start < 0)
            return $"Section missing from THIRD-PARTY-NOTICES.md: {title}";
        int next = notices.IndexOf("\n## ", start + heading.Length, StringComparison.Ordinal);
        return notices.Substring(start, (next < 0 ? notices.Length : next) - start).Trim();
    }
}
