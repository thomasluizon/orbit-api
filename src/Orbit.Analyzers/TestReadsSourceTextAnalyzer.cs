using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Orbit.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class TestReadsSourceTextAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "ORBIT0005";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Test reads a C# source file as text",
        "This test reads a C# source file as text, so it checks structure, not behaviour (CODING_STANDARDS.md LIE-2). Call the code, or move a repository-wide guard into a Guards folder.",
        "Reliability",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A test that reads source text breaks on a safe refactor and passes when the behaviour is wrong.");

    private static readonly ImmutableHashSet<string> TextReadMethods = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "ReadAllText",
        "ReadAllTextAsync",
        "ReadAllLines",
        "ReadAllLinesAsync",
        "ReadLines");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        if (!TestAssemblyName.Matches(context.Compilation.AssemblyName))
            return;

        var fileType = context.Compilation.GetTypeByMetadataName("System.IO.File");
        if (fileType is null)
            return;

        context.RegisterSyntaxNodeAction(
            nodeContext => AnalyzeInvocation(nodeContext, fileType),
            SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context, INamedTypeSymbol fileType)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.ArgumentList.Arguments.Count == 0 || IsInGuardsFolder(invocation.SyntaxTree.FilePath))
            return;

        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method
            || !TextReadMethods.Contains(method.Name)
            || !SymbolEqualityComparer.Default.Equals(method.ContainingType, fileType))
        {
            return;
        }

        var path = invocation.ArgumentList.Arguments[0].Expression;
        if (NamesSourceFile(path, context.SemanticModel, context.CancellationToken))
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }

    private static bool NamesSourceFile(
        ExpressionSyntax path, SemanticModel semanticModel, CancellationToken cancellationToken)
    {
        if (HoldsSourceFileLiteral(path))
            return true;

        return semanticModel.GetSymbolInfo(path, cancellationToken).Symbol is ILocalSymbol local
            && local.DeclaringSyntaxReferences.Any(reference =>
                reference.GetSyntax(cancellationToken) is VariableDeclaratorSyntax { Initializer: { } initializer }
                && HoldsSourceFileLiteral(initializer.Value));
    }

    private static bool HoldsSourceFileLiteral(ExpressionSyntax expression)
    {
        return expression.DescendantNodesAndSelf()
            .OfType<LiteralExpressionSyntax>()
            .Any(literal => literal.IsKind(SyntaxKind.StringLiteralExpression)
                && literal.Token.ValueText.EndsWith(".cs", StringComparison.Ordinal));
    }

    private static bool IsInGuardsFolder(string path) =>
        path.Replace('\\', '/').IndexOf("/Guards/", StringComparison.Ordinal) >= 0;
}
