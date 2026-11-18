using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace MeowChan.Build;

/// <summary>Temporary UX-LEDGER-22 baseline, frozen 2026-11-17; remove with UX-LEDGER-23.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DebtSuppressor : DiagnosticSuppressor
{
    private static readonly ImmutableArray<SuppressionDescriptor> Rules =
        [Rule("CA2000"), Rule("CA2213"), Rule("CA1001"), Rule("CA2016")];

    private static SuppressionDescriptor Rule(string id) => new("UX22" + id, id,
        "Existing debt frozen 2026-11-17 (UX-LEDGER-23); exact diagnostic in an unchanged source file only.");

    public override ImmutableArray<SuppressionDescriptor> SupportedSuppressions => Rules;

    public override void ReportSuppressions(SuppressionAnalysisContext context)
    {
        AdditionalText? baseline = context.Options.AdditionalFiles.SingleOrDefault(file =>
            Path.GetFileName(file.Path) == "analyzer-baseline-2026-11-17.tsv");
        if (baseline is null) return; // Missing baseline fails closed: all four remain errors.
        string root = Path.GetDirectoryName(Path.GetDirectoryName(baseline.Path))!;
        var entries = (baseline.GetText(context.CancellationToken)?.ToString() ?? "")
            .Split('\n').Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(line => line.TrimEnd('\r')).ToHashSet(StringComparer.Ordinal);
        Dictionary<SyntaxTree, string> hashes = new();
        foreach (Diagnostic diagnostic in context.ReportedDiagnostics)
        {
            SuppressionDescriptor? rule = Rules.FirstOrDefault(rule => rule.SuppressedDiagnosticId == diagnostic.Id);
            SyntaxTree? tree = diagnostic.Location.SourceTree;
            if (rule is null || tree is null) continue;
            if (!hashes.TryGetValue(tree, out string? hash))
            {
                hash = Hash(tree.GetText(context.CancellationToken).ToString().Replace("\r\n", "\n"));
                hashes.Add(tree, hash);
            }
            FileLinePositionSpan span = diagnostic.Location.GetLineSpan();
            string key = string.Join('\t', context.Compilation.AssemblyName,
                Path.GetRelativePath(root, tree.FilePath).Replace('\\', '/'), hash, diagnostic.Id,
                (span.StartLinePosition.Line + 1).ToString(CultureInfo.InvariantCulture),
                (span.StartLinePosition.Character + 1).ToString(CultureInfo.InvariantCulture),
                (span.EndLinePosition.Line + 1).ToString(CultureInfo.InvariantCulture),
                (span.EndLinePosition.Character + 1).ToString(CultureInfo.InvariantCulture),
                Hash(diagnostic.GetMessage(CultureInfo.InvariantCulture)));
            if (entries.Contains(key)) context.ReportSuppression(Suppression.Create(rule, diagnostic));
        }
    }

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
