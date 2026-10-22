using System.Globalization;
using System.Text;
using System.Text.Json;
using MeowChan.PluginHost.Compilation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

namespace MeowChan.PluginCompiler;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args is ["--write-toolchain-manifest", string directory])
        {
            await WriteManifestAsync(Path.GetFullPath(directory));
            return 0;
        }
        if (args is not ["--compile", string toolchainDirectory]) return 2;
        CompilerResponse response;
        try
        {
            using MemoryStream input = new();
            Stream standardInput = Console.OpenStandardInput();
            byte[] buffer = new byte[65536];
            int count;
            while ((count = await standardInput.ReadAsync(buffer)) != 0)
            {
                if (input.Length + count > CompilerWire.MaximumRequestBytes) throw new InvalidDataException("Compiler request is too large.");
                input.Write(buffer, 0, count);
            }
            CompilerRequest request = JsonSerializer.Deserialize<CompilerRequest>(input.ToArray(), CompilerWire.JsonOptions)
                ?? throw new InvalidDataException("Missing compiler request.");
            response = await CompileAsync(request, toolchainDirectory);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException or BadImageFormatException)
        {
            response = new(false, "ReferenceError", [new("MCREF", exception.Message, null, null, null, "error")]);
        }
        await Console.Out.WriteAsync(JsonSerializer.Serialize(response, CompilerWire.JsonOptions));
        return response.Success ? 0 : 1;
    }

    private static async Task<CompilerResponse> CompileAsync(CompilerRequest request, string directory)
    {
        if (request.Source is null || request.Source.Length is 0 or > CompilerWire.MaximumSourceBytes ||
            !CompilerWire.IsHash(request.CompilationFingerprint) || !Path.IsPathFullyQualified(request.OutputPath) ||
            !Path.IsPathFullyQualified(request.OriginalPath)) throw new InvalidDataException("Invalid compiler request.");
        ToolchainManifest manifest = await CompilerWire.ReadToolchainAsync(directory, CancellationToken.None);
        if (CompilerWire.ToolchainHash(manifest) != request.ToolchainFingerprint ||
            CompilerWire.HashParts(CompilerWire.Hash(request.Source), request.ToolchainFingerprint) != request.CompilationFingerprint)
            throw new InvalidDataException("Compiler snapshot fingerprint mismatch.");
        List<MetadataReference> references = new();
        foreach (ToolchainFile file in manifest.Files.Where(f => f.Role is "sdk" or "framework"))
        {
            byte[] bytes = await CompilerWire.ReadBoundedAsync(CompilerWire.ResolvePath(directory, file.Path), 64 * 1024 * 1024, CancellationToken.None);
            if (CompilerWire.Hash(bytes) != file.Sha256) throw new InvalidDataException($"Reference changed while compiling: {file.Path}");
            references.Add(MetadataReference.CreateFromImage(bytes));
        }
        // SourceText reads the same immutable snapshot that the host fingerprinted; no file reopen, analyzer, generator, project, or resolver.
        using MemoryStream sourceStream = new(request.Source, false);
        SourceText source = SourceText.From(sourceStream, encoding: new UTF8Encoding(false, true), checksumAlgorithm: SourceHashAlgorithm.Sha256);
        SyntaxTree syntax = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp14), "/plugin/source.cs");
        CSharpCompilation compilation = CSharpCompilation.Create("MeowChan.Plugin_" + request.CompilationFingerprint,
            [syntax], references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release, allowUnsafe: false, deterministic: true,
                nullableContextOptions: NullableContextOptions.Enable, concurrentBuild: false));
        await using FileStream output = new(request.OutputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        EmitResult result = compilation.Emit(output);
        CompilerWireDiagnostic[] diagnostics = result.Diagnostics.Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
            .OrderByDescending(d => d.Severity).Take(100).Select(d => ToDiagnostic(d, request.OriginalPath)).ToArray();
        return new(result.Success, result.Success ? null : "SourceError", diagnostics);
    }

    private static CompilerWireDiagnostic ToDiagnostic(Diagnostic diagnostic, string originalPath)
    {
        FileLinePositionSpan location = diagnostic.Location.GetLineSpan();
        return new(diagnostic.Id, diagnostic.GetMessage(CultureInfo.InvariantCulture),
            diagnostic.Location.IsInSource ? originalPath : null,
            diagnostic.Location.IsInSource ? location.StartLinePosition.Line + 1 : null,
            diagnostic.Location.IsInSource ? location.StartLinePosition.Character + 1 : null,
            diagnostic.Severity == DiagnosticSeverity.Warning ? "warning" : "error");
    }

    private static async Task WriteManifestAsync(string directory)
    {
        List<ToolchainFile> files = new();
        foreach (string path in new[] { "MeowChan.PluginCompiler.dll", "Microsoft.CodeAnalysis.dll", "Microsoft.CodeAnalysis.CSharp.dll" })
            files.Add(new(path, CompilerWire.Hash(await File.ReadAllBytesAsync(Path.Combine(directory, path))), "compiler"));
        const string sdkPath = "MeowChan.PluginSdk.dll";
        files.Add(new(sdkPath, CompilerWire.Hash(await File.ReadAllBytesAsync(Path.Combine(directory, sdkPath))), "sdk"));
        foreach (string path in Directory.EnumerateFiles(Path.Combine(directory, "references"), "*.dll").Order(StringComparer.Ordinal))
            files.Add(new("references/" + Path.GetFileName(path), CompilerWire.Hash(await File.ReadAllBytesAsync(path)), "framework"));
        if (files.Count < 5) throw new InvalidDataException("No delivered framework references were found.");
        ToolchainManifest manifest = new(1, "net10.0", CompilerWire.Options,
            CompilerWire.FileSetHash(files.Where(f => f.Role == "compiler")), files.Single(f => f.Role == "sdk").Sha256,
            CompilerWire.FileSetHash(files.Where(f => f.Role != "compiler")), files.ToArray());
        await File.WriteAllTextAsync(Path.Combine(directory, "toolchain.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }
}
