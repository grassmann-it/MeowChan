using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
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
            string notices = File.ReadAllText(Path.Combine(directory, "THIRD-PARTY-NOTICES.md"));
            var entries = new List<Entry>();
            string ownLicense = ReadOptional(directory, "LICENSE");
            entries.Add(new Entry("MeowChan", "application", "Artistic License 2.0", "Tomka (tfgrass)",
                "LICENSE · https://github.com/tfgrass/MeowChan", ownLicense));

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
            entries.Add(new Entry("VLC native component notices", "3.0.24", "Various; see component texts",
                "VideoLAN and component contributors", "docs/native-video-licenses.txt · docs/native-video-source-manifest.json",
                ReadOptional(directory, "docs/native-video-licenses.txt")));
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                entries.Add(new Entry("Linux LibVLC runtime", "See packaged linux-video-runtime-inventory.json",
                    "Per-component distribution licenses", "VideoLAN, Ubuntu and component contributors",
                    "docs/native-video-linux-sources.md · linux-video-runtime-inventory.json · linux-video-licenses/",
                    ReadOptional(directory, "docs/native-video-linux-sources.md") + "\n\n" +
                    ReadOptional(directory, "linux-video-runtime-inventory.json")));
            return new OfflineLegalCatalog(entries.AsReadOnly(), notices, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
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

    private static string SectionsFor(string name, string notices)
    {
        string[] titles = name switch
        {
            var n when n.StartsWith("Actipro", StringComparison.Ordinal) => ["Actipro Avalonia Free — proprietary EULA"],
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
