using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using MeowChan.Build;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

string root = Path.GetTempPath();
string sourcePath = Path.Combine(root, "Probe.cs");
string baselinePath = Path.Combine(root, "build", "analyzer-baseline-2026-11-17.tsv");
const string source = "class Existing { }\n";
string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
string[] rows = ProbeAnalyzer.Ids.Select(id => string.Join('\t', "Probe", "Probe.cs", Hash(source), id,
    "1", "7", "1", "15", Hash("Debt Existing"))).ToArray();

async Task Check(string name, int expected, string code = source, string? baseline = null,
    string assembly = "Probe", string? path = null, string message = "Debt {0}", bool missing = false)
{
    var tree = CSharpSyntaxTree.ParseText(code, path: path ?? sourcePath);
    var compilation = CSharpCompilation.Create(assembly, [tree],
        [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
            specificDiagnosticOptions: ProbeAnalyzer.Ids.ToDictionary(id => id, _ => ReportDiagnostic.Error)));
    var options = new AnalyzerOptions(missing ? [] :
        [new TextFile(baselinePath, baseline ?? string.Join('\n', rows))]);
    var diagnostics = await compilation.WithAnalyzers(
        [new ProbeAnalyzer(message), new DebtSuppressor()], options).GetAnalyzerDiagnosticsAsync();
    var errors = diagnostics.Where(d => !d.IsSuppressed && d.Severity == DiagnosticSeverity.Error).ToArray();
    if (errors.Length != expected || diagnostics.Any(d => d.Id == "AD0001"))
        throw new InvalidOperationException($"{name}: expected {expected} errors, got {errors.Length}: {string.Join("; ", diagnostics)}");
    Console.WriteLine($"PASS {name}: {errors.Length} errors");
}

await Check("unchanged exact baseline", 0);
await Check("new file", 4, path: Path.Combine(root, "New.cs"));
await Check("new symbol in baselined file fails closed", 8, code: source + "class New { }\n");
await Check("edited source fails closed", 4, code: source + "// edited\n");
await Check("different assembly", 4, assembly: "Other");
await Check("changed diagnostic message", 4, message: "Different {0}");
await Check("missing baseline", 4, missing: true);
await Check("pruned entry becomes error", 1, baseline: string.Join('\n', rows.Skip(1)));
await Check("CRLF checkout matches LF hash", 0, code: source.Replace("\n", "\r\n"));
Console.WriteLine("All 9 baseline compiler tests passed.");

sealed class TextFile(string path, string text) : AdditionalText
{
    public override string Path => path;
    public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(text);
}

[DiagnosticAnalyzer(LanguageNames.CSharp)]
sealed class ProbeAnalyzer(string message) : DiagnosticAnalyzer
{
    internal static readonly string[] Ids = ["CA2000", "CA2213", "CA1001", "CA2016"];
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        Ids.Select(id => new DiagnosticDescriptor(id, "Debt", message, "Test", DiagnosticSeverity.Warning, true)).ToImmutableArray();
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(node =>
        {
            var declaration = (ClassDeclarationSyntax)node.Node;
            foreach (var descriptor in SupportedDiagnostics)
                node.ReportDiagnostic(Diagnostic.Create(descriptor, declaration.Identifier.GetLocation(), declaration.Identifier.ValueText));
        }, SyntaxKind.ClassDeclaration);
    }
}
