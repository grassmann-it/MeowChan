using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace MeowChan.DesktopTests;

internal static class CSharpSource
{
    internal static SyntaxNode Parse(string source) => CSharpSyntaxTree.ParseText(source).GetRoot();

    internal static MethodDeclarationSyntax Method(this SyntaxNode source, string name) =>
        Assert.Single(source.DescendantNodes().OfType<MethodDeclarationSyntax>(), method => method.Identifier.ValueText == name);

    internal static IEnumerable<InvocationExpressionSyntax> Calls(this SyntaxNode source, string name, string? receiver = null) =>
        source.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Where(call => call.Expression switch
        {
            SimpleNameSyntax method => receiver == null && method.Identifier.ValueText == name,
            MemberAccessExpressionSyntax method => method.Name.Identifier.ValueText == name
                && (receiver == null || method.Expression is IdentifierNameSyntax target && target.Identifier.ValueText == receiver),
            _ => false
        });
}
