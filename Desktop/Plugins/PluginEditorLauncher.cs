using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MeowChan.Desktop.Plugins;

internal sealed record PluginEditorOptions(string? Executable, IReadOnlyList<string> Arguments);

/// <summary>Only host-owned editor configuration becomes executable input; source paths remain literal arguments.</summary>
internal static class PluginEditorLauncher
{
    internal static ProcessStartInfo CreateStartInfo(PluginEditorOptions options, string path, int line = 1, int column = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        if (string.IsNullOrWhiteSpace(options.Executable)) return ExternalLauncher.CreateStartInfo(fullPath);
        if (options.Arguments.Count > 32 || options.Arguments.Any(argument => argument.Length > 4096))
            throw new ArgumentException("Editor arguments exceed the allowed limits.");
        ProcessStartInfo start = new() { FileName = options.Executable, UseShellExecute = false };
        ExternalLauncher.RemoveSnapRuntimePaths(start);
        bool hasFile = false;
        foreach (string argument in options.Arguments)
        {
            hasFile |= argument.Contains("{file}", StringComparison.Ordinal);
            string expanded = Regex.Replace(argument, "\\{(file|line|column)\\}", match => match.Groups[1].Value switch
            {
                "file" => fullPath,
                "line" => Math.Max(1, line).ToString(CultureInfo.InvariantCulture),
                _ => Math.Max(1, column).ToString(CultureInfo.InvariantCulture)
            });
            start.ArgumentList.Add(expanded);
        }
        if (!hasFile) throw new ArgumentException("The editor argument template must include {file}.");
        return start;
    }

    internal static async Task OpenAsync(PluginEditorOptions options, string path, int line = 1, int column = 1,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) throw new FileNotFoundException(Localization.Texts.T("plugins.fileMissing"), path);
        ProcessStartInfo info = CreateStartInfo(options, path, line, column);
        cancellationToken.ThrowIfCancellationRequested();
        using Process? process = Process.Start(info);
        if (info.UseShellExecute) return;
        if (process == null) throw new InvalidOperationException(Localization.Texts.T("plugins.editorFailed"));
        using CancellationTokenSource observation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        observation.CancelAfter(TimeSpan.FromMilliseconds(750));
        try { await process.WaitForExitAsync(observation.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return; }
        if (process.HasExited && process.ExitCode != 0)
            throw new Win32Exception(Localization.Texts.T("plugins.editorExit", process.ExitCode));
        // A running editor belongs to its user, never kill it on page cancellation.
    }
}
