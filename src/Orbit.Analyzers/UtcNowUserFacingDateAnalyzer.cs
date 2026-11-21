using System;
using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Orbit.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UtcNowUserFacingDateAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "ORBIT0004";

    private static readonly DiagnosticDescriptor DateOnlyRule = new(
        DiagnosticId,
        "Calendar date derived from DateTime.UtcNow",
        "Do not derive a DateOnly from DateTime.UtcNow. The server's UTC date is not the user's date; use IUserDateService.GetUserTodayAsync(userId), or convert via TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, userTimeZone) first.",
        "Reliability",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "DateOnly.FromDateTime(DateTime.UtcNow) collapses the UTC instant to the server's calendar date, which disagrees with the user's date around midnight in their timezone.");

    private static readonly ImmutableHashSet<string> CoveredAssemblies = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "Orbit.Api",
        "Orbit.Application",
        "Orbit.Domain",
        "Orbit.Infrastructure");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(DateOnlyRule);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        var assemblyName = context.Compilation.AssemblyName;
        if (assemblyName is null || !CoveredAssemblies.Contains(assemblyName))
            return;

        var dateOnlyType = context.Compilation.GetTypeByMetadataName("System.DateOnly");

        context.RegisterSyntaxNodeAction(
            nodeContext => AnalyzeMemberAccess(nodeContext, dateOnlyType),
            SyntaxKind.SimpleMemberAccessExpression);
    }

    private static void AnalyzeMemberAccess(
        SyntaxNodeAnalysisContext context,
        INamedTypeSymbol? dateOnlyType)
    {
        var memberAccess = (MemberAccessExpressionSyntax)context.Node;
        if (memberAccess.Name.Identifier.ValueText != "UtcNow")
            return;
        if (IsExcludedPath(memberAccess.SyntaxTree.FilePath))
            return;
        if (!IsDateTimeUtcNow(memberAccess, context.SemanticModel, context.CancellationToken))
            return;

        var consumingInvocation = FindConsumingInvocation(memberAccess);
        if (consumingInvocation is null || dateOnlyType is null || !IsMethodNamed(consumingInvocation, "FromDateTime"))
            return;

        var consumerType = ResolveMethodContainingType(consumingInvocation, context.SemanticModel, context.CancellationToken);
        if (SymbolEqualityComparer.Default.Equals(consumerType, dateOnlyType))
            context.ReportDiagnostic(Diagnostic.Create(DateOnlyRule, consumingInvocation.GetLocation()));
    }

    private static bool IsDateTimeUtcNow(
        MemberAccessExpressionSyntax memberAccess, SemanticModel semanticModel, CancellationToken cancellationToken)
    {
        return semanticModel.GetSymbolInfo(memberAccess, cancellationToken).Symbol is IPropertySymbol property
            && property.ContainingType?.SpecialType == SpecialType.System_DateTime;
    }

    /// <summary>
    /// Walks up from the <c>DateTime.UtcNow</c> access through the member-access/invocation chain it
    /// roots (for example <c>DateTime.UtcNow.AddDays(-1)</c>) and returns the invocation that consumes
    /// the chain as an argument, or null when the value is not a direct argument to a call.
    /// </summary>
    private static InvocationExpressionSyntax? FindConsumingInvocation(MemberAccessExpressionSyntax memberAccess)
    {
        SyntaxNode node = memberAccess;
        while (true)
        {
            switch (node.Parent)
            {
                case MemberAccessExpressionSyntax parentAccess when parentAccess.Expression == node:
                    node = parentAccess;
                    continue;
                case InvocationExpressionSyntax parentInvocation when parentInvocation.Expression == node:
                    node = parentInvocation;
                    continue;
                case ParenthesizedExpressionSyntax parenthesized:
                    node = parenthesized;
                    continue;
                case ArgumentSyntax { Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax consumer } }:
                    return consumer;
                default:
                    return null;
            }
        }
    }

    private static INamedTypeSymbol? ResolveMethodContainingType(
        InvocationExpressionSyntax invocation, SemanticModel semanticModel, CancellationToken cancellationToken)
    {
        return semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol is IMethodSymbol method
            ? method.ContainingType
            : null;
    }

    private static bool IsMethodNamed(InvocationExpressionSyntax invocation, string name)
    {
        return invocation.Expression is MemberAccessExpressionSyntax memberAccess
            && memberAccess.Name.Identifier.ValueText == name;
    }

    private static bool IsExcludedPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        var normalized = path.Replace('\\', '/');
        return normalized.IndexOf("/Migrations/", StringComparison.OrdinalIgnoreCase) >= 0
            || normalized.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase);
    }
}
