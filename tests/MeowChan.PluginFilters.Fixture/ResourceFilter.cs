using System.Diagnostics;
using MeowChan.PluginSdk;
namespace MeowChan.PluginFilters.Fixture;

public sealed class ResourceFilter : IImageFilterPlugin
{
    public PluginDescription Describe() => new("fixture.resources", "Resource filter fixture", "1.0.0", SdkContract.Version,
        "Own original copy and bundled helper, no OCR-specific host API.", [],
        [new("resource", "Resource helper", "Checks exact bytes through a private helper", FilterDataAccess.OriginalImage | FilterDataAccess.CandidateMetadata,
            new("1", [new("mode", SettingScope.FilterRule, SettingKind.Text, "Mode")]), ["image/png"])], new("1", []), []);
    public async Task<ImageFilterResult> EvaluateAsync(ImageFilterRequest request, PluginCallContext context, CancellationToken cancellationToken = default)
    {
        string mode = request.RuleSettings.Values.FirstOrDefault(value => value.Key == "mode")?.Text ?? "match";
        if (mode == "crash") Environment.Exit(67);
        string copy = Path.Combine(Environment.CurrentDirectory, "original.bin");
        try
        {
            await using (FileStream output = new(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                for (long offset = 0; offset < request.OriginalSize;)
                {
                    ImageAssetChunk chunk = await context.Images.ReadImageAsync(request.AssetToken, offset,
                        (int)Math.Min(SdkLimits.MaximumImageChunkBytes, request.OriginalSize - offset), cancellationToken);
                    await output.WriteAsync(chunk.Bytes, cancellationToken); offset += chunk.Bytes.Length;
                }
            string helper = context.Resources.ResolvePath("helper") ?? throw new InvalidOperationException("Missing bundled helper.");
            using Process child = new() { StartInfo = new(helper) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true } };
            child.StartInfo.ArgumentList.Add(copy); child.StartInfo.ArgumentList.Add(mode); child.Start();
            try
            {
                string output = await child.StandardOutput.ReadToEndAsync(cancellationToken);
                await child.WaitForExitAsync(cancellationToken);
                return new(child.ExitCode == 0 && output.Trim().Equals(request.OriginalSha256, StringComparison.OrdinalIgnoreCase)
                    ? ImageFilterOutcome.Match : ImageFilterOutcome.Error, "resource_helper_bytes");
            }
            finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); } }
        }
        finally { File.Delete(copy); File.Delete(Path.Combine(Environment.CurrentDirectory, "helper-pids")); File.Delete(Path.Combine(Environment.CurrentDirectory, "helper-copy.bin")); }
    }
}
